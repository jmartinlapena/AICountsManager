using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AICountsManager;

internal sealed record GenerationTokens(long Timestamp, string Model, long Total);
internal static class AntigravityAccounting
{
    // Only generation-accounting fields; never decode prompts or responses.
    internal static GenerationTokens? Decode(byte[] blob, byte[]? step)
    {
        try
        {
            var wrapped = Bytes(blob, 1); var usage = Bytes(wrapped, 4);
            var model = Text(wrapped, 19); var label = Text(wrapped, 21);
            var system = Number(usage, 1); var input = Number(usage, 2); var output = Number(usage, 3); var cache = Number(usage, 5);
            if (model is null && label is null && input == 0 && output == 0 && cache == 0) return null;
            var total = checked((long)system + (long)input + (long)output + (long)cache);
            if (total <= 0) return null;
            var seconds = Number(Bytes(Bytes(wrapped, 9), 4), 1);
            if (seconds == 0 && step is not null) seconds = Number(Bytes(step, 1), 1);
            if (seconds == 0 || seconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
            return new((long)seconds, model?.EndsWith("-default", StringComparison.Ordinal) == true ? label ?? model : model ?? label ?? "Sin modelo", total);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException) { return null; }
    }
    private static string? Text(ReadOnlySpan<byte> bytes, int field)
    { var data = Bytes(bytes, field); return data.Length is > 0 and <= 256 ? Encoding.UTF8.GetString(data).Trim() : null; }
    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong value = 0;
        for (var i = 0; i < 10; i++)
        {
            if (position >= bytes.Length) throw new InvalidDataException();
            var b = bytes[position++]; if (i == 9 && b > 1) throw new InvalidDataException();
            value |= (ulong)(b & 127) << (7 * i); if ((b & 128) == 0) return value;
        }
        throw new InvalidDataException();
    }
    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> bytes, int requested)
    { Find(bytes, requested, out var start, out var length, out _); return bytes.Slice(start, length); }
    private static ulong Number(ReadOnlySpan<byte> bytes, int requested)
    { Find(bytes, requested, out _, out _, out var value); return value; }
    private static void Find(ReadOnlySpan<byte> bytes, int requested, out int start, out int length, out ulong value)
    {
        start = length = 0; value = 0; var offset = 0;
        while (offset < bytes.Length)
        {
            var tag = Varint(bytes, ref offset); var number = tag >> 3; if (number == 0) throw new InvalidDataException();
            var wire = tag & 7; ulong n = 0; var size = 0;
            switch (wire)
            {
                case 0: n = Varint(bytes, ref offset); break;
                case 1: size = 8; break;
                case 5: size = 4; break;
                case 2: var l = Varint(bytes, ref offset); if (l > (ulong)(bytes.Length - offset)) throw new InvalidDataException(); size = (int)l; break;
                default: throw new InvalidDataException();
            }
            if (size > bytes.Length - offset) throw new InvalidDataException();
            if (number == (ulong)requested) { if (wire == 2) { start = offset; length = size; } if (wire == 0) value = n; return; }
            offset += size;
        }
    }
}

// Built-in Windows SQLite; read-only connections, bounded blob copies, deterministic disposal.
internal sealed class ReadOnlySqlite : IDisposable
{
    private IntPtr _db;
    public ReadOnlySqlite(string path)
    {
        var rc = sqlite3_open_v2(Encoding.UTF8.GetBytes(path + '\0'), out _db, 1, IntPtr.Zero);
        if (rc != 0) { Dispose(); throw new IOException("No se puede leer el índice local."); }
        sqlite3_busy_timeout(_db, 100);
    }
    public void Query(string sql, Action<IntPtr> row)
    {
        var code = sqlite3_prepare_v2(_db, Encoding.UTF8.GetBytes(sql + '\0'), -1, out var stmt, IntPtr.Zero);
        try
        {
            if (code != 0) throw new IOException("Esquema de historial no compatible.");
            while ((code = sqlite3_step(stmt)) == 100) row(stmt);
            if (code != 101) throw new IOException("El historial local está ocupado.");
        }
        finally { if (stmt != IntPtr.Zero) sqlite3_finalize(stmt); }
    }
    internal static byte[]? Blob(IntPtr stmt, int column)
    {
        var length = sqlite3_column_bytes(stmt, column); if (length <= 0 || length > 1024 * 1024) return null;
        var ptr = sqlite3_column_blob(stmt, column); if (ptr == IntPtr.Zero) return null;
        var result = new byte[length]; Marshal.Copy(ptr, result, 0, length); return result;
    }
    internal static long Integer(IntPtr stmt, int column) => sqlite3_column_int64(stmt, column);
    public void Dispose() { if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; } }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
}

internal sealed class AntigravityTokenHistory(string geminiHome, string cachePath) : IProviderTokenHistory
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, Cursor> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    public TokenHistorySnapshot Snapshot { get; private set; } = TokenHistorySnapshot.Empty;
    internal int LastRowsRead { get; private set; }
    public async Task<TokenHistorySnapshot> RefreshAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return Snapshot;
        try { return await Task.Run(() => Scan(token), token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private TokenHistorySnapshot Scan(CancellationToken token)
    {
        var clock = Stopwatch.StartNew(); bool partial = false, pending = false, changed = false; LastRowsRead = 0;
        var cutoff = DateTimeOffset.Now.Date.AddDays(1 - TokenHistory.RetentionDays); long bytes = 0;
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                try
                {
                    if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 8 * 1024 * 1024)
                    {
                        var cache = JsonSerializer.Deserialize<Dictionary<string, Cursor>>(File.ReadAllText(cachePath));
                        if (cache is { Count: <= 1024 } && cache.Values.Sum(c => c?.Events?.Count ?? 100001) <= 100000 && cache.Values.All(c => c is not null && c.Events is { Count: <= 100000 } && c.Events.Values.All(e => e is { Total: > 0, Timestamp: > 0 } && e.Timestamp <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() && e.Model is { Length: <= 256 })))
                            _files = new(cache, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { partial = true; }
            }
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(geminiHome))
                foreach (var home in Directory.EnumerateDirectories(geminiHome, "antigravity*"))
                {
                    if ((File.GetAttributes(home) & FileAttributes.ReparsePoint) != 0) continue;
                    var directory = Path.Combine(home, "conversations");
                    if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var path in Directory.EnumerateFiles(directory, "*.db").Take(1025))
                    {
                        if (discovered.Count >= 1024) { partial = true; break; }
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) discovered.Add(Path.GetFullPath(path));
                    }
                }
            // Keep indexed observations when a local conversation is removed; retention still applies.
            var eventCount = _files.Values.Sum(c => c.Events.Count);
            foreach (var path in discovered.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds > 750 || bytes >= 8 * 1024 * 1024) { pending = true; break; }
                var info = new FileInfo(path); var wal = new FileInfo(path + "-wal");
                var stamp = $"{info.CreationTimeUtc.Ticks}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{(wal.Exists ? wal.Length : 0)}:{(wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0)}";
                if (_files.TryGetValue(path, out var cursor) && cursor.Stamp == stamp) continue;
                if (cursor is null)
                { if (_files.Count >= 1024) { partial = true; continue; } _files[path] = cursor = new Cursor(); }
                try
                {
                    using var db = new ReadOnlySqlite(path);
                    var hasSteps = false;
                    db.Query("SELECT 1 FROM pragma_table_info('steps') WHERE name='metadata'", _ => hasSteps = true);
                    long max = -1; db.Query("SELECT COALESCE(MAX(idx),-1) FROM gen_metadata", s => max = ReadOnlySqlite.Integer(s, 0));
                    if (max < cursor.Last || cursor.Created != info.CreationTimeUtc.Ticks || (!cursor.HasSteps && hasSteps))
                    { eventCount -= cursor.Events.Count; cursor.Events.Clear(); cursor.Last = -1; }
                    cursor.Created = info.CreationTimeUtc.Ticks; cursor.HasSteps = hasSteps;
                    var from = Math.Max(-1, cursor.Last - 8);
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (clock.ElapsedMilliseconds > 750 || bytes >= 8 * 1024 * 1024 || eventCount >= 100000)
                        { if (eventCount >= 100000) partial = true; else pending = true; break; }
                        var rows = 0;
                        var step = hasSteps ? "(SELECT CASE WHEN length(metadata)<=1048576 THEN metadata END FROM steps WHERE idx=g.idx LIMIT 1)" : "NULL";
                        db.Query($"SELECT g.idx, CASE WHEN length(g.data)<=1048576 THEN g.data END, {step} FROM gen_metadata g WHERE g.idx>{from} ORDER BY g.idx LIMIT 8", s =>
                        {
                            token.ThrowIfCancellationRequested(); rows++; LastRowsRead++;
                            var index = ReadOnlySqlite.Integer(s, 0); var blob = ReadOnlySqlite.Blob(s, 1); var metadata = ReadOnlySqlite.Blob(s, 2);
                            bytes += (blob?.Length ?? 0) + (metadata?.Length ?? 0);
                            from = index; cursor.Last = Math.Max(index, cursor.Last); if (cursor.Events.Remove(index)) eventCount--;
                            if (blob is null) { partial = true; cursor.Partial = true; return; }
                            if (AntigravityAccounting.Decode(blob, metadata) is { } e)
                            {
                                var time = DateTimeOffset.FromUnixTimeSeconds(e.Timestamp).LocalDateTime;
                                if (time >= cutoff && time <= DateTime.Now.AddMinutes(5)) { cursor.Events[index] = e; eventCount++; }
                            }
                        });
                        changed = true;
                        if (rows < 8) { cursor.Stamp = stamp; break; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { partial = true; }
            }
            foreach (var cursor in _files.Values)
            {
                foreach (var key in cursor.Events.Where(e => e.Value.Timestamp < new DateTimeOffset(cutoff).ToUnixTimeSeconds()).Select(e => e.Key).ToArray())
                { cursor.Events.Remove(key); changed = true; }
                partial |= cursor.Partial;
            }
            if (changed)
            {
                var json = JsonSerializer.Serialize(_files);
                if (Encoding.UTF8.GetByteCount(json) <= 8 * 1024 * 1024) AtomicFile.WriteText(cachePath, json);
                else partial = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
        var days = _files.Values.SelectMany(c => c.Events.Values).GroupBy(e => DateTimeOffset.FromUnixTimeSeconds(e.Timestamp).LocalDateTime.ToString("yyyy-MM-dd"))
            .Select(g => new TokenDay(g.Key, g.GroupBy(e => e.Model).ToDictionary(m => m.Key, m => (long)Math.Min(long.MaxValue, m.Sum(e => (decimal)e.Total)))))
            .OrderBy(d => d.Date).ToArray();
        return Snapshot = new(days, partial, days.Length > 0 ? DateTimeOffset.UtcNow.ToString("O") : null,
            partial ? "Parte del historial de Antigravity no se pudo leer." : null, pending);
    }
    internal sealed class Cursor
    {
        public string? Stamp { get; set; }
        public long Created { get; set; }
        public long Last { get; set; } = -1;
        public bool HasSteps { get; set; }
        public bool Partial { get; set; }
        public Dictionary<long, GenerationTokens> Events { get; set; } = [];
    }
}
internal static class ProviderHistory
{
    public static TokenHistorySnapshot Merge(TokenHistorySnapshot codex, TokenHistorySnapshot antigravity)
    {
        return Merge(new Dictionary<string, TokenHistorySnapshot> { [ProviderIds.Codex] = codex, [ProviderIds.Antigravity] = antigravity });
    }
    public static TokenHistorySnapshot Merge(IReadOnlyDictionary<string, TokenHistorySnapshot> sources)
    {
        var days = sources.SelectMany(s => s.Value.Days.Select(d => (Source: s.Key, Day: d))).GroupBy(e => e.Day.Date)
            .Select(g => new TokenDay(g.Key, g.SelectMany(e => e.Day.Models.Select(m => (Name: ProviderIds.Name(e.Source) + " · " + m.Key, m.Value)))
                .GroupBy(m => m.Name).ToDictionary(m => m.Key, m => (long)Math.Min(long.MaxValue, m.Sum(v => (decimal)v.Value)))))
            .OrderBy(d => d.Date).ToArray();
        return new(days, sources.Values.Any(s => s.Partial), sources.Values.Select(s => s.UpdatedAt).FirstOrDefault(t => t is not null),
            sources.Values.Select(s => s.Error).FirstOrDefault(e => e is not null), sources.Values.Any(s => s.CatchingUp), sources);
    }
}
