using AICountsManager;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static JsonObject CodexOrganization(OrganizationAccount source, OrganizationAccount target)
    {
        var shared = new JsonObject
        {
            ["local-projects"] = new JsonObject
            {
                ["first"] = new JsonObject { ["id"] = "first", ["name"] = "First", ["rootPaths"] = new JsonArray("C:/projects/first") },
                ["deleted"] = new JsonObject { ["id"] = "deleted", ["name"] = "Delete me", ["rootPaths"] = new JsonArray("C:/projects/deleted") }
            },
            ["project-order"] = new JsonArray("deleted", "first"),
            ["selected-project"] = new JsonObject { ["type"] = "local", ["projectId"] = "first" },
            ["project-appearances"] = new JsonObject { ["first"] = new JsonObject { ["color"] = "blue" } },
            ["sidebar-project-thread-orders"] = new JsonObject { ["first"] = new JsonArray("chat-2", "chat-1") },
            ["thread-project-assignments"] = new JsonObject
            {
                ["chat-1"] = new JsonObject { ["projectKind"] = "local", ["projectId"] = "first" },
                ["deleted-chat"] = new JsonObject { ["projectKind"] = "local", ["projectId"] = "deleted" },
                ["source-cloud"] = new JsonObject { ["projectKind"] = "cloud", ["projectId"] = "private-source" }
            },
            ["thread-project-membership-host-ids"] = new JsonObject { ["chat-1"] = "local" },
            ["app-server-project-id-by-legacy-project-id-by-host"] = new JsonObject { ["local"] = new JsonObject { ["first"] = "stable-project-id" } },
            ["app-server-projects-migration-by-host"] = new JsonObject { ["local"] = new JsonObject { ["version"] = 1, ["projectsMigrated"] = true } },
            ["electron-persisted-atom-state"] = new JsonObject { ["identity"] = "source", ["cloud-cache"] = "source-private" },
            ["unrelated-setting"] = "keep-global"
        };
        shared["electron-account-renderer-state-v1"] = new JsonObject
        {
            ["owner"] = CodexOrganizationAdapter.Scope(source),
            ["inactive"] = new JsonObject
            {
                [CodexOrganizationAdapter.Scope(target)] = new JsonObject
                {
                    ["atoms"] = new JsonObject { ["identity"] = "target", ["cloud-cache"] = "target-private" },
                    ["globals"] = new JsonObject
                    {
                        ["local-projects"] = new JsonObject { ["stale"] = new JsonObject { ["id"] = "stale" } },
                        ["project-order"] = new JsonArray("stale"),
                        ["thread-project-assignments"] = new JsonObject
                        {
                            ["stale-chat"] = new JsonObject { ["projectKind"] = "local", ["projectId"] = "stale" },
                            ["target-cloud"] = new JsonObject { ["projectKind"] = "cloud", ["projectId"] = "private-target" }
                        },
                        ["target-only-setting"] = true
                    }
                }
            }
        };
        return shared;
    }
    private static void PutJson(string path, JsonNode data) => AtomicFile.WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(data));
    private static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    private static LocalOrganizationTransfer Transfer(AppPaths paths, ILocalOrganizationAdapter adapter, Action? closed = null) =>
        new(adapter, paths.TransactionsDir, paths.BackupsDir, closed ?? (() => { }));
    private static void TransferOrganization(LocalOrganizationTransfer transfer, string fromId, string toId, OrganizationAccount from, OrganizationAccount to)
    {
        var change = transfer.Prepare(fromId, toId, from, to);
        change.Apply(); change.Verify(); change.Commit();
    }
    // Mirrors the installed client's account-scope bootstrap: save owner, clear scoped keys, load target.
    private static void SimulateCodexBootstrap(string path, OrganizationAccount target)
    {
        var root = ReadObject(path); var state = root["electron-account-renderer-state-v1"]!.AsObject();
        var inactive = state["inactive"]!.AsObject(); var owner = state["owner"]!.GetValue<string>();
        var key = CodexOrganizationAdapter.Scope(target); if (owner == key) return;
        var outgoing = new JsonObject();
        foreach (var field in CodexOrganizationAdapter.Fields) if (root[field] is { } value) outgoing[field] = value.DeepClone();
        inactive[owner] = new JsonObject { ["atoms"] = root["electron-persisted-atom-state"]!.DeepClone(), ["globals"] = outgoing };
        var incoming = inactive[key]!.AsObject();
        foreach (var field in CodexOrganizationAdapter.Fields)
        {
            root.Remove(field); if (incoming["globals"]![field] is { } value) root[field] = value.DeepClone();
        }
        root["electron-persisted-atom-state"] = incoming["atoms"]!.DeepClone();
        inactive.Remove(key); state["owner"] = key; PutJson(path, root);
    }
    private static Task CodexOrganizationRoundTripAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories(); var a = new OrganizationAccount("account-a", "user-a"); var b = new OrganizationAccount("account-b", "user-b");
        var path = Path.Combine(paths.CodexHome, ".codex-global-state.json"); PutJson(path, CodexOrganization(a,b));
        var scope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("[\"user-b\",\"account-b\"]"))).ToLowerInvariant();
        Check(CodexOrganizationAdapter.Scope(b) == scope, "Codex scope differs from the installed client's hash.");
        var transcript = Path.Combine(paths.CodexHome, "sessions", "do-not-copy.jsonl"); AtomicFile.WriteText(transcript, "transcript");
        var db = Path.Combine(paths.CodexHome, "state_5.sqlite"); AtomicFile.WriteText(db, "database");
        var transfer = Transfer(paths, new CodexOrganizationAdapter(paths.CodexHome));
        TransferOrganization(transfer, "a", "b", a, b);
        var prepared = ReadObject(path);
        Check(prepared["electron-account-renderer-state-v1"]!["owner"]!.GetValue<string>() == CodexOrganizationAdapter.Scope(a), "Transfer changed Codex's owner prematurely.");
        SimulateCodexBootstrap(path, b); var current = ReadObject(path);
        Check(current["local-projects"]!["first"] is not null && current["local-projects"]!["stale"] is null, "Target organization was merged instead of replaced.");
        Check(current["thread-project-assignments"]!["target-cloud"] is not null && current["thread-project-assignments"]!["source-cloud"] is null, "Cross-account cloud associations were copied or removed.");
        Check(current["electron-persisted-atom-state"]!["identity"]!.GetValue<string>() == "target", "Codex account-specific atoms were copied.");
        current["local-projects"]!.AsObject().Remove("deleted");
        current["local-projects"]!["first"]!["name"] = "Renamed";
        current["local-projects"]!["new"] = new JsonObject { ["id"] = "new", ["name"] = "Created", ["rootPaths"] = new JsonArray("C:/projects/new") };
        current["project-order"] = new JsonArray("new", "first");
        current["sidebar-project-thread-orders"]!["first"] = new JsonArray("chat-1", "chat-2");
        current["thread-project-assignments"]!.AsObject().Remove("deleted-chat");
        current["thread-project-assignments"]!["chat-1"]!["projectId"] = "new";
        PutJson(path, current);
        TransferOrganization(transfer, "b", "a", b, a); SimulateCodexBootstrap(path, a);
        var returned = ReadObject(path);
        Check(returned["local-projects"]!["deleted"] is null && returned["local-projects"]!["first"]!["name"]!.GetValue<string>() == "Renamed"
            && returned["local-projects"]!["new"] is not null && returned["project-order"]!.ToJsonString() == "[\"new\",\"first\"]", "A → B → A lost create, rename, delete or project order.");
        Check(returned["thread-project-assignments"]!["deleted-chat"] is null && returned["thread-project-assignments"]!["chat-1"]!["projectId"]!.GetValue<string>() == "new"
            && returned["sidebar-project-thread-orders"]!["first"]!.ToJsonString() == "[\"chat-1\",\"chat-2\"]", "A → B → A lost thread membership or order.");
        Check(returned["electron-persisted-atom-state"]!["identity"]!.GetValue<string>() == "source" && returned["unrelated-setting"]!.GetValue<string>() == "keep-global", "Target identity or global settings changed.");
        TransferOrganization(transfer, "a", "b", a, b); SimulateCodexBootstrap(path, b);
        Check(ReadObject(path)["local-projects"]!["deleted"] is null, "A deleted project reappeared on a later switch.");
        Check(File.ReadAllText(transcript) == "transcript" && File.ReadAllText(db) == "database", "Transfer touched histories or the shared database.");
        Check(!transfer.HasRecovery && Directory.GetFiles(paths.BackupsDir, "organization-*.bin").Length == 1, "Backups grow across successful changes.");
        return Task.CompletedTask;
    });
    private static Task CodexOrganizationSwitchAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
        var b = Snapshot("user-b", "account-b", "workspace-b", "b", 1_700_000_010);
        SeedProfiles(paths, a, ("AccountA", a), ("AccountB", b));
        var path = Path.Combine(paths.CodexHome, ".codex-global-state.json");
        PutJson(path, CodexOrganization(new("account-a", "user-a"), new("account-b", "user-b")));
        var before = File.ReadAllBytes(path);
        var repository = Repository(paths); repository.SaveAppConfig(repository.LoadAppConfig() with { StrictVerification = true });
        using (var service = CreateService(paths, appServer: new FakeAppServerClient(new(AuthVerificationStatus.RefreshTokenInvalidated, null, "fixture"))))
        {
            Check((await service.SwitchToProfileAsync("AccountB")).Status == SwitchProfileStatus.RequiresReauthentication, "Failed credential verification did not abort.");
            Check(File.ReadAllBytes(path).SequenceEqual(before) && (await Read(paths.ActiveAuthPath)).Identity.Matches(a.Identity), "Failed switch did not restore both credentials and organization byte for byte.");
            Check(repository.LoadAppConfig().ActiveProfile == "AccountA" && !File.Exists(paths.TransactionJournalPath), "Failed switch left the wrong profile or a credential journal.");
        }
        repository.SaveAppConfig(repository.LoadAppConfig() with { StrictVerification = false });
        using (var service = CreateService(paths))
        {
            Check((await service.SwitchToProfileAsync("AccountB")).Status == SwitchProfileStatus.Success, "Successful service switch was blocked.");
            var target = ReadObject(path)["electron-account-renderer-state-v1"]!["inactive"]![CodexOrganizationAdapter.Scope(new("account-b", "user-b"))]!["globals"]!;
            Check(target["local-projects"]!["first"] is not null && target["local-projects"]!["stale"] is null, "Service did not invoke the organization adapter.");
        }
        PutJson(path, new JsonObject { ["electron-account-renderer-state-v1"] = new JsonObject { ["inactive"] = "unknown" } });
        before = File.ReadAllBytes(path);
        using (var service = CreateService(paths))
        {
            Check((await service.SwitchToProfileAsync("AccountA")).Status == SwitchProfileStatus.Failed, "Unknown metadata schema allowed a credential switch.");
            Check(File.ReadAllBytes(path).SequenceEqual(before) && (await Read(paths.ActiveAuthPath)).Identity.Matches(b.Identity), "Unknown schema modified account or organization.");
        }
    });
    private static string ClaudeIndexPath(string desktop, OrganizationAccount account, string session) =>
        Path.Combine(desktop, "claude-code-sessions", account.AccountId, account.ContextId, session + ".json");
    private static JsonObject ClaudeIndex(string session, string cli, string title, string identity) => new()
    {
        ["sessionId"] = session, ["cliSessionId"] = cli, ["cwd"] = "C:/projects/common", ["title"] = title, ["titleSource"] = "user",
        ["lastFocusedAt"] = 20, ["createdAt"] = 1, ["isArchived"] = false,
        ["remoteMcpServersConfig"] = new JsonObject { ["account-specific"] = identity }, ["permissionMode"] = identity,
        ["runtime-private"] = identity
    };
    private static Task ClaudeOrganizationRoundTripAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories(); var desktop = Path.Combine(paths.AppHome, "desktop");
        var a = new OrganizationAccount(DesktopAccount, DesktopOrg); var b = new OrganizationAccount(ClaudeSecond, DesktopOrg);
        var cli = Guid.NewGuid().ToString(); var cliNew = Guid.NewGuid().ToString(); var cliStale = Guid.NewGuid().ToString();
        var sessionA = "local_" + Guid.NewGuid(); var sessionB = "local_" + Guid.NewGuid(); var sessionNew = "local_" + Guid.NewGuid(); var stale = "local_" + Guid.NewGuid();
        var pa = ClaudeIndexPath(desktop, a, sessionA); var pb = ClaudeIndexPath(desktop, b, sessionB); var pn = ClaudeIndexPath(desktop, a, sessionNew); var ps = ClaudeIndexPath(desktop, b, stale);
        PutJson(pa, ClaudeIndex(sessionA, cli, "Current", "source-private")); PutJson(pn, ClaudeIndex(sessionNew, cliNew, "New", "source-private"));
        PutJson(pb, ClaudeIndex(sessionB, cli, "Old", "target-private")); PutJson(ps, ClaudeIndex(stale, cliStale, "Stale", "target-private"));
        var tasks = Path.Combine(Path.GetDirectoryName(pb)!, "scheduled-tasks.json"); AtomicFile.WriteText(tasks, "scheduled-unchanged");
        var transcript = Path.Combine(paths.AppHome, "transcripts", cli + ".jsonl"); AtomicFile.WriteText(transcript, "transcript-unchanged");
        var transfer = Transfer(paths, new ClaudeCodeOrganizationAdapter(desktop));
        TransferOrganization(transfer, "a", "b", a, b);
        var record = ReadObject(pb); var copiedNew = ClaudeIndexPath(desktop, b, sessionNew);
        Check(record["sessionId"]!.GetValue<string>() == sessionB && record["title"]!.GetValue<string>() == "Current", "Matching cliSessionId did not retain the target session ID and source title.");
        Check(record["permissionMode"]!.GetValue<string>() == "target-private" && record["remoteMcpServersConfig"]!["account-specific"]!.GetValue<string>() == "target-private", "Target-specific Claude settings were overwritten.");
        Check(!File.Exists(ps) && File.Exists(copiedNew) && ReadObject(copiedNew)["runtime-private"] is null && ReadObject(copiedNew)["remoteMcpServersConfig"] is null, "Claude index was merged or copied source credentials/settings.");
        record["title"] = "Renamed"; record["lastFocusedAt"] = 50; record["isArchived"] = true; PutJson(pb, record); File.Delete(copiedNew);
        var created = "local_" + Guid.NewGuid(); var cliCreated = Guid.NewGuid().ToString();
        PutJson(ClaudeIndexPath(desktop, b, created), ClaudeIndex(created, cliCreated, "Created in B", "target-private"));
        TransferOrganization(transfer, "b", "a", b, a);
        var returned = ReadObject(pa);
        Check(returned["sessionId"]!.GetValue<string>() == sessionA && returned["title"]!.GetValue<string>() == "Renamed" && returned["lastFocusedAt"]!.GetValue<int>() == 50
            && returned["isArchived"]!.GetValue<bool>() && returned["permissionMode"]!.GetValue<string>() == "source-private", "Claude A → B → A lost rename, order, archive, identity or stable session ID.");
        Check(!File.Exists(pn) && File.Exists(ClaudeIndexPath(desktop, a, created)), "Claude A → B → A lost deletion or creation.");
        TransferOrganization(transfer, "a", "b", a, b); Check(!File.Exists(ps) && !File.Exists(copiedNew), "Deleted Claude sessions reappeared.");
        Check(File.ReadAllText(tasks) == "scheduled-unchanged" && File.ReadAllText(transcript) == "transcript-unchanged", "Claude transfer touched schedules or transcripts.");
        var missing = new OrganizationAccount(Guid.NewGuid().ToString(), DesktopOrg);
        Check(new ClaudeCodeOrganizationAdapter(desktop).Prepare(missing, a).Count == 0, "Unobserved terminal-only account cleared Desktop organization.");
        Directory.CreateDirectory(Path.GetDirectoryName(ClaudeIndexPath(desktop, missing, sessionA))!);
        TransferOrganization(transfer, "missing", "a", missing, a);
        Check(Directory.GetFiles(Path.GetDirectoryName(pa)!, "local_*.json").Length == 0, "An observed empty source did not clear stale target indices.");
        return Task.CompletedTask;
    });
    private static Task ClaudeOrganizationSwitchAsync() => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories(); using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        using var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => {}, (_,_) => {});
        await accounts.SetEnabledAsync(true); await accounts.BeginAsync("Second"); fixture.Login(ClaudeSecond); await accounts.RefreshAsync(true);
        var first = accounts.State.Accounts.Single(x => x.Account == DesktopAccount); var second = accounts.State.Accounts.Single(x => x.Account == ClaudeSecond);
        var a = new OrganizationAccount(DesktopAccount, DesktopOrg); var b = new OrganizationAccount(ClaudeSecond, DesktopOrg);
        var cli = Guid.NewGuid().ToString(); var session = "local_" + Guid.NewGuid();
        var source = ClaudeIndexPath(fixture.Desktop, b, session); var target = ClaudeIndexPath(fixture.Desktop, a, session);
        PutJson(source, ClaudeIndex(session, cli, "Newest B", "b-only"));
        PutJson(target, ClaudeIndex(session, cli, "Old A", "a-only"));
        var before = File.ReadAllBytes(target); var authBefore = fixture.Digests();
        fixture.Archive.AfterWriteForTesting = n => { if (n == 3) throw new IOException("fixture credential failure"); };
        var failed = false; try { await accounts.SwitchAsync(first.Id); } catch (IOException) { failed = true; }
        Check(failed && File.ReadAllBytes(target).SequenceEqual(before) && authBefore.All(p => fixture.Digests()[p.Key] == p.Value), "Claude credential failure did not restore organization and auth exactly.");
        Check(accounts.State.ActiveId == second.Id, "Failed Claude switch changed active account.");
        fixture.Archive.AfterWriteForTesting = null; await accounts.SwitchAsync(first.Id);
        Check(ReadObject(target)["title"]!.GetValue<string>() == "Newest B" && accounts.State.ActiveId == first.Id, "Claude account activation did not transfer organization.");
        Check((await fixture.Archive.IdentifyAsync(null, default))?.Account == DesktopAccount, "Organization transfer changed Claude authentication semantics.");
        var edited = ReadObject(target); edited["title"] = "Newest A"; PutJson(target, edited);
        await accounts.SwitchAsync(second.Id);
        Check(ReadObject(source)["title"]!.GetValue<string>() == "Newest A", "Claude service round-trip did not use the latest outgoing organization.");
        // A broken organization file must not affect quota queries or cause an idle scan.
        AtomicFile.WriteText(target, "invalid-json-for-unrelated-operation");
        var quotaBefore = fixture.Digests(); await accounts.RefreshAsync(true);
        Check(quotaBefore.All(p => fixture.Digests()[p.Key] == p.Value) && !File.Exists(Path.Combine(paths.TransactionsDir, "organization-claude.bin")), "Quota refresh ran the organization transfer.");
    });
    private static Task OrganizationRecoveryAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories(); var desktop = Path.Combine(paths.AppHome, "desktop");
        var a = new OrganizationAccount(DesktopAccount, DesktopOrg); var b = new OrganizationAccount(ClaudeSecond, DesktopOrg);
        var session = "local_" + Guid.NewGuid(); var second = "local_" + Guid.NewGuid();
        var target = ClaudeIndexPath(desktop, b, session); var stale = ClaudeIndexPath(desktop, b, second);
        var cli = Guid.NewGuid().ToString();
        PutJson(ClaudeIndexPath(desktop, a, session), ClaudeIndex(session, cli, "After", "a"));
        PutJson(target, ClaudeIndex(session, cli, "Before", "b")); PutJson(stale, ClaudeIndex(second, Guid.NewGuid().ToString(), "Stale", "b"));
        var before = File.ReadAllBytes(target); var staleBefore = File.ReadAllBytes(stale);
        var transfer = Transfer(paths, new ClaudeCodeOrganizationAdapter(desktop));
        transfer.AfterWriteForTesting = _ => throw new IOException("simulated process termination");
        try { transfer.Prepare("a", "b", a, b).Apply(); } catch (IOException) { }
        Check(transfer.HasRecovery, "Interrupted organization transfer lost its journal.");
        var recovered = Transfer(paths, new ClaudeCodeOrganizationAdapter(desktop));
        var blocked = false; try { recovered.Recover("unrelated"); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && recovered.HasRecovery, "Recovery overwrote an unknown live identity.");
        recovered.Recover("a");
        Check(File.ReadAllBytes(target).SequenceEqual(before) && File.ReadAllBytes(stale).SequenceEqual(staleBefore) && !recovered.HasRecovery, "Interrupted multi-file transfer was not restored byte for byte.");
        for (var failure = 1; failure <= 2; failure++)
        {
            recovered.AfterWriteForTesting = count => { if (count == failure) throw new IOException("injected index write failure"); };
            var interrupted = recovered.Prepare("a", "b", a, b);
            try { interrupted.Apply(); } catch (IOException) { interrupted.Rollback(); }
            Check(File.ReadAllBytes(target).SequenceEqual(before) && File.ReadAllBytes(stale).SequenceEqual(staleBefore), "Organization rollback was not exact after write " + failure);
        }
        recovered.AfterWriteForTesting = null;
        var change = recovered.Prepare("a", "b", a, b); change.Apply(); // Crash after credentials were installed, before journal archival.
        var latest = ReadObject(target); latest["title"] = "Newer legitimate target edits"; PutJson(target, latest);
        var latestBytes = File.ReadAllBytes(target);
        recovered.Recover("b");
        Check(!recovered.HasRecovery && File.ReadAllBytes(target).SequenceEqual(latestBytes) && !File.Exists(stale), "Completed-target recovery discarded newer client edits.");
        return Task.CompletedTask;
    });
    private static Task CodexOrganizationRecoveryAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
        var b = Snapshot("user-b", "account-b", "workspace-b", "b", 1_700_000_010);
        SeedProfiles(paths, a, ("AccountA", a), ("AccountB", b));
        var ca = new OrganizationAccount("account-a", "user-a"); var cb = new OrganizationAccount("account-b", "user-b");
        var path = Path.Combine(paths.CodexHome, ".codex-global-state.json"); PutJson(path, CodexOrganization(ca,cb)); var before = File.ReadAllBytes(path);
        var transfer = Transfer(paths, new CodexOrganizationAdapter(paths.CodexHome));
        transfer.Prepare(a.Identity.Fingerprint, b.Identity.Fingerprint, ca, cb).Apply();
        using (var service = CreateService(paths)) { await service.InitializeAsync(); }
        Check(File.ReadAllBytes(path).SequenceEqual(before) && !transfer.HasRecovery, "Service startup did not restore an interrupted source-side organization change.");
        transfer.Prepare(a.Identity.Fingerprint, b.Identity.Fingerprint, ca, cb).Apply();
        AtomicFile.WriteBytes(paths.ActiveAuthPath, b.Bytes);
        var latest = ReadObject(path); latest["unrelated-setting"] = "newer target edit"; PutJson(path, latest);
        using (var service = CreateService(paths))
        {
            await service.InitializeAsync();
            Check((await Read(paths.ActiveAuthPath)).Identity.Matches(b.Identity), "Recovery rolled back installed target credentials.");
        }
        Check(!transfer.HasRecovery && ReadObject(path)["unrelated-setting"]!.GetValue<string>() == "newer target edit", "Service startup lost a completed target change.");
    });
    private static Task OrganizationClientRaceAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories(); var a = new OrganizationAccount("a", "a"); var b = new OrganizationAccount("b", "b");
        var path = Path.Combine(paths.CodexHome, ".codex-global-state.json"); PutJson(path, CodexOrganization(a,b)); var before = File.ReadAllBytes(path);
        var running = false;
        var transfer = Transfer(paths, new CodexOrganizationAdapter(paths.CodexHome), () => { if (running) throw new InvalidOperationException("running fixture"); });
        transfer.AfterWriteForTesting = _ => running = true;
        var change = transfer.Prepare("a", "b", a, b);
        try { change.Apply(); } catch (InvalidOperationException) { }
        var blocked = false; try { change.Rollback(); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && transfer.HasRecovery, "A client opening mid-write was overwritten or lost its journal.");
        running = false; transfer.Recover("a");
        Check(File.ReadAllBytes(path).SequenceEqual(before) && !transfer.HasRecovery, "Deferred organization recovery was not exact.");
        running = true; blocked = false; try { transfer.Prepare("a", "b", a, b); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && File.ReadAllBytes(path).SequenceEqual(before), "Transfer wrote while the client was open.");
        return Task.CompletedTask;
    });
    private static Task OrganizationBoundariesAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories(); var a = new OrganizationAccount("a", "a"); var b = new OrganizationAccount("b", "b");
        var path = Path.Combine(paths.CodexHome, ".codex-global-state.json"); PutJson(path, CodexOrganization(a,b));
        var transfer = Transfer(paths, new CodexOrganizationAdapter(paths.CodexHome)); var before = File.ReadAllBytes(path);
        var cloudSelected = ReadObject(path);
        cloudSelected["selected-project"] = new JsonObject { ["type"] = "cloud", ["projectId"] = "source-cloud" };
        cloudSelected["electron-account-renderer-state-v1"]!["inactive"]![CodexOrganizationAdapter.Scope(b)]!["globals"]!["selected-project"] = new JsonObject { ["type"] = "local", ["projectId"] = "stale" };
        PutJson(path, cloudSelected);
        var adjusted = JsonNode.Parse(new CodexOrganizationAdapter(paths.CodexHome).Prepare(a,b).Single().After!)!;
        Check(adjusted["electron-account-renderer-state-v1"]!["inactive"]![CodexOrganizationAdapter.Scope(b)]!["globals"]!["selected-project"] is null, "A stale local selection survived replacement with a cloud-selected source.");
        cloudSelected["electron-account-renderer-state-v1"]!["owner"] = 12; PutJson(path, cloudSelected);
        var unknownOwnerRejected = false; try { new CodexOrganizationAdapter(paths.CodexHome).Prepare(a,b); } catch (InvalidDataException) { unknownOwnerRejected = true; }
        Check(unknownOwnerRejected, "Unknown Codex owner schema was accepted.");
        AtomicFile.WriteBytes(path, before);
        var change = transfer.Prepare("a", "b", a, b); var external = ReadObject(path); external["unrelated-setting"] = "edited concurrently"; PutJson(path, external);
        var blocked = false; try { change.Apply(); } catch (IOException) { blocked = true; }
        Check(blocked && !transfer.HasRecovery && ReadObject(path)["unrelated-setting"]!.GetValue<string>() == "edited concurrently", "Preflight overwrote a concurrent organization edit.");
        AtomicFile.WriteBytes(path, before); change = transfer.Prepare("a", "b", a, b); change.Apply();
        external = ReadObject(path); external["unrelated-setting"] = "after-write external edit"; PutJson(path, external);
        blocked = false; try { change.Rollback(); } catch (IOException) { blocked = true; }
        Check(blocked && transfer.HasRecovery && ReadObject(path)["unrelated-setting"]!.GetValue<string>() == "after-write external edit", "Rollback discarded an external client edit.");
        var outside = Path.Combine(paths.AppHome, "not-an-index.json");
        ClaudeSessionArchive.SaveProtected(Path.Combine(paths.TransactionsDir, "organization-codex.bin"), new OrganizationJournal("fixture", "codex", Path.GetFullPath(paths.CodexHome), "a", "b", "prepared", [new(outside, null, [1])]));
        blocked = false; try { transfer.Recover("a"); } catch (InvalidDataException) { blocked = true; }
        Check(blocked && !File.Exists(outside), "A journal escaped the provider's fixed paths.");
        var desktop = Path.Combine(paths.AppHome, "desktop"); var ca = new OrganizationAccount(DesktopAccount, DesktopOrg); var cb = new OrganizationAccount(ClaudeSecond, DesktopOrg);
        var cli = Guid.NewGuid().ToString(); var s1 = "local_" + Guid.NewGuid(); var s2 = "local_" + Guid.NewGuid();
        PutJson(ClaudeIndexPath(desktop,ca,s1), ClaudeIndex(s1,cli,"one","a")); PutJson(ClaudeIndexPath(desktop,ca,s2), ClaudeIndex(s2,cli,"duplicate","a"));
        blocked = false; try { new ClaudeCodeOrganizationAdapter(desktop).Prepare(ca,cb); } catch (InvalidDataException) { blocked = true; }
        Check(blocked && !Directory.Exists(Path.GetDirectoryName(ClaudeIndexPath(desktop,cb,s1))), "Ambiguous Code indices modified the target.");
        return Task.CompletedTask;
    });
}
