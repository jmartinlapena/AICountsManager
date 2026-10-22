using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AICountsManager;

internal interface IProviderTokenHistory
{
    TokenHistorySnapshot Snapshot { get; }
    Task<TokenHistorySnapshot> RefreshAsync(CancellationToken token);
}
internal sealed record ClientTokenEvent(string Date, string Model, long Tokens);

// Incremental, bounded local reads. Cached identifiers are hashes; no message text is retained.
internal sealed class ClaudeTokenHistory(string home, string cachePath) : IProviderTokenHistory
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, FileIndex> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    public TokenHistorySnapshot Snapshot { get; private set; } = TokenHistorySnapshot.Empty;
    internal long LastBytesRead { get; private set; }
    public async Task<TokenHistorySnapshot> RefreshAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return Snapshot;
        try { return await Task.Run(() => Scan(token), token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private TokenHistorySnapshot Scan(CancellationToken token)
    {
        LastBytesRead = 0; var clock = Stopwatch.StartNew(); bool changed = false, partial = false, pending = false;
        var today = DateOnly.FromDateTime(DateTime.Today); var cutoff = today.AddDays(1 - TokenHistory.RetentionDays).ToString("yyyy-MM-dd");
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length < 12 * 1024 * 1024)
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, FileIndex>>(File.ReadAllText(cachePath));
                    if (saved is not null && saved.Count <= 10000 && saved.Values.All(f => f is not null && f.Offset >= 0 && f.Events is not null && f.Events.Count <= 50000 && f.Events.Values.All(Valid))) _files = saved;
                }
            }
            var root = Path.Combine(home, "projects");
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0)
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                var eventCount = _files.Values.Sum(f => f.Events.Count);
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", options).Take(10001))
                {
                    token.ThrowIfCancellationRequested();
                    if (_files.Count >= 10000 || clock.ElapsedMilliseconds > 600) { partial = pending = true; break; }
                    var info = new FileInfo(path); if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-365)) continue;
                    var key = AuthHash.Bytes(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
                    if (!_files.TryGetValue(key, out var index)) _files[key] = index = new();
                    if (index.Length == info.Length && index.Modified == info.LastWriteTimeUtc.Ticks && index.Completed) continue;
                    if (LastBytesRead >= 4 * 1024 * 1024 || eventCount >= 50000) { partial = true; pending = eventCount < 50000; break; }
                    if (info.Length < index.Offset || (index.Completed && index.Length == info.Length && index.Modified != info.LastWriteTimeUtc.Ticks) || index.Created != info.CreationTimeUtc.Ticks)
                    { eventCount -= index.Events.Count; _files[key] = index = new(); }
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
                    file.Position = index.Offset; using var line = new MemoryStream();
                    while (LastBytesRead < 4 * 1024 * 1024 && clock.ElapsedMilliseconds < 600 && eventCount < 50000)
                    {
                        token.ThrowIfCancellationRequested(); var b = file.ReadByte(); if (b < 0) break; LastBytesRead++;
                        if (b == 10)
                        {
                            if (!index.Discard && Parse(line.ToArray()) is { } entry && string.CompareOrdinal(entry.Event.Date, cutoff) >= 0 && string.CompareOrdinal(entry.Event.Date, today.ToString("yyyy-MM-dd")) <= 0)
                            {
                                if (!index.Events.TryGetValue(entry.Id, out var old)) { index.Events[entry.Id] = entry.Event; eventCount++; }
                                else if (entry.Event.Tokens >= old.Tokens) index.Events[entry.Id] = entry.Event;
                            }
                            line.SetLength(0); index.Discard = false; index.Offset = file.Position;
                        }
                        else if (index.Discard) index.Offset = file.Position;
                        else if (line.Length >= 256 * 1024) { index.Discard = true; index.Uncertain = true; line.SetLength(0); index.Offset = file.Position; }
                        else line.WriteByte((byte)b);
                    }
                    index.Completed = file.Position >= file.Length; index.Length = info.Length; index.Modified = info.LastWriteTimeUtc.Ticks; index.Created = info.CreationTimeUtc.Ticks;
                    changed = true; if (!index.Completed) { partial = true; pending = eventCount < 50000; }
                }
            }
            foreach (var index in _files.Values)
                foreach (var id in index.Events.Where(e => string.CompareOrdinal(e.Value.Date, cutoff) < 0).Select(e => e.Key).ToArray()) { index.Events.Remove(id); changed = true; }
            partial |= _files.Values.Any(f => f.Uncertain);
            if (changed)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(_files);
                if (bytes.Length <= 12 * 1024 * 1024) AtomicFile.WriteBytes(cachePath, bytes); else partial = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { partial = true; }
        var events = _files.Values.SelectMany(f => f.Events).GroupBy(e => e.Key).Select(g => g.MaxBy(e => e.Value.Tokens).Value);
        var days = ToDays(events);
        return Snapshot = new(days, partial, days.Count > 0 ? DateTimeOffset.UtcNow.ToString("O") : null, partial ? "Historial de Claude incompleto." : null, pending);
    }
    internal static (string Id, ClientTokenEvent Event)? Parse(byte[] bytes)
    {
        if (bytes.AsSpan().IndexOf("\"usage\""u8) < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
            if (ProviderJson.Text(root, "type") != "assistant") return null;
            var message = ProviderJson.Get(root, "message"); var usage = ProviderJson.Get(message, "usage");
            var id = ProviderJson.Text(message, "id"); var model = ProviderJson.Text(message, "model");
            if (id is not { Length: > 0 and <= 200 } || model is not { Length: > 0 and <= 120 } || model == "<synthetic>" || !DateTimeOffset.TryParse(ProviderJson.Text(root, "timestamp"), out var time)) return null;
            long total = 0;
            foreach (var key in new[] { "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens" })
            {
                var number = ProviderJson.Get(usage, key);
                if (number.ValueKind == JsonValueKind.Undefined && key.StartsWith("cache_")) continue;
                if (!number.TryGetInt64(out var n) || n < 0) return null; total = checked(total + n);
            }
            return total > 0 ? (AuthHash.Bytes(Encoding.UTF8.GetBytes(id)), new(time.LocalDateTime.ToString("yyyy-MM-dd"), model, total)) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException) { return null; }
    }
    private static bool Valid(ClientTokenEvent e) => e is not null && e.Tokens >= 0 && DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", out _) && e.Model is { Length: > 0 and <= 120 };
    internal static IReadOnlyList<TokenDay> ToDays(IEnumerable<ClientTokenEvent> events) => events.GroupBy(e => e.Date).OrderBy(g => g.Key)
        .Select(g => new TokenDay(g.Key, g.GroupBy(e => e.Model).ToDictionary(m => m.Key, m => (long)Math.Min(long.MaxValue, m.Sum(e => (decimal)e.Tokens))))).ToArray();
    public sealed class FileIndex
    {
        public long Offset { get; set; }
        public long Length { get; set; }
        public long Modified { get; set; }
        public long Created { get; set; }
        public bool Completed { get; set; }
        public bool Discard { get; set; }
        public bool Uncertain { get; set; }
        public Dictionary<string, ClientTokenEvent> Events { get; set; } = [];
    }
}

// Remote event reads happen only with the existing history refresh, at most once per five minutes.
// An incomplete page set never replaces the last complete week. Account caches never cross identities.
internal sealed class CursorTokenHistory(IClientSessionSource sessions, HttpClient http, string cachePath) : IProviderTokenHistory
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _identity;
    private DateTimeOffset _next;
    private TokenHistorySnapshot _snapshot = TokenHistorySnapshot.Empty;
    public TokenHistorySnapshot Snapshot => _snapshot;
    public async Task<TokenHistorySnapshot> RefreshAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return Snapshot;
        try
        {
            var session = sessions.Read(); var user = session is null ? null : ProviderJson.CursorUser(session.AccessToken);
            if (user is null) { _identity = null; return _snapshot = TokenHistorySnapshot.Empty; }
            var identity = AuthHash.Bytes(Encoding.UTF8.GetBytes(user));
            if (identity != _identity)
            {
                _identity = identity; _snapshot = TokenHistorySnapshot.Empty; _next = DateTimeOffset.MinValue;
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length < 2 * 1024 * 1024)
                {
                    try { var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(cachePath)); if (saved?.Identity == identity && saved.Days is { Count: <= 365 } && saved.Days.All(d => DateOnly.TryParseExact(d.Date, "yyyy-MM-dd", out _) && d.Models is { Count: <= 100 } && d.Models.All(m => m.Key.Length <= 120 && m.Value >= 0))) _snapshot = new(saved.Days, true, saved.CheckedAt, null); }
                    catch (JsonException) { }
                }
            }
            if (DateTimeOffset.UtcNow < _next) return Snapshot;
            _next = DateTimeOffset.UtcNow.AddMinutes(5);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var today = DateOnly.FromDateTime(DateTime.Today); var monday = HistoryPanel.Monday(today);
            var start = new DateTimeOffset(monday.ToDateTime(TimeOnly.MinValue)); var end = DateTimeOffset.Now;
            var events = new List<ClientTokenEvent>(); var seen = new HashSet<string>(); int readCount = 0; bool complete = false;
            for (var page = 1; page <= 4; page++)
            {
                var body = JsonSerializer.Serialize(new { startDate = start.ToUnixTimeMilliseconds().ToString(), endDate = end.ToUnixTimeMilliseconds().ToString(), page, pageSize = 500 });
                var response = await ProviderHttp.SendAsync(http, ProviderHttp.CursorWeb("https://cursor.com/api/dashboard/get-filtered-usage-events", session!, body), deadline.Token).ConfigureAwait(false);
                if (response.Status != 200)
                { if (response.Status == 429) _next = DateTimeOffset.UtcNow.AddSeconds(Math.Max(300, response.RetryAfter ?? 300)); break; }
                var rows = ProviderJson.Get(response.Body, "usageEventsDisplay"); var count = ProviderJson.Number(response.Body, "totalUsageEventsCount");
                if (rows.ValueKind != JsonValueKind.Array || count is not >= 0 || count != Math.Truncate(count.Value)) break;
                foreach (var row in rows.EnumerateArray())
                {
                    readCount++;
                    var parsed = Parse(row); if (parsed is null) throw new InvalidDataException("Evento no compatible.");
                    var eventId = ProviderJson.Text(row, "id");
                    if (eventId is null || seen.Add(eventId)) events.Add(parsed);
                }
                if (readCount >= count) { complete = true; break; }
                if (rows.GetArrayLength() == 0) break;
            }
            if (sessions.Read()?.Fingerprint != session!.Fingerprint) { _identity = null; return _snapshot = TokenHistorySnapshot.Empty; }
            if (!complete) return _snapshot = Snapshot with { Partial = true, Error = "Historial de Cursor incompleto; se conserva la última lectura." };
            var days = Snapshot.Days.Where(d => string.CompareOrdinal(d.Date, monday.ToString("yyyy-MM-dd")) < 0 && DateOnly.Parse(d.Date) >= today.AddDays(-364))
                .Concat(ClaudeTokenHistory.ToDays(events.Where(e => string.CompareOrdinal(e.Date, monday.ToString("yyyy-MM-dd")) >= 0 && string.CompareOrdinal(e.Date, today.ToString("yyyy-MM-dd")) <= 0))).OrderBy(d => d.Date).ToArray();
            _snapshot = new(days, false, DateTimeOffset.UtcNow.ToString("O"), null);
            AtomicFile.WriteText(cachePath, JsonSerializer.Serialize(new Saved(identity, days, _snapshot.UpdatedAt)));
            return Snapshot;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return _snapshot = Snapshot with { Partial = true, Error = "Historial de Cursor pendiente." }; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or UnauthorizedAccessException)
        { return _snapshot = Snapshot with { Partial = true, Error = "No se pudo consultar el historial de Cursor." }; }
        finally { _gate.Release(); }
    }
    internal static ClientTokenEvent? Parse(JsonElement row)
    {
        var time = ProviderJson.Timestamp(ProviderJson.Get(row, "timestamp")); var model = ProviderJson.Text(row, "model"); var usage = ProviderJson.Get(row, "tokenUsage");
        if (time is null || model is not { Length: > 0 and <= 120 } || usage.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined)) return null;
        long total = 0;
        try
        {
            foreach (var key in new[] { "inputTokens", "outputTokens", "cacheWriteTokens", "cacheReadTokens" })
            {
                var n = ProviderJson.Get(usage, key); if (n.ValueKind == JsonValueKind.Undefined) continue;
                if (!n.TryGetInt64(out var value) || value < 0) return null; total = checked(total + value);
            }
            return new(DateTimeOffset.FromUnixTimeSeconds(time.Value).LocalDateTime.ToString("yyyy-MM-dd"), model, total);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException or ArgumentOutOfRangeException) { return null; }
    }
    private sealed record Saved(string Identity, IReadOnlyList<TokenDay> Days, string? CheckedAt);
}
