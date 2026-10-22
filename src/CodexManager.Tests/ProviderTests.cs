using CodexManager;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace CodexManager.Tests;

internal static partial class Program
{
    private static TokenHistorySnapshot SampleAntigravityHistory() => new([new(DateTime.Today.ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["gemini-test"] = 240, ["claude-test"] = 60 })], false, DateTimeOffset.UtcNow.ToString("O"), null);
    private static JsonNode QuotaPayload()
    {
        var groups = new JsonArray();
        foreach (var (group, ids) in new[] { ("Gemini Models", new[] { "gemini-weekly", "gemini-5h" }), ("Claude and GPT models", new[] { "3p-weekly", "3p-5h" }), ("Gemini 4 Argon", new[] { "argon-weekly" }) })
        {
            var buckets = new JsonArray();
            foreach (var id in ids) buckets.Add(new JsonObject { ["bucketId"] = id, ["window"] = id.EndsWith("weekly") ? "weekly" : "5h", ["remainingFraction"] = .75,
                ["resetTime"] = DateTimeOffset.UtcNow.AddDays(4).ToString("O") });
            groups.Add(new JsonObject { ["displayName"] = group, ["buckets"] = buckets });
        }
        return new JsonObject { ["response"] = new JsonObject { ["groups"] = groups } };
    }
    private static ProviderUsage SampleAntigravity() => new(ProviderIds.Antigravity, "sample-id", "Cuenta personal", "cuenta@example.test", "Google AI Pro",
        DateTimeOffset.UtcNow.ToString("O"), "ok", null, AntigravityQuotaProvider.ParseSummary(QuotaPayload())!
            .Select((q, i) => q with { UsedPercent = new double[] { 38, 16, 62, 23, 81 }[i] }).ToArray());
    private static Task AntigravityQuotaParsingAsync()
    {
        var data = QuotaPayload(); var quotas = AntigravityQuotaProvider.ParseSummary(data)!;
        Check(quotas.Count == 5 && quotas.All(q => q.UsedPercent == 25), "Five authoritative limits not preserved.");
        Check(quotas.Count(q => q.Minutes == 10080) == 3 && quotas.Count(q => q.Minutes == 300) == 2, "Windows misclassified.");
        var groups = (JsonArray)data["response"]!["groups"]!;
        groups[0]!["buckets"]![0]!["remainingFraction"] = "invalid";
        groups[0]!["buckets"]![1]!["remainingFraction"] = 2;
        groups[1]!["buckets"]![0]!["remainingFraction"] = null;
        groups[1]!["buckets"]![1]!["remainingFraction"] = 0;
        groups[2]!["buckets"]![0]!["disabled"] = true;
        quotas = AntigravityQuotaProvider.ParseSummary(data)!;
        Check(quotas.Count == 4 && quotas.Take(3).All(q => q.UsedPercent is null) && quotas[3].UsedPercent == 100, "Unknown and exhausted quotas conflated.");
        data = QuotaPayload(); groups = (JsonArray)data["response"]!["groups"]!;
        ((JsonArray)groups[2]!["buckets"]!).Add(groups[2]!["buckets"]![0]!.DeepClone());
        Check(AntigravityQuotaProvider.ParseSummary(data)!.Count == 4, "Ambiguous Argon inferred.");
        Check(AntigravityQuotaProvider.ParseSummary(JsonNode.Parse("{}")) is null, "Unsupported endpoint appeared supported.");
        Check(AntigravityQuotaProvider.ParseSummary(JsonNode.Parse("{\"groups\":[]}"))!.All(q => q.UsedPercent is null), "Empty summary invented quota.");
        return Task.CompletedTask;
    }
    private sealed class FakeAntigravityConnection : IAntigravityConnection
    {
        public bool ChangingIdentity, MultipleIdentities; private int _status;
        public Task<IReadOnlyList<LocalQuotaEndpoint>> DiscoverAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<LocalQuotaEndpoint>>(
            MultipleIdentities ? [new(1, new("http://127.0.0.1:111/"), "fake"), new(2, new("http://127.0.0.1:222/"), "fake")] : [new(1, new("http://127.0.0.1:111/"), "fake")]);
        public Task<JsonNode?> CallAsync(LocalQuotaEndpoint endpoint, string method, CancellationToken token)
        {
            if (method == "RetrieveUserQuotaSummary") return Task.FromResult<JsonNode?>(QuotaPayload());
            var id = MultipleIdentities ? endpoint.ProcessId : ChangingIdentity ? ++_status : 1;
            return Task.FromResult<JsonNode?>(new JsonObject { ["userStatus"] = new JsonObject { ["email"] = "user" + id + "@example.test", ["name"] = "Test", ["userTier"] = new JsonObject { ["name"] = "Google AI Pro" } } });
        }
    }
    private static async Task AntigravityIdentityAsync()
    {
        var connection = new FakeAntigravityConnection(); var provider = new AntigravityQuotaProvider(connection);
        Check(!provider.CanSwitchAccounts, "External provider can switch accounts.");
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Quotas.Count == 5, "Current account not read.");
        connection.ChangingIdentity = true;
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "error", "Session change during query mixed identities.");
        connection.ChangingIdentity = false; connection.MultipleIdentities = true;
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "error", "Multiple different sessions silently selected.");
        var rejected = false;
        try { await new WindowsAntigravityConnection().CallAsync(new(1, new("https://example.com/"), "fake"), "GetUserStatus", default); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Local credential could be sent to a remote host.");
    }
    private sealed class FakeQuotaProvider : IQuotaProvider
    {
        public string Id { get; init; } = ProviderIds.Antigravity; public bool CanSwitchAccounts { get; init; }
        public ProviderUsage Result = SampleAntigravity(); public int Calls; public bool Fails;
        public Task<ProviderUsage> ReadAsync(QuotaRequest request, CancellationToken token)
        { Calls++; if (Fails) throw new IOException("Synthetic provider failure"); return Task.FromResult(Result); }
    }
    private static Task MultiProviderRefreshAsync() => InTemporaryStoreAsync(async paths =>
    {
        var auth = Snapshot("u", "a", "w", "g", 1700000000); SeedProfiles(paths, auth, ("Account", auth));
        var before = File.ReadAllBytes(paths.ActiveAuthPath); var external = new FakeQuotaProvider(); var usage = new FakeUsageClient();
        using var service = new CodexManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), usage, false, external, Path.Combine(paths.AppHome, "empty-gemini"));
        await service.RefreshAllUsageAsync();
        var state = service.BuildDashboardState();
        Check(state.Profiles.Count == 1 && state.OtherAccounts?.Single().Quotas.Count == 5, "Shared refresh lost a provider.");
        Check(before.SequenceEqual(File.ReadAllBytes(paths.ActiveAuthPath)) && Repository(paths).ListProfiles().Count == 1, "External refresh changed credentials or saved a profile.");
        Check(usage.Calls == 1 && external.Calls == 1, "Unexpected duplicate query.");
        await service.SetAntigravityEnabledAsync(false);
        Check(service.BuildDashboardState().OtherAccounts?.Count == 0 && service.BuildDashboardState().Profiles.Count == 1, "Disabling provider removed Codex.");
        external.Fails = true; await service.SetAntigravityEnabledAsync(true);
        Check(service.BuildDashboardState().OtherAccounts?.Single().Status == "error", "Provider failure escaped or was hidden.");
        Check(service.BuildDashboardState().Profiles.Single().Status == "ok", "Antigravity failure damaged Codex quota.");
    });
    private static Task ProviderRegistryAsync() => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories();
        var failed = new FakeQuotaProvider { Fails = true };
        var next = new FakeQuotaProvider { Id = "test-client", Result = SampleAntigravity() with { Provider = "test-client" } };
        var registry = new CurrentProviderRegistry(paths, [failed, next]);
        await registry.RefreshAsync(default);
        Check(registry.Accounts.Count == 2 && registry.Accounts.Single(a=>a.Provider=="test-client").Fresh, "Provider failure prevented another adapter from refreshing.");
        await registry.SetEnabledAsync(ProviderIds.Antigravity, false, default);
        Check(registry.Accounts.Single().Provider == "test-client", "Enablement leaked between providers.");
        var restored = new CurrentProviderRegistry(paths, [failed, next]);
        Check(!restored.IsEnabled(ProviderIds.Antigravity) && restored.IsEnabled("test-client"), "Provider enablement did not persist independently.");
        using var service = new CodexManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, next);
        await service.RefreshCurrentProvidersAsync();
        Check(service.BuildDashboardState().Profiles.Count == 0 && service.BuildDashboardState().UpdatedLabel is not null,
            "Provider-only installation lost the last-update footer.");
    });
    private static Task ProviderCacheIdentityAsync() => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories(); var fake = new FakeQuotaProvider(); var store = new CurrentProviderStore(paths, fake);
        await store.RefreshAsync(default); Check(store.Snapshot?.Identity == "sample-id", "No provider cache.");
        var reopened = new CurrentProviderStore(paths, fake); Check(reopened.Snapshot?.Status == "stale", "Cached account claimed current without verifying.");
        fake.Result = fake.Result with { Identity = "different", Email = "different@example.test", Quotas = [] };
        await reopened.RefreshAsync(default);
        Check(reopened.Snapshot?.Identity == "different" && reopened.Snapshot.Quotas.Count == 0, "Old identity quotas leaked into new account.");
        var json = File.ReadAllText(Path.Combine(paths.AppHome, "cache", "provider-antigravity.json"));
        Check(!json.Contains("access_token") && !json.Contains("Csrf"), "Provider cache contains authentication material.");
        reopened.SetEnabled(false); Check(new CurrentProviderStore(paths, fake).Snapshot is null, "Removed provider resurrected cached account.");
    });
    private static byte[] ProtoVar(ulong n)
    { var b = new List<byte>(); do { var part = (byte)(n & 127); n >>= 7; b.Add(n > 0 ? (byte)(part | 128) : part); } while (n > 0); return b.ToArray(); }
    private static byte[] ProtoNum(int field, ulong value) => [..ProtoVar((ulong)(field << 3)), ..ProtoVar(value)];
    private static byte[] ProtoBytes(int field, byte[] value) => [..ProtoVar((ulong)((field << 3) | 2)), ..ProtoVar((ulong)value.Length), ..value];
    private static byte[] AccountingBlob(ulong time, ulong input = 100, bool embedded = true)
    {
        byte[] usage = [..ProtoNum(1, 10), ..ProtoNum(2, input), ..ProtoNum(3, 20), ..ProtoNum(5, 30)];
        byte[] payload = [..ProtoBytes(19, System.Text.Encoding.UTF8.GetBytes("gemini-test")), ..ProtoBytes(4, usage)];
        if (embedded) payload = [..payload, ..ProtoBytes(9, ProtoBytes(4, ProtoNum(1, time)))];
        return ProtoBytes(1, payload);
    }
    private static Task AntigravityAccountingAsync()
    {
        var time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var blob = AccountingBlob(time);
        Check(AntigravityAccounting.Decode(blob, null) is { Total: 160, Model: "gemini-test" }, "Accounting total differs from source fields.");
        Check(AntigravityAccounting.Decode(AccountingBlob(time, embedded: false), ProtoBytes(1, ProtoNum(1, time)))?.Timestamp == (long)time, "Step timestamp fallback failed.");
        Check(AntigravityAccounting.Decode(AccountingBlob(time, embedded: false), null) is null, "Undated generation assigned a date.");
        Check(AntigravityAccounting.Decode(AccountingBlob(time, ulong.MaxValue), null) is null, "Token overflow accepted.");
        for (var i = 0; i < blob.Length; i++) AntigravityAccounting.Decode(blob[..i], null);
        Check(AntigravityAccounting.Decode([10, 255, 255, 255], null) is null, "Malformed protobuf accepted.");
        return Task.CompletedTask;
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open(byte[] path, out IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr p);
    private static void WriteTestDb(string path, string sql)
    {
        Check(Path.GetFullPath(path).StartsWith(Path.Combine(Path.GetTempPath(), "CodexManager.Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Test DB outside temporary store.");
        Check(sqlite3_open(System.Text.Encoding.UTF8.GetBytes(path + '\0'), out var db) == 0, "Cannot create fixture DB.");
        try { var code = sqlite3_exec(db, System.Text.Encoding.UTF8.GetBytes(sql + '\0'), IntPtr.Zero, IntPtr.Zero, out var err); if (err != IntPtr.Zero) sqlite3_free(err); Check(code == 0, "Fixture SQL failed."); }
        finally { sqlite3_close(db); }
    }
    private static Task AntigravitySqliteHistoryAsync() => InTemporaryStoreAsync(async paths =>
    {
        var home = Path.Combine(paths.AppHome, "gemini"); var dir = Path.Combine(home, "antigravity", "conversations"); Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "synthetic.db"); var cache = Path.Combine(paths.AppHome, "cache", "agy.json");
        var time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var hex = Convert.ToHexString(AccountingBlob(time));
        WriteTestDb(db, $"CREATE TABLE gen_metadata(idx INTEGER PRIMARY KEY, data BLOB); CREATE TABLE steps(idx INTEGER PRIMARY KEY, metadata BLOB); INSERT INTO gen_metadata VALUES(0,X'{hex}');");
        var before = AuthHash.Bytes(File.ReadAllBytes(db)); var scanner = new AntigravityTokenHistory(home, cache);
        var first = await scanner.RefreshAsync(default);
        Check(first.Days.Single().Total == 160 && !first.Partial, "SQLite source did not produce daily count.");
        Check(AuthHash.Bytes(File.ReadAllBytes(db)) == before, "History reader changed source database.");
        var second = await scanner.RefreshAsync(default); Check(scanner.LastRowsRead == 0 && second.Days.Single().Total == 160, "Unchanged database rescanned or double-counted.");
        WriteTestDb(db, $"UPDATE gen_metadata SET data=X'{Convert.ToHexString(AccountingBlob(time, 200))}' WHERE idx=0; INSERT INTO gen_metadata VALUES(1,X'{Convert.ToHexString(AccountingBlob(time, embedded:false))}'); INSERT INTO steps VALUES(1,X'{Convert.ToHexString(ProtoBytes(1, ProtoNum(1,time)))}');");
        var third = await scanner.RefreshAsync(default); Check(third.Days.Single().Total == 420, "Updated/new row counted incorrectly.");
        var restarted = new AntigravityTokenHistory(home, cache); var restored = await restarted.RefreshAsync(default);
        Check(restored.Days.Single().Total == 420 && restarted.LastRowsRead == 0, "Persistent incremental cache lost or duplicated totals.");
        var merged = ProviderHistory.Merge(new([new(DateTime.Now.ToString("yyyy-MM-dd"), new Dictionary<string,long>{["gpt-test"]=500})],false,DateTimeOffset.UtcNow.ToString("O"),null), restored);
        Check(merged.Days.Single().Total == 920 && merged.Sources?.Count == 2 && merged.Days.Single().Models.Count == 2, "Provider totals/model separation incorrect.");
        var empty = await new AntigravityTokenHistory(Path.Combine(home,"missing"), Path.Combine(paths.AppHome,"none.json")).RefreshAsync(default);
        Check(empty.UpdatedAt is null && empty.Days.Count == 0, "Missing Antigravity history reported a measured zero.");
    });
    private static Task ProviderUiAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        using var service = CreateService(paths); using var form = new WidgetForm(service, new UiTheme(true, false, false), startClock:false);
        var state = SampleState() with { OtherAccounts = [SampleAntigravity()] }; form.ApplyState(state);
        var overview = UiField<ProviderOverview>(form,"_overview");
        var sampleHistory = ProviderHistory.Merge(SampleHistory(), SampleAntigravityHistory());
        form.SetHistoryPreview(sampleHistory);
        Check(overview.Series.Count == 2 && overview.Series.Count(s => s.Provider == ProviderIds.Antigravity) == 1, "Summary did not group Antigravity into one segment.");
        var monday = HistoryPanel.Monday(DateOnly.FromDateTime(DateTime.Today));
        var expected = SampleHistory().Days.Where(d => DateOnly.Parse(d.Date) >= monday).Sum(d => (decimal)d.Total);
        Check(overview.Series.Single(s => s.Provider == ProviderIds.Codex).Tokens == expected && overview.Series.Single(s => s.Provider == ProviderIds.Antigravity).Tokens == 300, "Summary did not use weekly tokens isolated by provider.");
        var card = UiField<Dictionary<string,CurrentProviderCard>>(form,"_externalRows")[ProviderIds.Antigravity];
        static bool HasButton(W.DependencyObject x) => x is C.Button || Enumerable.Range(0,M.VisualTreeHelper.GetChildrenCount(x)).Any(i => HasButton(M.VisualTreeHelper.GetChild(x,i)));
        Check(!HasButton(card), "Antigravity account exposes actions.");
        var historyPanel = UiField<HistoryPanel>(form, "_historyPanel");
        Check(overview.SelectedView == UsageView.Today && historyPanel.Visibility == W.Visibility.Visible, "Today is not the default view.");
        form.SetHistoryPreview(SampleHistory());
        var todayPeriod = UiField<C.TextBlock>(historyPanel, "_period").Text;
        UiClick(UiField<C.Button>(overview, "_week"));
        Check(overview.SelectedView == UsageView.Week && UiField<C.TextBlock>(historyPanel, "_period").Text != todayPeriod, "Week tab did not switch history period.");
        UiClick(UiField<C.Button>(historyPanel, "_previous"));
        var previousPeriod = UiField<C.TextBlock>(historyPanel, "_period").Text;
        form.ApplyState(state);
        Check(overview.SelectedView == UsageView.Week && UiField<C.TextBlock>(historyPanel, "_period").Text == previousPeriod, "Refresh reset the selected history period.");
        UiClick(UiField<C.Button>(overview,"_today")); Check(UiField<HistoryPanel>(form,"_historyPanel").Visibility == W.Visibility.Visible, "Tokens tab inaccessible.");
        UiClick(UiField<C.Button>(overview,"_quotas")); Check(UiField<HistoryPanel>(form,"_historyPanel").Visibility == W.Visibility.Collapsed, "Quota tab did not restore compact layout.");
        Check(UiField<C.Grid>(overview, "_body").Visibility == W.Visibility.Visible, "Quota overview is hidden.");
        UiClick(UiField<C.Button>(overview, "_today"));
        Check(UiField<C.TextBlock>(historyPanel, "_period").Text == todayPeriod, "Today tab did not restore the current day.");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var boundaryHistory = new TokenHistorySnapshot([
            new(monday.AddDays(-1).ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["outside-week"] = 900 }),
            new(today.ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["used-model"] = 100, ["unused-model"] = 0 }),
            new(today.AddDays(1).ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["future"] = 800 })
        ], false, DateTimeOffset.UtcNow.ToString("O"), null);
        form.SetHistoryPreview(ProviderHistory.Merge(boundaryHistory, TokenHistorySnapshot.Empty));
        Check(overview.Series.Count == 1 && overview.Series[0].Tokens == 100, "Donut includes other weeks, future dates or missing-provider usage.");
        UiClick(UiField<C.Button>(historyPanel, "_models"));
        var modelLines = UiField<C.StackPanel>(historyPanel, "_details").Children.Cast<C.DockPanel>().ToArray();
        Check(modelLines.Length == 2 && modelLines.SelectMany(p => p.Children.OfType<C.TextBlock>()).All(t => !t.Text.Contains("sin datos") && !t.Text.Contains("unused-model") && !t.Text.Contains("Antigravity")), "Model details show missing or zero-use rows.");
        form.SetHistoryPreview(boundaryHistory with { Days = [] });
        Check(overview.Series.Count == 0 && UiField<C.StackPanel>(historyPanel, "_details").Children.Count == 0, "Known zero use invents segments or model rows.");
        var center = UiField<C.Canvas>(overview, "_rings").Children.OfType<C.StackPanel>().Single();
        Check(((C.TextBlock)center.Children[0]).Text == "0", "Measured zero is not shown as zero.");
        form.SetHistoryPreview(TokenHistorySnapshot.Empty);
        center = UiField<C.Canvas>(overview, "_rings").Children.OfType<C.StackPanel>().Single();
        Check(overview.Series.Count == 0 && ((C.TextBlock)center.Children[0]).Text == "—", "Unavailable history is shown as measured zero.");
        form.ApplyState(state with { OtherAccounts = [] }); Check(UiField<Dictionary<string,CurrentProviderCard>>(form,"_externalRows").Count == 0 && form.Rows.Count == state.Profiles.Count, "Provider removal damaged Codex cards.");
        return Task.CompletedTask;
    }));
    private static Task RenderProviderUiAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        Directory.CreateDirectory(output); using var service = new CodexManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, new FakeQuotaProvider());
        foreach (var dark in new[]{true,false})
        {
            using var form = new WidgetForm(service,new UiTheme(dark,false,false),startClock:false);
            form.Visual.Topmost=false; form.Visual.Left=-20000; form.Visual.Top=-20000; form.Visual.Height=710;
            var state = SampleState() with { Profiles = SampleState().Profiles.Take(1).ToArray(), OtherAccounts = [SampleAntigravity()] };
            state = state with { AccountOrder = [AccountOrdering.Current(ProviderIds.Antigravity), AccountOrdering.Codex(state.Profiles[0].Name)] };
            form.ApplyState(state); ((C.TextBlock)form.Visual.FindName("TitleLabel")).Text = "Vista previa · datos ficticios"; form.Visual.Show(); form.Visual.UpdateLayout();
            var overview = UiField<ProviderOverview>(form, "_overview");
            var previewAntigravity = new TokenHistorySnapshot([new(DateTime.Today.ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["gemini-test"] = 350000, ["claude-test"] = 250000 })], false, DateTimeOffset.UtcNow.ToString("O"), null);
            var history = ProviderHistory.Merge(SampleHistory(), previewAntigravity);
            form.SetHistoryPreview(history);
            UiClick(UiField<C.Button>(overview, "_quotas")); form.Visual.UpdateLayout();
            Save(form.Visual,Path.Combine(output,dark?"cuotas-oscuro.png":"cuotas-claro.png"));
            form.SetHistoryPreview(history); UiClick(UiField<C.Button>(UiField<ProviderOverview>(form,"_overview"),"_today"));
            UiClick(UiField<C.Button>(UiField<HistoryPanel>(form,"_historyPanel"),"_models")); form.Visual.UpdateLayout();
            Save(form.Visual,Path.Combine(output,dark?"tokens-oscuro.png":"tokens-claro.png"));
            UiClick(UiField<C.Button>(overview, "_week")); form.Visual.UpdateLayout();
            Save(form.Visual,Path.Combine(output,dark?"semana-oscuro.png":"semana-claro.png"));
            form.ShowSettings(true); form.Visual.UpdateLayout(); Save(form.Visual,Path.Combine(output,dark?"ajustes-oscuro.png":"ajustes-claro.png"));
        }
        return Task.CompletedTask;
    }));
}

