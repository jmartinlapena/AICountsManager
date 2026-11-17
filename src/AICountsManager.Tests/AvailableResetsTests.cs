using AICountsManager;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using C = System.Windows.Controls;
using W = System.Windows;
using M = System.Windows.Media;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static JsonObject ResetGrant(int count, DateTimeOffset end, string id = "redemption-handle-never-persist") => new()
    {
        ["id"] = id, ["type"] = "full", ["label"] = "Complete limits reset", ["resets_left"] = count, ["resets_total"] = count,
        ["paused"] = false, ["started"] = true, ["starts_at"] = end.AddDays(-30).ToString("O"), ["ends_at"] = end.ToString("O"),
        ["clears"] = new JsonArray("five_hour", "seven_day", "seven_day_overage_included"), ["usable_now"] = false,
    };
    private static JsonObject ClaudeResetFixture(int count = 1) => new()
    { ["eligible"] = true, ["grants"] = new JsonArray(ResetGrant(count, DateTimeOffset.UtcNow.AddDays(10))) };
    private static ResetInventory InventoryFixture(int count = 1) => new(count,
        [new("codex_rate_limits", "Reset semanal", "Semanal de Codex", count, DateTimeOffset.UtcNow.AddDays(10))], DateTimeOffset.UtcNow);

    private static Task AvailableResetParsingAsync()
    {
        var now = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
        var credit = new JsonObject { ["id"] = "grant", ["reset_type"] = "codex_rate_limits", ["status"] = "available",
            ["is_supported_by_plan"] = true, ["expires_at"] = "2026-12-10T23:00:00+01:00" };
        var root = new JsonObject { ["available_count"] = 3, ["credits"] = new JsonArray(credit) };
        var codex = ResetInventoryParser.Codex(Element(root.ToJsonString()), true, now);
        Check(codex.CountAt(now) == 3 && !codex.DetailsComplete && codex.Items.Single().Scope == "Semanal de Codex", "Partial Codex details replaced the server count or plan scope.");
        root["available_count"] = 1;
        root["credits"]!.AsArray().Add(credit.DeepClone());
        codex = ResetInventoryParser.Codex(Element(root.ToJsonString()), false, now);
        Check(codex.DetailsComplete && codex.Items.Count == 1 && codex.Items[0].ExpiresAtUtc == DateTimeOffset.Parse("2026-12-10T22:00:00Z"), "Codex duplicate or offset date parsing failed.");
        Check(!JsonSerializer.Serialize(codex).Contains("grant"), "Redemption identifier reached display cache.");
        Check(codex.CountAt(now.AddMonths(1)) == 0, "An expired stored reset remained available.");
        root["credits"]!.AsArray().RemoveAt(1); credit["expires_at"] = "10/12/2026 23:00";
        Check(!ResetInventoryParser.Codex(Element(root.ToJsonString()), false, now).DetailsComplete, "Ambiguous date was interpreted in the current culture.");
        credit["expires_at"] = null;
        Check(ResetInventoryParser.Codex(Element(root.ToJsonString()), false, now).Items.Single() is { ExpiresAtUtc: null, ExpiryKnown: true }, "Explicitly unbounded expiry was lost.");
        credit.Remove("expires_at");
        Check(!ResetInventoryParser.Codex(Element(root.ToJsonString()), false, now).Items.Single().ExpiryKnown, "Missing expiry became no expiry.");

        var full = ResetGrant(2, now.AddDays(10)); full["starts_at"] = now.AddDays(-1).ToString("O");
        var model = ResetGrant(1, now.AddDays(2), "model-grant"); model["starts_at"] = now.AddDays(-1).ToString("O");
        model["label"] = "Fable model reset"; model["clears"] = new JsonArray("seven_day_fable"); model["gives_back_weekly"] = true;
        var paused = (JsonObject)full.DeepClone(); paused["id"] = "paused"; paused["paused"] = true;
        var future = (JsonObject)full.DeepClone(); future["id"] = "future"; future["starts_at"] = now.AddDays(1).ToString("O");
        var expired = (JsonObject)full.DeepClone(); expired["id"] = "expired"; expired["ends_at"] = now.AddSeconds(-1).ToString("O");
        var claudeRoot = new JsonObject { ["cedar_ember"] = new JsonObject { ["eligible"] = true,
            ["grants"] = new JsonArray(full, model, paused, future, expired, full.DeepClone()) } };
        var claude = ResetInventoryParser.Claude(Element(claudeRoot.ToJsonString()), now);
        Check(claude.AvailableCount == 3 && claude.Items.Count == 2 && claude.Items[0].UsableNow == false, "Claude count must sum quantities, include gated grants and exclude invalid periods.");
        Check(claude.Items[1].Label == "Reset de modelo" && claude.Items[1].Scope.Contains("Fable"), "type=full incorrectly expanded a model reset to every limit.");
        full["ends_at"] = "bad date";
        Check(ResetInventoryParser.Claude(Element(claudeRoot.ToJsonString()), now).Status == "unknown", "Malformed grants became a confirmed total.");
        Check(ResetInventoryParser.Claude(Element("{}"), now).AvailableCount is null, "Missing reset support became zero inventory.");
        return Task.CompletedTask;
    }
    private static async Task AvailableResetTransportAsync()
    {
        var snapshot = Snapshot("reset-user", "reset-account", "reset-org", "one", 100);
        var handler = new SessionHttp(); using var http = new HttpClient(handler);
        var quota = "{\"rate_limit\":{\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":604800}}}";
        handler.Reply = (r, _) =>
        {
            Check(r.Method == HttpMethod.Get && r.Headers.GetValues("ChatGPT-Account-Id").Single() == snapshot.AccountId, "Codex inventory changed account or request method.");
            return Task.FromResult(Response(r.RequestUri!.AbsolutePath.EndsWith("usage") ? quota
                : "{\"available_count\":2,\"credits\":[]}"));
        };
        var client = new UsageClient(http);
        var reading = await client.ReadAsync(snapshot, "no-such-config", default);
        Check(reading.Status == "ok" && reading.Weekly.UsedPercent == 20 && reading.Resets?.AvailableCount == 2 && handler.Calls == 2, "Quota plus independent inventory failed.");
        handler.Reply = (r, _) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("usage") ? Response(quota) : new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)) } });
        reading = await client.ReadAsync(snapshot, "no-such-config", default);
        Check(reading.Status == "ok" && reading.Weekly.UsedPercent == 20 && reading.Resets is { HttpStatus: 429, RetryAfterSeconds: 120 }, "Optional 429 discarded quota or Retry-After.");
        handler.Reply = (r, _) => r.RequestUri!.AbsolutePath.EndsWith("usage") ? Task.FromResult(Response(quota)) : throw new TaskCanceledException();
        Check((await client.ReadAsync(snapshot, "no-such-config", default)).Weekly.UsedPercent == 20, "Optional reset timeout discarded successful quota.");
        using var cancel = new CancellationTokenSource(); handler.Reply = (r, token) =>
        { if (!r.RequestUri!.AbsolutePath.EndsWith("usage")) { cancel.Cancel(); token.ThrowIfCancellationRequested(); } return Task.FromResult(Response(quota)); };
        var cancelled = false; try { await client.ReadAsync(snapshot, "no-such-config", cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Caller cancellation was swallowed by optional inventory.");

        var source = new SessionFixture(); var calls = new List<string>();
        handler.Reply = (r, _) =>
        {
            calls.Add(r.RequestUri!.PathAndQuery);
            if (r.RequestUri.AbsolutePath.EndsWith("profile")) return Task.FromResult(Response("{}"));
            Check(r.RequestUri.Query == "?cedar_ember=1" && r.Headers.UserAgent.ToString().Contains("claude-cli/"), "Claude inventory capability headers missing.");
            var body = JsonNode.Parse(ClaudeFixture())!.AsObject(); body["cedar_ember"] = ClaudeResetFixture(2); return Task.FromResult(Response(body.ToJsonString()));
        };
        var claude = await new ClaudeQuotaProvider(source, http).ReadAsync(new CurrentQuotaRequest(), default);
        Check(claude.Status == "ok" && claude.Resets?.AvailableCount == 2 && calls.Count == 2 && claude.Quotas.Count == 4, "Claude inventory added a separate success request or changed quotas.");
        calls.Clear(); handler.Reply = (r, _) =>
        {
            calls.Add(r.RequestUri!.PathAndQuery);
            return Task.FromResult(r.RequestUri.Query.Length > 0 ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : Response(r.RequestUri.AbsolutePath.EndsWith("profile") ? "{}" : ClaudeFixture()));
        };
        claude = await new ClaudeQuotaProvider(source, http).ReadAsync(new CurrentQuotaRequest(), default);
        Check(claude.Status == "ok" && claude.Resets?.AvailableCount is null && calls.Count == 3, "Optional Claude capability rejection broke existing quotas.");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
        {
            calls.Clear(); handler.Reply = (r, _) => { calls.Add(r.RequestUri!.PathAndQuery); return Task.FromResult(new HttpResponseMessage(status)); };
            await new ClaudeQuotaProvider(source, http).ReadAsync(new CurrentQuotaRequest(), default);
            Check(calls.Count == 1, "Authentication failure or rate limiting triggered a fallback request.");
        }
    }
    private static Task AvailableResetCacheAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("one", "a", "org", "one", 100); var b = Snapshot("two", "b", "org", "two", 100);
        SeedProfiles(paths, a, ("A", a), ("B", b)); var repo = Repository(paths);
        repo.SaveUsage("A", FakeUsageClient.Result() with { Resets = InventoryFixture(3) });
        repo.SaveUsage("B", FakeUsageClient.Result() with { Resets = InventoryFixture(1) });
        repo.SaveUsage("A", FakeUsageClient.Result() with { Resets = ResetInventory.Unknown(DateTimeOffset.UtcNow) });
        Check(repo.LoadUsageCache().Accounts["A"].Resets is { AvailableCount: 3, Status: "stale" }
            && repo.LoadUsageCache().Accounts["B"].Resets?.AvailableCount == 1, "Cache failure lost or mixed reset metadata.");
        repo.SaveUsage("A", UsageRecord.FromError("offline"));
        Check(repo.LoadUsageCache().Accounts["A"].Weekly.UsedPercent == 20 && repo.LoadUsageCache().Accounts["A"].Resets?.AvailableCount == 3, "Quota failure discarded saved resets.");
        repo.SaveUsage("A", FakeUsageClient.Result() with { Resets = new(0, [], DateTimeOffset.UtcNow) });
        Check(repo.LoadUsageCache().Accounts["A"].Resets?.AvailableCount == 0, "Confirmed empty inventory resurrected cached credits.");
        using var service = CreateService(paths);
        Check(service.BuildDashboardState().Profiles.Single(p => p.Name == "B").Resets?.AvailableCount == 1, "Dashboard lost reset metadata.");
        var raw = JsonNode.Parse(File.ReadAllText(paths.UsageCachePath))!; raw["Accounts"]!["A"]!["Resets"] = new JsonObject { ["Items"] = "broken" };
        AtomicFile.WriteText(paths.UsageCachePath, raw.ToJsonString());
        Check(repo.LoadUsageCache().Accounts["A"].Weekly.UsedPercent == 20, "Malformed optional cache invalidated quota history.");
        var providerCache = JsonSerializer.SerializeToNode(SampleAntigravity())!.AsObject();
        providerCache["Resets"] = new JsonObject { ["Items"] = "broken" };
        var recovered = ProviderUsage.ReadCache(Element(providerCache.ToJsonString()));
        Check(recovered is { Resets: null } && recovered.Quotas.Count > 0, "Malformed provider reset cache discarded the quota snapshot.");
        await Task.CompletedTask;
    });
    private static Task AvailableResetBackoffAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("one", "a", "org", "one", 100);
        SeedProfiles(paths, a, Enumerable.Range(0, 5).Select(i => ("Account " + i, Snapshot("u" + i, "a" + i, "org", "one", 100))).ToArray());
        var client = new BatchUsageClient((_, _) => Task.FromResult(FakeUsageClient.Result() with
        { Resets = ResetInventory.Unknown(DateTimeOffset.UtcNow, 429, 120) }));
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), client, false);
        await service.RefreshAllUsageAsync();
        Check(client.Calls == 2, "Inventory Retry-After failed to stop the remaining account batch.");
        await service.RefreshAllUsageAsync();
        Check(client.Calls == 2 && Repository(paths).LoadUsageCache().Accounts.Values.All(u => u.Status == "ok"), "Inventory backoff was ignored or converted quota success to an error.");
    });
    private static Task AvailableResetBulkAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("one", "a", "org", "one", 100); var b = Snapshot("two", "b", "org", "two", 100);
        SeedProfiles(paths, a, ("A", a), ("B", b)); var auth = File.ReadAllBytes(paths.ActiveAuthPath);
        var client = new BatchUsageClient((snapshot, _) => Task.FromResult(FakeUsageClient.Result() with { Resets = InventoryFixture(snapshot.AccountId == "a" ? 3 : 1) }));
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), client, false);
        await service.RefreshAllUsageAsync();
        var cache = Repository(paths).LoadUsageCache();
        Check(cache.Accounts["A"].Resets?.AvailableCount == 3 && cache.Accounts["B"].Resets?.AvailableCount == 1
            && auth.SequenceEqual(File.ReadAllBytes(paths.ActiveAuthPath)), "Bulk reset inventory did not preserve account isolation and credentials.");

        using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var original = fixture.Handler.Reply;
        fixture.Handler.Reply = async (request, token) =>
        {
            var response = await original(request, token);
            if (!request.RequestUri!.AbsolutePath.EndsWith("profile"))
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                body["cedar_ember"] = ClaudeResetFixture(request.Headers.Authorization?.Parameter?.Contains("second") == true ? 2 : 1);
                response.Dispose(); return Response(body.ToJsonString());
            }
            return response;
        };
        using var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => { }, (_, _) => { });
        await accounts.SetEnabledAsync(true);
        using (var restored = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => { }, (_, _) => { }))
            Check(restored.Accounts.Single().Resets is { AvailableCount: 1, Status: "stale" }, "Unregistered Claude session did not retain its display cache.");
        await accounts.BeginAsync("Claude second"); fixture.Login(ClaudeSecond); await accounts.RefreshAsync(true);
        Check(accounts.Accounts.Count == 2 && accounts.Accounts.Select(r => r.Resets?.AvailableCount).Order().SequenceEqual(new int?[] { 1, 2 }), "Claude bulk reset counts mixed saved accounts.");
        var before = fixture.Digests(); await accounts.RefreshAsync(true);
        Check(before.All(p => fixture.Digests()[p.Key] == p.Value), "Reading reset inventories changed Claude sessions.");
    });
    private static Task AvailableResetUiAsync() => RunOnUiThreadAsync(() =>
    {
        foreach (var theme in new[] { new UiTheme(true, false, false), new UiTheme(false, false, false), new UiTheme(true, false, true) })
        {
            using var codex = new AccountRow(theme); using var claude = new CurrentProviderCard(theme);
            var inventory = InventoryFixture(3);
            var profile = SampleProfile("Personal", true) with { Resets = inventory };
            var other = SampleAntigravity() with { Provider = ProviderIds.Claude, Resets = inventory };
            codex.Update(profile, false); claude.Update(other);
            var views = new[] { UiField<AvailableResetsView>(codex, "_resets"), UiField<AvailableResetsView>(claude, "_resets") };
            foreach (var view in views)
            {
                Check(view.Visibility == W.Visibility.Visible && !view.Expanded, "Reset row must begin collapsed.");
                UiClick(UiField<C.Button>(view, "_toggle"));
                Check(view.Expanded && UiField<C.StackPanel>(view, "_details").Children.Count > 0 && view.ToolTip is null && !view.HasAnimatedProperties, "Disclosure did not show local data or introduced hover/animation.");
            }
            codex.Update(profile, false); claude.Update(other);
            Check(views.All(v => v.Expanded) && ReferenceEquals(views[1], UiField<AvailableResetsView>(claude, "_resets")), "Provider refresh rebuilt or collapsed the disclosure.");
            codex.Update(profile with { Resets = new(0, [], DateTimeOffset.UtcNow) }, false); claude.Update(other with { Resets = null });
            Check(views.All(v => v.Visibility == W.Visibility.Collapsed), "Empty or unsupported inventory took vertical space.");
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        var reset = new AvailableReset("full", "Reset", "", 1, DateTimeOffset.Parse("2026-12-10T22:00:00Z"));
        Check(AvailableResetsView.ExpiryLabel(reset, zone).Contains("23:00") && AvailableResetsView.ExpiryLabel(reset, zone).Contains("+01:00"), "Winter local expiration is wrong.");
        reset = reset with { ExpiresAtUtc = DateTimeOffset.Parse("2027-06-10T22:00:00Z") };
        Check(AvailableResetsView.ExpiryLabel(reset, zone).Contains("00:00") && AvailableResetsView.ExpiryLabel(reset, zone).Contains("+02:00"), "Summer expiration did not cross into the next local day.");
        Check(AvailableResetsView.ExpiryLabel(reset, TimeZoneInfo.Utc).Contains("22:00"), "Zone was fixed or cached in the presentation.");
        return Task.CompletedTask;
    });

    private static Task RenderAvailableResetsAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        Directory.CreateDirectory(output);
        using var service = CreateService(paths);
        var now = DateTimeOffset.UtcNow;
        var inventory = new ResetInventory(3, Enumerable.Range(0, 3).Select(i => new AvailableReset("codex_rate_limits", "Reset semanal", "Semanal de Codex", 1, now.AddDays(13 + i * 4))).ToArray(), now);
        var profile = SampleProfile("Personal", true) with { Email = "personal@ejemplo.com", PlanType = "prolite", SessionLimitNotApplicable = true,
            WeeklyWindow = new(28, 10080, now.AddDays(4).AddHours(19).ToUnixTimeSeconds()), WeeklyRemainingPercent = 72, Resets = inventory, CheckedAt = now.ToString("O") };
        var claude = new ProviderUsage(ProviderIds.Claude, "demo", "Claude personal", "claude@ejemplo.com", "Pro", now.ToString("O"), "ok", null,
            [new("five_hour", "Claude", "Sesión · 5 h", 14, 300, now.AddHours(2).AddMinutes(14).ToUnixTimeSeconds()),
             new("seven_day", "Claude", "Semanal", 42, 10080, now.AddDays(3).AddHours(6).ToUnixTimeSeconds())],
            Resets: new(1, [new("full", "Reset completo", "Sesión de 5 h y semanal", 1, now.AddDays(23))], now));
        var state = SampleState() with { ActiveProfile = profile, Profiles = [profile], OtherAccounts = [claude] };
        using var form = new WidgetForm(service, new UiTheme(true, false, false, M.Color.FromRgb(70, 74, 53), M.Color.FromRgb(137, 155, 93)), false);
        form.Visual.Topmost = false; form.Visual.Left = form.Visual.Top = -20000; form.Visual.Height = 650;
        form.ApplyState(state); form.SetHistoryPreview(ProviderHistory.Merge(new Dictionary<string, TokenHistorySnapshot>
        { [ProviderIds.Codex] = SampleHistory(), [ProviderIds.Claude] = new([new(DateTime.Today.ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["Claude"] = 220000 })], false, now.ToString("O"), null) }));
        UiClick(UiField<C.Button>(UiField<ProviderOverview>(form, "_overview"), "_quotas"));
        form.Visual.Show(); form.Visual.UpdateLayout();
        SaveResetDemo(form.Visual, Path.Combine(output, "resets-plegados.png"));
        UiField<AvailableResetsView>(form.Rows.Single(), "_resets").SetExpanded(true);
        var external = UiField<Dictionary<string, CurrentProviderCard>>(form, "_externalRows").Values.Single();
        UiField<AvailableResetsView>(external, "_resets").SetExpanded(true);
        form.Visual.Height = 825; form.Visual.UpdateLayout();
        SaveResetDemo(form.Visual, Path.Combine(output, "resets-desplegados.png"));
        Console.WriteLine("Real WPF controls rendered using synthetic accounts and quotas.");
        return Task.CompletedTask;
    }));

    private static void SaveResetDemo(W.Window window, string output)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var bitmap = new M.Imaging.RenderTargetBitmap((int)(window.ActualWidth * 2), (int)(window.ActualHeight * 2), 192, 192, M.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new M.Imaging.PngBitmapEncoder(); png.Frames.Add(M.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(output); png.Save(file);
    }

    private static Task MeasureAvailableResetsAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        using var service = CreateService(paths);
        using var form = new WidgetForm(service, startClock: false);
        form.Visual.Topmost = false; form.Visual.Left = form.Visual.Top = -20000;
        var state = SampleState(); form.ApplyState(state); form.Visual.Show();
        await Task.Delay(3000); form.Hide(); await Task.Delay(1000);
        var samples = new List<object> { await SampleResourcesAsync("Panel oculto · sin inventario de resets", form.AcrylicEnabled) };
        form.ApplyState(state with { Profiles = state.Profiles.Select(p => p with { Resets = InventoryFixture(3) }).ToArray(),
            OtherAccounts = [new(ProviderIds.Claude, "demo", "Claude", "claude@example.test", "Pro", DateTimeOffset.UtcNow.ToString("O"), "ok", null,
                ClaudeQuotaProvider.Parse(Element(ClaudeFixture())), Resets: InventoryFixture())] });
        foreach (var row in form.Rows) UiField<AvailableResetsView>(row, "_resets").SetExpanded(true);
        foreach (var row in UiField<Dictionary<string, CurrentProviderCard>>(form, "_externalRows").Values) UiField<AvailableResetsView>(row, "_resets").SetExpanded(true);
        form.Visual.Show(); await Task.Delay(1000); form.Hide(); await Task.Delay(1000);
        samples.Add(await SampleResourcesAsync("Panel oculto · cuatro cuentas con inventarios desplegados", form.AcrylicEnabled));
        samples.Add(await SampleResourcesAsync("Panel oculto · reposo estable tras cargar los inventarios", form.AcrylicEnabled));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(samples));
    }, 60));
}
