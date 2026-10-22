using System.Diagnostics;
using System.Text.Json;

namespace AicountsManager;

internal sealed record TokenDay(string Date, IReadOnlyDictionary<string, long> Models)
{
    public long Total => Models.Values.Aggregate(0L, (a, b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b);
}
internal sealed record TokenHistorySnapshot(IReadOnlyList<TokenDay> Days, bool Partial, string? UpdatedAt, string? Error, bool CatchingUp = false, IReadOnlyDictionary<string, TokenHistorySnapshot>? Sources = null)
{
    public static TokenHistorySnapshot Empty { get; } = new([], false, null, null);
}

// Counts local observations once across account changes. No network, pricing, transcripts or auth data in the cache.
internal sealed class TokenHistory
{
    internal const int RetentionDays = 365;
    private const int CacheVersion = 1, MaxLineBytes = 256 * 1024, MaxFiles = 20000;
    private readonly string _home, _cachePath;
    private readonly TimeZoneInfo _zone;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HistoryCache _cache = new();
    private bool _loaded;
    // At most one unfinished line in RAM. It is never serialized (it may be conversation text).
    private byte[] _pending = [];
    private FileCursor? _pendingOwner;
    private TokenHistorySnapshot _snapshot = TokenHistorySnapshot.Empty;
    public TokenHistorySnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal long LastBytesRead { get; private set; }
    public TokenHistory(string home, string cachePath, TimeZoneInfo? zone = null)
    { _home = home; _cachePath = cachePath; _zone = zone ?? TimeZoneInfo.Local; }

    public async Task<TokenHistorySnapshot> RefreshAsync(CancellationToken cancellationToken = default,
        int byteBudget = 16 * 1024 * 1024, int milliseconds = 1500)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return Snapshot;
        try { return await Task.Run(() => Scan(cancellationToken, byteBudget, milliseconds), cancellationToken); }
        finally { _gate.Release(); }
    }

    private TokenHistorySnapshot Scan(CancellationToken token, int byteBudget, int milliseconds)
    {
        LastBytesRead = 0;
        var watch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var cutoff = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime).AddDays(1 - RetentionDays).ToString("yyyy-MM-dd");
        bool partial = false, changed = false, catchingUp = false;
        string? error = null;
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                try
                {
                    if (File.Exists(_cachePath) && new FileInfo(_cachePath).Length < 24 * 1024 * 1024)
                    {
                        using var input = File.OpenRead(_cachePath);
                        var saved = JsonSerializer.Deserialize<HistoryCache>(input);
                        if (saved is { Version: CacheVersion } && saved.Zone == _zone.Id && saved.Files is not null && saved.Files.Count <= MaxFiles && saved.Files.Values.All(ValidCursor)) _cache = saved;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { error = "Reconstruyendo el índice local."; }
                _cache.Zone = _zone.Id;
            }
            var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var folder in new[] { "sessions", "archived_sessions" })
            {
                var root = Path.Combine(_home, folder);
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", options))
                    {
                        token.ThrowIfCancellationRequested();
                        var file = new FileInfo(path);
                        if (file.LastWriteTimeUtc < now.UtcDateTime.AddDays(-RetentionDays)) continue;
                        // A move to archived_sessions is the same source, not new consumption.
                        if (!files.TryGetValue(file.Name, out var other) || file.LastWriteTimeUtc > other.LastWriteTimeUtc) files[file.Name] = file;
                        if (files.Count >= MaxFiles) { partial = true; break; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; error = "Parte del historial no se puede leer."; }
            }
            foreach (var pair in files.OrderByDescending(p => p.Value.LastWriteTimeUtc))
            {
                token.ThrowIfCancellationRequested();
                var file = pair.Value;
                if (!_cache.Files.TryGetValue(pair.Key, out var state))
                {
                    if (_cache.Files.Count >= MaxFiles) { partial = true; continue; }
                    _cache.Files[pair.Key] = state = new FileCursor(); changed = true;
                }
                if (state.Length == file.Length && state.Modified == file.LastWriteTimeUtc.Ticks && state.Completed) continue;
                if (LastBytesRead >= byteBudget || watch.ElapsedMilliseconds >= milliseconds) { partial = catchingUp = true; continue; }
                try
                {
                    var prefix = Prefix(file.FullName);
                    if (file.Length < state.Offset || (state.Prefix is not null && state.Prefix != prefix)
                        || (state.Completed && file.Length == state.Length && state.Modified != file.LastWriteTimeUtc.Ticks))
                        _cache.Files[pair.Key] = state = new FileCursor();
                    state.Prefix = prefix;
                    Read(file, state, token, watch, byteBudget, milliseconds, cutoff);
                    changed = true;
                    if (!state.Completed) partial = catchingUp = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; error = "Parte del historial no se puede leer."; }
            }
            foreach (var key in _cache.Files.Keys.ToArray())
            {
                var state = _cache.Files[key];
                foreach (var day in state.Days.Keys.Where(day => string.CompareOrdinal(day, cutoff) < 0).ToArray()) { state.Days.Remove(day); changed = true; }
                if (!files.ContainsKey(key) && state.Days.Count == 0) { _cache.Files.Remove(key); changed = true; }
            }
            if (changed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                var data = JsonSerializer.SerializeToUtf8Bytes(_cache);
                if (data.Length <= 24 * 1024 * 1024) AtomicFile.WriteBytes(_cachePath, data);
                else { partial = true; error = "El índice local ha alcanzado su límite de tamaño."; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        { partial = true; error = "No se ha podido completar la lectura del historial local."; }
        var days = new SortedDictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var state in _cache.Files.Values)
            foreach (var day in state.Days)
            {
                if (!days.TryGetValue(day.Key, out var models)) days[day.Key] = models = new(StringComparer.Ordinal);
                foreach (var model in day.Value) models[model.Key] = SaturatingAdd(models.GetValueOrDefault(model.Key), model.Value);
            }
        partial |= _cache.Files.Values.Any(s => s.Uncertain);
        var snapshot = new TokenHistorySnapshot(days.Select(p => new TokenDay(p.Key, p.Value)).ToArray(), partial, now.ToString("O"), error, catchingUp);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    private static bool ValidCursor(FileCursor s) => s is not null && s.Offset >= 0 && s.Length >= 0
        && s.Model is not null && s.Days is not null && s.Days.Count <= RetentionDays
        && s.Days.All(d => DateOnly.TryParseExact(d.Key, "yyyy-MM-dd", out _) && d.Value is not null
            && d.Value.Count <= 65 && d.Value.All(m => m.Value >= 0 && m.Key.Length <= 120));

    private static string Prefix(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Only the immutable first metadata record identifies a rewrite. Small appended files keep the same prefix.
        var bytes = new List<byte>(1024);
        for (int b; bytes.Count < 1024 && (b = file.ReadByte()) >= 0 && b != 10;) bytes.Add((byte)b);
        return AuthHash.Bytes(bytes.ToArray());
    }

    private void Read(FileInfo file, FileCursor state, CancellationToken token, Stopwatch watch, int budget, int milliseconds, string cutoff)
    {
        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        var pending = ReferenceEquals(_pendingOwner, state) ? _pending : [];
        // If cancellation interrupts this read, the committed newline offset is authoritative.
        _pendingOwner = null; _pending = [];
        input.Position = state.Offset + pending.Length;
        var buffer = new byte[65536];
        using var line = new MemoryStream();
        line.Write(pending);
        var position = input.Position;
        bool discard = state.DiscardLine;
        while (LastBytesRead < budget && watch.ElapsedMilliseconds < milliseconds)
        {
            token.ThrowIfCancellationRequested();
            int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, budget - LastBytesRead));
            if (n == 0) break;
            LastBytesRead += n;
            for (int i = 0; i < n; i++)
            {
                position++;
                if (buffer[i] == 10)
                {
                    if (!discard) Consume(line.GetBuffer().AsSpan(0, (int)line.Length), state, cutoff);
                    line.SetLength(0); discard = false; state.DiscardLine = false; state.Offset = position;
                }
                else if (!discard)
                {
                    if (line.Length >= MaxLineBytes)
                    {
                        var prefix = line.GetBuffer().AsSpan(0, Math.Min((int)line.Length, 1024));
                        if (prefix.IndexOf("\"session_meta\""u8) >= 0) { state.MetadataUnavailable = true; state.Uncertain = true; }
                        if (prefix.IndexOf("\"turn_context\""u8) >= 0) { state.Model = "Sin modelo"; state.Uncertain = true; }
                        discard = true; state.DiscardLine = true; line.SetLength(0);
                    }
                    else line.WriteByte(buffer[i]);
                }
                if (discard) state.Offset = position;
            }
        }
        state.DiscardLine = discard;
        _pendingOwner = state;
        _pending = discard ? [] : line.ToArray();
        state.Length = file.Length; state.Modified = file.LastWriteTimeUtc.Ticks;
        // An incomplete final line is left for the next append, without repeatedly scheduling catch-up.
        state.Completed = input.Position >= input.Length;
    }

    private void Consume(ReadOnlySpan<byte> bytes, FileCursor state, string cutoff)
    {
        // Most bytes are conversation text. Do not parse or retain those records.
        if (bytes.IndexOf("\"token_count\""u8) < 0 && bytes.IndexOf("\"turn_context\""u8) < 0 && bytes.IndexOf("\"session_meta\""u8) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(bytes.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) return;
            var type = String(root, "type");
            if (type == "session_meta")
            {
                state.Inherited = Number(p, "subagent_history_start_ordinal") ?? Number(p, "forked_from_ordinal_exclusive");
                state.HasBase = (p.TryGetProperty("history_base", out var historyBase) && historyBase.ValueKind == JsonValueKind.Object) || String(p, "forked_from_id") is not null;
                return;
            }
            if (type == "turn_context") { state.Model = String(p, "model") ?? "Sin modelo"; return; }
            if (type != "event_msg" || String(p, "type") != "token_count" || !p.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return;
            var total = info.TryGetProperty("total_token_usage", out var totalNode) ? Number(totalNode, "total_tokens") : null;
            var last = info.TryGetProperty("last_token_usage", out var lastNode) ? Number(lastNode, "total_tokens") : null;
            var ordinal = Number(root, "ordinal");
            bool inherited = state.Inherited.HasValue && ordinal.HasValue && ordinal < state.Inherited;
            long delta = 0;
            if (total is >= 0)
            {
                if (state.Previous is long previous) delta = total >= previous ? total.Value - previous : Math.Max(0, last ?? 0);
                else delta = state.HasBase ? Math.Max(0, last ?? 0) : total.Value;
                state.Previous = total;
            }
            // A row without a cumulative total cannot be reliably deduplicated: keep coverage explicitly partial.
            else { state.Uncertain = true; return; }
            if (state.MetadataUnavailable || inherited || delta <= 0) return;
            if (!DateTimeOffset.TryParse(String(root, "timestamp"), out var timestamp)) { state.Uncertain = true; return; }
            var date = TimeZoneInfo.ConvertTime(timestamp, _zone).ToString("yyyy-MM-dd");
            if (string.CompareOrdinal(date, cutoff) < 0 || timestamp > DateTimeOffset.UtcNow.AddMinutes(5)) return;
            if (!state.Days.TryGetValue(date, out var models)) state.Days[date] = models = new(StringComparer.Ordinal);
            var model = state.Model.Length <= 120 ? state.Model : "Sin modelo";
            if (!models.ContainsKey(model) && models.Count >= 64) model = "Otros modelos";
            models[model] = SaturatingAdd(models.GetValueOrDefault(model), delta);
        }
        catch (JsonException) { state.Uncertain = true; }
    }
    private static string? String(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Number(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static long SaturatingAdd(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;
    public sealed class HistoryCache
    {
        public int Version { get; set; } = CacheVersion;
        public string Zone { get; set; } = "";
        public Dictionary<string, FileCursor> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    public sealed class FileCursor
    {
        public long Offset { get; set; }
        public long Length { get; set; }
        public long Modified { get; set; }
        public string? Prefix { get; set; }
        public bool Completed { get; set; }
        public bool DiscardLine { get; set; }
        public long? Previous { get; set; }
        public long? Inherited { get; set; }
        public bool HasBase { get; set; }
        public bool Uncertain { get; set; }
        public bool MetadataUnavailable { get; set; }
        public string Model { get; set; } = "Sin modelo";
        public Dictionary<string, Dictionary<string, long>> Days { get; set; } = new(StringComparer.Ordinal);
    }
}
