using AICountsManager;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static Task ProbeOrganizationCopiesAsync(string codexHome, string desktop, string report) => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories(); AtomicFile.TryRestrictDirectory(paths.AppHome);
        var original = Path.Combine(codexHome, ".codex-global-state.json");
        var codexBefore = File.ReadAllBytes(original);
        var auth = await StableAuthFile.ReadAsync(Path.Combine(codexHome, "auth.json"));
        var a = new OrganizationAccount(auth.Identity.AccountId!, auth.Identity.UserId!);
        var b = new OrganizationAccount(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var copy = Path.Combine(paths.CodexHome, ".codex-global-state.json"); AtomicFile.WriteBytes(copy, codexBefore);
        var transfer = Transfer(paths, new CodexOrganizationAdapter(paths.CodexHome));
        TransferOrganization(transfer, "copy-a", "copy-b", a, b); SimulateCodexBootstrap(copy, b);
        var changed = ReadObject(copy); var project = "probe-" + Guid.NewGuid();
        var projects = changed["local-projects"]!.AsObject(); var previousCount = projects.Count;
        projects[project] = new JsonObject { ["id"] = project, ["name"] = "Isolated probe", ["rootPaths"] = new JsonArray(paths.CodexHome) };
        changed["project-order"]!.AsArray().Insert(0, JsonValue.Create(project)); PutJson(copy, changed);
        TransferOrganization(transfer, "copy-b", "copy-a", b, a); SimulateCodexBootstrap(copy, a);
        Check(ReadObject(copy)["local-projects"]![project] is not null, "Real-format Codex copy did not propagate the new local reference.");
        changed = ReadObject(copy); changed["local-projects"]!.AsObject().Remove(project); changed["project-order"]!.AsArray().RemoveAt(0); PutJson(copy, changed);
        TransferOrganization(transfer, "copy-a", "copy-b", a, b); SimulateCodexBootstrap(copy, b);
        Check(ReadObject(copy)["local-projects"]![project] is null, "Real-format Codex copy resurrected a deleted reference.");

        var realRoot = Path.Combine(desktop, "claude-code-sessions");
        var directories = Directory.EnumerateDirectories(realRoot).SelectMany(Directory.EnumerateDirectories)
            .Select(d => new { Directory = d, Files = Directory.GetFiles(d, "local_*.json", SearchOption.TopDirectoryOnly) })
            .OrderByDescending(d => d.Files.Length).ToArray();
        var selected = directories.FirstOrDefault(d => d.Files.Length != 0);
        var claudeCount = 0; var claudeHashesUnchanged = true;
        if (selected is not null)
        {
            var ca = new OrganizationAccount(Path.GetFileName(Path.GetDirectoryName(selected.Directory))!, Path.GetFileName(selected.Directory));
            var cb = new OrganizationAccount(Guid.NewGuid().ToString(), ca.ContextId);
            var fakeDesktop = Path.Combine(paths.AppHome, "desktop-copy");
            var originals = selected.Files.ToDictionary(f => f, File.ReadAllBytes);
            foreach (var f in originals) AtomicFile.WriteBytes(Path.Combine(fakeDesktop, "claude-code-sessions", ca.AccountId, ca.ContextId, Path.GetFileName(f.Key)), f.Value);
            var adapter = new ClaudeCodeOrganizationAdapter(fakeDesktop); var claude = Transfer(paths, adapter);
            TransferOrganization(claude, "claude-copy-a", "claude-copy-b", ca, cb);
            var destination = Path.Combine(adapter.Root, cb.AccountId, cb.ContextId);
            var files = Directory.GetFiles(destination, "local_*.json"); claudeCount = files.Length;
            Check(claudeCount == originals.Count, "Real-format Claude copy lost an index.");
            var first = files.First(); var record = ReadObject(first); var cli = record["cliSessionId"]!.GetValue<string>();
            record["title"] = "Renamed only in isolated copy"; PutJson(first, record);
            TransferOrganization(claude, "claude-copy-b", "claude-copy-a", cb, ca);
            var returned = Directory.GetFiles(Path.Combine(adapter.Root, ca.AccountId, ca.ContextId), "local_*.json").Select(ReadObject).Single(r => r["cliSessionId"]!.GetValue<string>() == cli);
            Check(returned["title"]!.GetValue<string>() == "Renamed only in isolated copy", "Real-format Claude round-trip lost the latest title.");
            claudeHashesUnchanged = originals.All(f => File.ReadAllBytes(f.Key).SequenceEqual(f.Value));
        }
        var codexUnchanged = File.ReadAllBytes(original).SequenceEqual(codexBefore);
        Check(codexUnchanged && claudeHashesUnchanged, "A real client file changed during the read-only probe.");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            Mode = "Isolated copies; no live accounts switched; no clients opened or closed",
            CodexProjects = previousCount, CodexRoundTrip = true, CodexOriginalUnchanged = codexUnchanged,
            ClaudeIndices = claudeCount, ClaudeRoundTrip = claudeCount > 0, ClaudeOriginalsUnchanged = claudeHashesUnchanged
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(await File.ReadAllTextAsync(report));
    });
}
