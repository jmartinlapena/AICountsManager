using AICountsManager;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static async Task ProbeRealClaudeArchiveAsync(string source)
    {
        var names = new[] { "config.json", "Local State", "Network/Cookies", "Network/Cookies-journal", "Network/Cookies-wal", "Network/Cookies-shm" };
        var original = names.ToDictionary(n => n, n => ClaudeSessionArchive.ReadFile(Path.Combine(source, n)));
        var basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AICountsManager.ArchiveProbe"));
        var temporary = Path.GetFullPath(Path.Combine(basePath, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(temporary); AtomicFile.TryRestrictDirectory(temporary);
        try
        {
            var desktop = Path.Combine(temporary, "desktop"); Directory.CreateDirectory(Path.Combine(desktop, "Network"));
            foreach (var pair in original) if (pair.Value is not null) AtomicFile.WriteBytes(Path.Combine(desktop, pair.Key), pair.Value);
            using var http = ProviderHttp.Create();
            var archive = new ClaudeSessionArchive(desktop, Path.Combine(temporary, "code"), Path.Combine(temporary, "code-config.json"), Path.Combine(temporary, "rollback.bin"), http, () => false);
            var snapshot = archive.Capture(); var initialSession = new ClaudeDesktopSessionSource(desktop).Read();
            archive.Apply(ClaudeSessionArchive.Empty);
            var cleared = new ClaudeDesktopSessionSource(desktop).Read() is null && !File.Exists(Path.Combine(desktop,"Network","Cookies"));
            archive.Apply(snapshot);
            var restored = new ClaudeDesktopSessionSource(desktop).Read();
            var cookiesRestored = ClaudeSessionArchive.CookieNames.All(n => EqualBytes(original.GetValueOrDefault("Network/" + n), ClaudeSessionArchive.ReadFile(Path.Combine(desktop, "Network", n))));
            var expectedConfig = JsonNode.Parse(original["config.json"]!);
            var configRestored = JsonNode.DeepEquals(expectedConfig, JsonNode.Parse(File.ReadAllBytes(Path.Combine(desktop,"config.json"))));
            var beforeFailure = File.ReadAllBytes(Path.Combine(desktop,"config.json"));
            archive.AfterWriteForTesting = n => { if (n == 4) throw new IOException("injected"); };
            try { archive.Apply(ClaudeSessionArchive.Empty); } catch (IOException) { }
            var rollbackExact = beforeFailure.SequenceEqual(File.ReadAllBytes(Path.Combine(desktop,"config.json"))) && cookiesRestored && !archive.HasRecovery;
            var originalUnchanged = original.All(p => EqualBytes(p.Value, ClaudeSessionArchive.ReadFile(Path.Combine(source,p.Key))));
            Check(cleared && cookiesRestored && configRestored && rollbackExact && originalUnchanged && initialSession?.Fingerprint == restored?.Fingerprint,
                "The isolated real-data round-trip did not pass every check.");
            Console.WriteLine(JsonSerializer.Serialize(new { IsolatedCopy = true, OriginalUnchanged = originalUnchanged, SessionReadable = initialSession is not null,
                Cleared = cleared, ConfigRestored = configRestored, CookiesRestored = cookiesRestored, RollbackByteExact = rollbackExact,
                Files = original.Count(p => p.Value is not null), Bytes = original.Values.Sum(b => (long)(b?.Length ?? 0)) }));
        }
        finally
        {
            if (!temporary.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe probe directory.");
            Directory.Delete(temporary, true);
        }
        await Task.CompletedTask;
    }
    private static bool EqualBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
