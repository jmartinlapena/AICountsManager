using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AICountsManager;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static Task ReviewCodexQuotaAsync() => InTemporaryStoreAsync(async paths =>
    {
        var auth = Snapshot("u", "account", "workspace", "1", 1700000000);
        SeedProfiles(paths, auth, ("Personal", auth));
        var handler = new SessionHttp();
        using var http = new HttpClient(handler);
        var client = new UsageClient(http);
        var payload = PlanPayload("prolite").ToJsonString();
        var requested = new List<Uri>();
        handler.Reply = (request, _) =>
        {
            Check(request.Headers.Authorization?.Parameter == auth.AccessToken
                && request.Headers.GetValues("ChatGPT-Account-Id").Single() == auth.AccountId,
                "The authorized request did not use the selected account.");
            requested.Add(request.RequestUri!);
            return Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/usage") ? payload : """{"available_count":0}"""));
        };
        foreach (var url in new[] { "https://example.test", "https://chatgpt.com.example.test", "https://chatgpt.com@example.test",
            "https://user@chatgpt.com", "http://127.0.0.1.example.test", "http://localhost", "https://chatgpt.com:8443",
            "https://chatgpt.com/backend-api?redirect=x", "https://chatgpt.com/backend-api#fragment", "https://chatgpt.com/unrelated" })
        {
            await File.WriteAllTextAsync(paths.CodexConfigPath, $"chatgpt_base_url = \"{url}\"");
            Check((await client.ReadAsync(auth, paths.CodexConfigPath, default)).Status == "error" && handler.Calls == 0,
                "A disallowed destination reached HTTP: " + url);
        }
        await File.WriteAllTextAsync(paths.CodexConfigPath, "chatgpt_base_url = 'https://CHAT.OPENAI.COM/backend-api/' # official");
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), client, false);
        var valid = await service.RefreshActiveProfileAsync();
        Check(valid is { Status: "ok", SessionLimitNotApplicable: true, Weekly.UsedPercent: 28 }
            && requested.Select(u => u.AbsoluteUri).SequenceEqual([
                "https://chat.openai.com/backend-api/wham/usage", "https://chat.openai.com/backend-api/wham/rate-limit-reset-credits"]),
            "Official URI or valid weekly-only quota changed.");
        Check(service.BuildTrayTooltip() == "Personal · 72 % semanal restante", "Weekly-only plan lost its tray percentage.");
        foreach (var invalid in new[] { "{}", "null", "[]", "{broken", """{"rate_limit":{"allowed":true,"primary_window":{"limit_window_seconds":604800}}}""",
            """{"rate_limit":{"primary_window":{"used_percent":"bad","limit_window_seconds":604800}}}""",
            """{"rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":3600}}}""" })
        {
            payload = invalid;
            var calls = handler.Calls;
            var result = await service.RefreshActiveProfileAsync();
            var cached = Repository(paths).LoadUsageCache().Accounts["Personal"];
            Check(result?.Status == "error" && handler.Calls == calls + 1 && cached.CheckedAt == valid!.CheckedAt
                && cached.Weekly.UsedPercent == 28 && cached.Status == "error", "Invalid Codex response replaced the last valid observation: " + invalid);
        }
    });

    private static Task ReviewProviderBackoffAsync() => InTemporaryStoreAsync(async paths =>
    {
        var auth = Snapshot("u", "a", "w", "1", 1700000000); SeedProfiles(paths, auth, ("A", auth));
        var rateLimited = UsageRecord.FromError("rate limited") with { HttpStatus = 429, RetryAfterSeconds = 300 };
        var client = new BatchUsageClient((_, _) => Task.FromResult(rateLimited));
        var other = new FakeQuotaProvider();
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), client, false, other);
        await service.RefreshActiveProfileAsync();
        Check((await service.RefreshAllUsageAsync()).Contains("Codex") && client.Calls == 1 && other.Calls == 1,
            "An automatic Codex 429 did not pause the manual Codex query or blocked another provider.");
        var retryField = typeof(AICountsManagerService).GetField("_codexRetryAfter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        retryField.SetValue(service, DateTimeOffset.MinValue);
        await service.RefreshAllUsageAsync();
        await service.RefreshActiveProfileAsync();
        Check(client.Calls == 2, "A manual Codex 429 did not pause the periodic query.");
    });

    private static Task ReviewClaudeBackoffAsync() => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories();
        using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var name = Repository(paths).SanitizeProfileName;
        var root = Path.Combine(paths.AppHome, "provider-accounts", "claude");
        var index = Path.Combine(root, "accounts.json");
        var cache = Path.Combine(paths.AppHome, "cache", "claude-accounts.json");
        using (var setup = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, name, () => { }, (_, _) => { }))
        {
            var normal = fixture.Handler.Reply;
            fixture.Handler.Reply = async (request, token) =>
            {
                var reply = await normal(request, token);
                if (request.RequestUri!.AbsolutePath.EndsWith("profile")) return reply;
                using (reply)
                {
                    var body = JsonNode.Parse(await reply.Content.ReadAsStringAsync(token))!.AsObject();
                    body["cedar_ember"] = ClaudeResetFixture(2);
                    return Response(body.ToJsonString());
                }
            };
            await setup.SetEnabledAsync(true);
            await setup.BeginAsync("Pendiente");
            var first = setup.State.Accounts.Single();
            var second = new ClaudeAccount(Guid.NewGuid().ToString("N"), "Nueva", ClaudeSecond, DesktopOrg, null, null);
            AtomicFile.WriteText(index, JsonSerializer.Serialize(new ClaudeAccountState([first, second], first.Id)));
            var readings = JsonNode.Parse(File.ReadAllText(cache))!.AsObject();
            var firstReading = JsonSerializer.Deserialize<ProviderUsage>(readings[first.Id]!.ToJsonString())!;
            // Reproduce a contaminated cache written by 3.4.0.
            readings[second.Id] = JsonSerializer.SerializeToNode(firstReading);
            AtomicFile.WriteText(cache, readings.ToJsonString());
            fixture.Login(DesktopAccount);
        }
        fixture.Handler.Reply = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        };
        using var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, name, () => { }, (_, _) => { });
        var calls = fixture.Handler.Calls;
        await accounts.RefreshAsync(true);
        await accounts.RefreshAsync(true);
        var rows = accounts.Accounts;
        Check(fixture.Handler.Calls == calls + 1 && rows.Count == 2, "The provider did not share only its wait time.");
        Check(rows.Single(a => a.Name != "Nueva") is { Status: "stale", HttpStatus: 429, Quotas.Count: > 0, Resets.AvailableCount: 2 },
            "The active account lost its own previous observation.");
        Check(rows.Single(a => a.Name == "Nueva") is { Quotas.Count: 0, Resets: null, Error: "Pendiente de consultar", HttpStatus: null },
            "An unqueried account inherited another account's reading or error.");
    });

    private static Task ReviewClaudeIndexAsync() => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories();
        using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var root = Path.Combine(paths.AppHome, "provider-accounts", "claude");
        Directory.CreateDirectory(root);
        var index = Path.Combine(root, "accounts.json");
        var config = Path.Combine(paths.AppHome, "config", "provider-claude.json");
        var name = Repository(paths).SanitizeProfileName;
        var auth = Snapshot("u", "codex", "w", "1", 1700000000); SeedProfiles(paths, auth, ("Codex", auth));
        foreach (var invalid in new[] { "{broken", """{"Accounts":null}""", """{"Accounts":[null]}""" })
        {
            AtomicFile.WriteText(index, invalid);
            AtomicFile.WriteText(config, """{"claude":false}""");
            var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, name, () => { }, (_, _) => { });
            using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), new FakeUsageClient(), false, claudeAccounts: accounts);
            Check(service.BuildDashboardState().OtherAccounts?.Count == 0, "A disabled broken provider became visible.");
            await service.SetCurrentProviderEnabledAsync(ProviderIds.Claude, true);
            await service.RefreshAllUsageAsync();
            var dashboard = service.BuildDashboardState();
            Check(dashboard.Profiles.Single().Status == "ok" && dashboard.OtherAccounts?.Single().Error?.Contains("índice") == true,
                "A corrupt Claude index prevented Codex from working or hid the error.");
            Check(File.ReadAllText(index) == invalid, "A corrupt index was overwritten.");
        }
        foreach (var count in new[] { 99, 100 })
        {
            var filled = new ClaudeAccountState(Enumerable.Range(1, count).Select(i =>
                new ClaudeAccount(Guid.NewGuid().ToString("N"), "Cuenta " + i, Guid.NewGuid().ToString(), DesktopOrg, null, null)).ToArray());
            AtomicFile.WriteText(index, JsonSerializer.Serialize(filled));
            var before = File.ReadAllBytes(index); var session = fixture.Digests();
            using var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, name, () => { }, (_, _) => { });
            var rejected = false;
            try { await accounts.BeginAsync("Nueva"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("100")) { rejected = true; }
            Check(rejected && accounts.PendingName is null && before.SequenceEqual(File.ReadAllBytes(index))
                && session.All(p => fixture.Digests()[p.Key] == p.Value), "Enrollment exhausted its slots or modified the outgoing login.");
        }
    });

    private static Task ReviewClaudeHistoryCapacityAsync() => InTemporaryStoreAsync(async paths =>
    {
        var homePath = Path.Combine(paths.AppHome, "claude");
        var root = Path.Combine(homePath, "projects"); Directory.CreateDirectory(root);
        var knownPath = Path.Combine(root, "z-known.jsonl");
        var newPath = Path.Combine(root, "a-new.jsonl");
        var cache = Path.Combine(paths.AppHome, "cache", "claude-capacity.json");
        string Line(string id) => JsonSerializer.Serialize(new { type = "assistant", timestamp = DateTimeOffset.UtcNow.ToString("O"),
            message = new { id, model = "test", usage = new { input_tokens = 5, output_tokens = 5 } } }) + "\n";
        await File.WriteAllTextAsync(knownPath, Line("old"));
        var info = new FileInfo(knownPath);
        var known = new ClaudeTokenHistory.FileIndex { Offset = info.Length, Length = info.Length, Modified = info.LastWriteTimeUtc.Ticks, Created = info.CreationTimeUtc.Ticks, Completed = true };
        var entries = Enumerable.Range(0, 9999).ToDictionary(i => "cached-" + i,
            _ => new ClaudeTokenHistory.FileIndex { Modified = DateTime.UtcNow.Ticks });
        entries[AuthHash.Bytes(Encoding.UTF8.GetBytes(Path.GetFileName(knownPath)))] = known;
        AtomicFile.WriteText(cache, JsonSerializer.Serialize(entries));
        await File.WriteAllTextAsync(newPath, Line("new"));
        await File.AppendAllTextAsync(knownPath, Line("append"));
        var result = await new ClaudeTokenHistory(homePath, cache).RefreshAsync(default);
        Check(result.Partial && result.Days.Single().Total == 10, "Full Claude index blocked appended messages in known files or admitted an extra index.");

        entries["cached-0"].Modified = DateTime.UtcNow.AddDays(-370).Ticks;
        AtomicFile.WriteText(cache, JsonSerializer.Serialize(entries));
        result = await new ClaudeTokenHistory(homePath, cache).RefreshAsync(default);
        Check(!result.Partial && result.Days.Single().Total == 20, "Pruning an expired index did not make room for a new history file.");
    });

    private static Task ReviewTrayAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var auth = Snapshot("u", "a", "w", "1", 1700000000); SeedProfiles(paths, auth, ("Personal", auth));
        var process = new FakeProcessController(false);
        var usage = UsageClient.NormalizeWhamUsage(PlanPayload("prolite"));
        var client = new BatchUsageClient((_, _) => Task.FromResult(usage));
        using var service = new AICountsManagerService(paths, process, new FakeAppServerClient(), client, false);
        using var open = new AutoResetEvent(false);
        using var tray = new TrayApplicationContext(service, open);
        try
        {
            var icon = UiField<System.Windows.Forms.NotifyIcon>(tray, "_notifyIcon");
            await EventuallyAsync(() => Task.FromResult(icon.Text.Contains("72 % semanal")));
            Check(!tray.WidgetCreated && process.CaptureCalls == 0, "Background tooltip read created the flyout or enumerated processes.");
            open.Set();
            await EventuallyAsync(() => Task.FromResult(tray.WidgetVisible));
            var form = UiField<WidgetForm>(tray, "_widget");
            tray.ToggleWidget();
            Repository(paths).SaveUsage("Personal", usage with { Weekly = usage.Weekly with { UsedPercent = 35 } });
            var apply = typeof(TrayApplicationContext).GetMethod("ApplyState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            apply.Invoke(tray, null);
            Check(icon.Text.Contains("65 % semanal") && UiField<DashboardState>(form, "_state").ActiveProfile.WeeklyRemainingPercent == 72,
                "A hidden flyout was updated or the tray label did not refresh.");
            open.Set();
            await EventuallyAsync(() => Task.FromResult(tray.WidgetVisible));
            Check(ReferenceEquals(form, UiField<WidgetForm>(tray, "_widget"))
                && UiField<DashboardState>(form, "_state").ActiveProfile.WeeklyRemainingPercent == 65 && process.CaptureCalls == 0,
                "The manual activation signal did not reopen and refresh the resident window.");
        }
        finally { tray.ExitThread(); }
    }));
}
