using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AICountsManager;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static Task AccountOrderPersistsAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("u-a", "a", "w", "g", 1700000000); var b = Snapshot("u-b", "b", "w", "g", 1700000000); var c = Snapshot("u-c", "c", "w", "g", 1700000000);
        SeedProfiles(paths, a, ("A", a), ("B", b), ("C", c));
        using var service = CreateService(paths); var repo = Repository(paths);
        var fingerprint = AuthHash.Bytes(File.ReadAllBytes(paths.ActiveAuthPath));
        await service.SetProfileOrderAsync(["C", "A", "B"]);
        Check(Repository(paths).ListProfiles().Select(p => p.Name).SequenceEqual(new[] { "C", "A", "B" }), "Saved order did not survive a fresh repository instance.");
        Check(AuthHash.Bytes(File.ReadAllBytes(paths.ActiveAuthPath)) == fingerprint, "Reordering changed active credentials.");
        var invalid = false; try { await service.SetProfileOrderAsync(["A", "A", "B"]); } catch (InvalidOperationException) { invalid = true; }
        Check(invalid && repo.LoadAppConfig().ProfileOrder!.First() == "C", "Duplicate/missing account order was accepted.");
        await service.RenameProfileAsync("C", "Renamed");
        Check(repo.ListProfiles().First().Name == "Renamed", "Renaming lost the custom position.");
        await service.DeleteProfileAsync("B");
        Check(repo.LoadAppConfig().ProfileOrder!.SequenceEqual(new[] { "Renamed", "A" }), "Deletion left a stale order entry.");
        AtomicFile.WriteBytes(paths.ActiveAuthPath, b.Bytes); await service.AddCurrentProfileAsync("New");
        Check(service.BuildDashboardState().Profiles.Select(p => p.Name).SequenceEqual(new[] { "Renamed", "A", "New" }), "Adding/activating an account overrode the custom order.");
        await service.SwitchToProfileAsync("A");
        Check(service.BuildDashboardState().Profiles.Select(p => p.Name).SequenceEqual(new[] { "Renamed", "A", "New" }), "Switching an account changed the custom order.");
    });
    private static Task UiAccountOrderAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var a = Snapshot("u-a", "a", "w", "g", 1700000000); var b = Snapshot("u-b", "b", "w", "g", 1700000000);
        SeedProfiles(paths, a, ("A", a), ("B", b)); using var service = CreateService(paths);
        using var form = new WidgetForm(service, new UiTheme(true, false, false), startClock: false);
        form.ApplyState(service.BuildDashboardState()); form.ShowSettings(true);
        var order = (System.Windows.Controls.StackPanel)form.Visual.FindName("OrderList");
        var row = (System.Windows.Controls.Grid)order.Children[0];
        var down = (System.Windows.Controls.Button)row.Children[2];
        down.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await EventuallyAsync(() => Task.FromResult(order.IsEnabled));
        Check(((System.Windows.Controls.TextBlock)((System.Windows.Controls.Grid)order.Children[0]).Children[0]).Text == "B", "Settings arrow did not reorder the list.");
        form.ShowSettings(false);
        var cards = (System.Windows.Controls.StackPanel)form.Visual.FindName("AccountList");
        Check(System.Windows.Automation.AutomationProperties.GetName(cards.Children[0]) == "Cuenta B", "Dashboard ignored settings order.");
        Check(((System.Windows.Controls.TextBlock)form.Visual.FindName("TitleLabel")).Text == "AICountsManager", "Wrong application title.");
        Check(form.Visual.FindName("RuntimeLabel") is null, "Removed Codex process status is still shown.");
    }));
    private static Task TrayLazyFlyoutAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var account = Snapshot("u", "a", "w", "g", 1700000000); SeedProfiles(paths, account, ("Prueba", account));
        using var service = CreateService(paths);
        using var tray = new TrayApplicationContext(service);
        try
        {
            Check(!tray.WidgetCreated, "Startup created the WPF window before it was requested.");
            await EventuallyAsync(() => Task.FromResult(Repository(paths).LoadUsageCache().Accounts.Count == 1));
            Check(!tray.WidgetCreated, "A background state update created the hidden flyout.");
            tray.ToggleWidget(); Check(tray.WidgetCreated && tray.WidgetVisible, "The first tray click could not open the flyout.");
            tray.ToggleWidget(); Check(!tray.WidgetVisible, "The second tray click could not hide the flyout.");
            tray.ToggleWidget(); Check(tray.WidgetVisible, "The flyout could not reopen."); tray.ToggleWidget();
        }
        finally { tray.ExitThread(); }
    }));
    private static Task StrictPollingStaysLightAsync() => InTemporaryStoreAsync(async paths =>
    {
        var account = Snapshot("u", "a", "w", "g", 1700000000); SeedProfiles(paths, account, ("A", account));
        var repo = Repository(paths); repo.SaveAppConfig(repo.LoadAppConfig() with { StrictVerification = true });
        var client = new FakeUsageClient(); var server = new NoPollingAppServer();
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), server, client, false);
        var result = await service.RefreshActiveProfileAsync();
        Check(result?.Status == "ok" && client.Calls == 1 && server.Calls == 0, "Strict mode spawned an app-server for background quota polling.");
    });
    private sealed class NoPollingAppServer : ICodexAppServerClient
    {
        public int Calls;
        public Task<AuthVerificationResult> VerifyAsync(bool forceRefresh, CancellationToken token) { Calls++; throw new InvalidOperationException("Unexpected verification"); }
        public Task<UsageRecord> ReadRateLimitsAsync(CancellationToken token) { Calls++; throw new InvalidOperationException("Unexpected quota process"); }
    }
    private static Task UiRefreshAndHistoryAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var accounts = Enumerable.Range(0, 3).Select(i => (Name: "Cuenta " + i, Snapshot: Snapshot("u" + i, "a" + i, "w" + i, "g", 1700000000))).ToArray();
        SeedProfiles(paths, accounts[0].Snapshot, accounts);
        var client = new FakeUsageClient(); using var service = CreateService(paths, usage: client);
        using var form = new WidgetForm(service, new UiTheme(true, false, false), startClock: false);
        form.ApplyState(service.BuildDashboardState()); form.SetHistoryPreview(SampleHistory());
        form.Visual.Left = -20000; form.Visual.Top = -20000; form.Visual.Show(); form.Visual.UpdateLayout();
        var stack = (System.Windows.Controls.StackPanel)form.Visual.FindName("DashboardStack");
        var history = UiField<HistoryPanel>(form, "_historyPanel");
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;
        static void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Click(UiField<System.Windows.Controls.Button>(UiField<ProviderOverview>(form, "_overview"), "_week"));
        var label = Field<System.Windows.Controls.TextBlock>(history, "_period"); var current = label.Text;
        Click(Field<System.Windows.Controls.Button>(history, "_previous")); Check(label.Text != current, "History navigation did not move the calendar week.");
        Click(Field<System.Windows.Controls.Button>(history, "_models"));
        Check(Field<System.Windows.Controls.StackPanel>(history, "_details").Children.Count > 0, "Model details could not be opened.");
        var refresh = (System.Windows.Controls.Button)form.Visual.FindName("RefreshButton"); Click(refresh);
        Check(!refresh.IsEnabled, "Refresh action did not lock duplicate UI clicks.");
        await EventuallyAsync(() => Task.FromResult(refresh.IsEnabled));
        Check(client.Calls == 3 && Repository(paths).LoadUsageCache().Accounts.Count == 3, "The visible refresh button did not update every account.");
        form.ShowSettings(true); form.ShowSettings(false); form.Hide(); form.Visual.Show();
        Check(form.Rows.Count == 3 && !form.IsDisposed, "Settings/hide/reopen lost accounts.");
    }));
    private static string Record(string type, object payload, DateTimeOffset? time = null, int? ordinal = null) =>
        JsonSerializer.Serialize(new { type, timestamp = (time ?? DateTimeOffset.UtcNow.AddMinutes(-10)).ToString("O"), ordinal, payload }) + "\n";
    private static string Tokens(long total, long last, DateTimeOffset? time = null, int? ordinal = null) =>
        Record("event_msg", new { type = "token_count", info = new { total_token_usage = new { total_tokens = total }, last_token_usage = new { total_tokens = last } } }, time, ordinal);
    private static string Model(string model) => Record("turn_context", new { model });
    private static string Meta() => Record("session_meta", new { id = "synthetic" });
    private static (string File, string Cache) HistoryPaths(AppPaths paths)
    {
        Directory.CreateDirectory(Path.Combine(paths.CodexHome, "sessions"));
        Directory.CreateDirectory(paths.AppHome);
        return (Path.Combine(paths.CodexHome, "sessions", "test.jsonl"), Path.Combine(paths.AppHome, "history.json"));
    }
    private static Task HistoryIncrementalAsync() => InTemporaryStoreAsync(async paths =>
    {
        var (file, cache) = HistoryPaths(paths);
        var earlier = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-10); var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        await File.WriteAllTextAsync(file, Meta() + Model("model-a") + Tokens(100, 100, earlier) + Tokens(100, 100, earlier) + Model("model-b") + Tokens(160, 60, now), new UTF8Encoding(false));
        var scanner = new TokenHistory(paths.CodexHome, cache, TimeZoneInfo.Utc);
        var first = await scanner.RefreshAsync();
        Check(first.Days.Sum(d => d.Total) == 160 && first.Days.Count == 2, "Daily cumulative deltas or duplicate handling failed.");
        Check(first.Days.Single(d => d.Date == now.ToString("yyyy-MM-dd")).Models["model-b"] == 60, "Model attribution failed.");
        await scanner.RefreshAsync(); Check(scanner.LastBytesRead == 0, "Unchanged logs were reread.");
        await File.AppendAllTextAsync(file, Tokens(200, 40, now));
        Check((await scanner.RefreshAsync()).Days.Sum(d => d.Total) == 200 && scanner.LastBytesRead < new FileInfo(file).Length, "Append was not incremental.");
        var restarted = new TokenHistory(paths.CodexHome, cache, TimeZoneInfo.Utc);
        Check((await restarted.RefreshAsync()).Days.Sum(d => d.Total) == 200 && restarted.LastBytesRead == 0, "Cached offsets did not survive restart.");
        Directory.CreateDirectory(Path.Combine(paths.CodexHome, "archived_sessions"));
        File.Move(file, Path.Combine(paths.CodexHome, "archived_sessions", Path.GetFileName(file)));
        Check((await restarted.RefreshAsync()).Days.Sum(d => d.Total) == 200 && restarted.LastBytesRead == 0, "Archiving double-counted history.");
        Check(!File.ReadAllText(cache).Contains("event_msg"), "Raw records leaked into the numeric cache.");
    });
    private static Task HistoryPartialLineAsync() => InTemporaryStoreAsync(async paths =>
    {
        var (file, cache) = HistoryPaths(paths); var tail = Tokens(200, 100);
        await File.WriteAllTextAsync(file, Meta() + Tokens(100, 100) + tail[..40]);
        var scanner = new TokenHistory(paths.CodexHome, cache);
        Check((await scanner.RefreshAsync()).Days.Sum(d => d.Total) == 100, "Incomplete line was counted.");
        await File.AppendAllTextAsync(file, tail[40..]);
        Check((await scanner.RefreshAsync()).Days.Sum(d => d.Total) == 200, "Completed append was not recovered.");
        await File.WriteAllTextAsync(file, Meta() + Tokens(50, 50));
        Check((await scanner.RefreshAsync()).Days.Sum(d => d.Total) == 50, "Truncation was not reindexed.");
    });
    private static Task HistoryInheritedAsync() => InTemporaryStoreAsync(async paths =>
    {
        var (file, cache) = HistoryPaths(paths);
        await File.WriteAllTextAsync(file, Record("session_meta", new { subagent_history_start_ordinal = 10 }) + Model("child") + Tokens(1000, 100, ordinal: 4) + Tokens(1200, 200, ordinal: 10) + Tokens(1200, 200, ordinal: 11));
        var scanner = new TokenHistory(paths.CodexHome, cache);
        Check((await scanner.RefreshAsync()).Days.Sum(d => d.Total) == 200, "Inherited child usage was counted twice.");
        await File.WriteAllTextAsync(file, Record("session_meta", new { history_base = new { thread_id = "parent", ordinal = 15 } }) + Model("fork") + Tokens(1300, 300));
        Check((await scanner.RefreshAsync()).Days.Sum(d => d.Total) == 300, "A fork's inherited cumulative base was counted.");
        await File.AppendAllTextAsync(file, Tokens(30, 30) + Tokens(50, 20));
        Check((await scanner.RefreshAsync()).Days.Sum(d => d.Total) == 350, "A reset cumulative counter lost usage.");
    });
    private static Task HistoryBudgetAsync() => InTemporaryStoreAsync(async paths =>
    {
        var (file, cache) = HistoryPaths(paths);
        await File.WriteAllTextAsync(file, Meta() + Record("response_item", new { text = new string('x', 350000) }) + Tokens(123, 123));
        var scanner = new TokenHistory(paths.CodexHome, cache); TokenHistorySnapshot result; int loops = 0;
        do { result = await scanner.RefreshAsync(byteBudget: 65536, milliseconds: 5000); Check(scanner.LastBytesRead <= 65536, "Byte budget exceeded."); } while (result.CatchingUp && ++loops < 15);
        Check(!result.CatchingUp && result.Days.Sum(d => d.Total) == 123, "Oversized transcript or budget continuation lost the token record.");
    });
    private static Task HistoryMalformedAsync() => InTemporaryStoreAsync(async paths =>
    {
        var (file, cache) = HistoryPaths(paths);
        await File.WriteAllTextAsync(cache, "{\"Version\":1,\"Files\":null}");
        await File.WriteAllTextAsync(file, Meta() + Model("a") + Tokens(10, 10) + Record("event_msg", new { type = "token_count", info = new { total_token_usage = new { total_tokens = "bad" } } }) + Tokens(20, 10));
        var scanner = new TokenHistory(paths.CodexHome, cache);
        var result = await scanner.RefreshAsync();
        Check(result.Partial && !result.CatchingUp && result.Days.Sum(d => d.Total) == 20, "Malformed data crashed the scanner or hid incomplete coverage.");
        Check((await new TokenHistory(paths.CodexHome, cache).RefreshAsync()).Partial, "Coverage warning did not survive restart.");
    });
    private static Task HistoryCalendarAsync() => InTemporaryStoreAsync(async paths =>
    {
        var (file, cache) = HistoryPaths(paths);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        // Fixed past timestamps straddle local midnight and the spring DST transition.
        var one = new DateTimeOffset(2026, 3, 28, 22, 59, 0, TimeSpan.Zero);
        var two = one.AddMinutes(2);
        await File.WriteAllTextAsync(file, Meta() + Tokens(100, 100, one) + Tokens(200, 100, two) + Tokens(300, 100, two.AddHours(4)));
        var result = await new TokenHistory(paths.CodexHome, cache, zone).RefreshAsync();
        Check(result.Days.Count == 2 && result.Days[0].Total == 100 && result.Days[1].Total == 200, "Local midnight/DST grouping failed.");
        Check(HistoryPanel.Monday(new DateOnly(2026, 3, 29)) == new DateOnly(2026, 3, 23), "Calendar week does not start on Monday.");
    });
    private static Task PaceAndCountdownAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddDays(3.5).ToUnixTimeSeconds();
        var pace = QuotaPace.Calculate(new UsageWindow(70, 10080, reset), now)!;
        Check(Math.Abs(pace.ExpectedRemaining - 50) < .01 && Math.Abs(pace.ProjectedUsed - 140) < .01, "Linear pace math failed.");
        Check(Math.Abs(pace.DailyBudget - 30 / 3.5) < .01 && pace.SecondsToEmpty < 3.5 * 86400, "Daily budget or exhaustion estimate failed.");
        Check(QuotaPace.Calculate(new UsageWindow(5, 10080, now.AddDays(7).ToUnixTimeSeconds()), now) is null, "Young window produced a misleading estimate.");
        Check(QuotaPace.Calculate(new UsageWindow(double.NaN, 300, reset), now) is null, "Non-finite usage produced a projection.");
        Check(UsagePresentation.Countdown(now.AddDays(2).AddHours(3).ToUnixTimeSeconds(), now).StartsWith("Resets in 2d"), "Day countdown failed.");
        Check(UsagePresentation.Countdown(now.AddMinutes(-1).ToUnixTimeSeconds(), now) == "Pendiente de actualizar", "Expired window did not request fresh data.");
        Check(UsagePresentation.Countdown(long.MaxValue, now) == "Sin fecha", "Invalid timestamp crashed countdown.");
        return Task.CompletedTask;
    }
    private sealed class BatchUsageClient(Func<AuthSnapshot, CancellationToken, Task<UsageRecord>> read) : IUsageClient
    {
        public int Calls, Running, Maximum;
        public async Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string path, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); var running = Interlocked.Increment(ref Running); Maximum = Math.Max(Maximum, running);
            try { return await read(snapshot, token); } finally { Interlocked.Decrement(ref Running); }
        }
    }
    private static Task BulkRefreshAsync() => InTemporaryStoreAsync(async paths =>
    {
        var accounts = Enumerable.Range(0, 5).Select(i => (Name: "Account" + i, Snapshot: Snapshot("u" + i, "a" + i, "w" + i, "g", 1700000000))).ToArray();
        SeedProfiles(paths, accounts[0].Snapshot, accounts);
        var client = new BatchUsageClient(async (snapshot, token) => { await Task.Delay(30, token); return snapshot.AccountId == "a2" ? UsageRecord.FromError("Synthetic rejected token") : FakeUsageClient.Result(); });
        var process = new FakeProcessController(true);
        using var service = new AICountsManagerService(paths, process, new FakeAppServerClient(), client, false);
        var repo = Repository(paths); repo.SaveUsage("Account2", FakeUsageClient.Result());
        var authPaths = accounts.Select(a => repo.GetProfileAuthPath(a.Name)).Append(paths.ActiveAuthPath).ToArray();
        var hashes = authPaths.Select(p => AuthHash.Bytes(File.ReadAllBytes(p))).ToArray();
        var result = await service.RefreshAllUsageAsync();
        Check(result.StartsWith("4/5") && client.Calls == 5 && client.Maximum == 2, "Bulk did not update all accounts with bounded concurrency.");
        Check(repo.LoadUsageCache().Accounts.Count == 5 && repo.LoadUsageCache().Accounts["Account2"].Session.UsedPercent == 10, "Cache results were lost on concurrent queries/failure.");
        Check(authPaths.Select(p => AuthHash.Bytes(File.ReadAllBytes(p))).SequenceEqual(hashes), "Bulk changed credentials.");
        Check(repo.LoadAppConfig().ActiveProfile == "Account0" && process.CloseCalls == 0 && process.RestartCalls == 0, "Bulk switched an account or touched Codex processes.");
        await service.RefreshAllUsageAsync(); Check(client.Calls == 5, "Repeated click bypassed throttle.");
    });
    private static Task BulkRateLimitAsync() => InTemporaryStoreAsync(async paths =>
    {
        var accounts = Enumerable.Range(0, 5).Select(i => (Name: "Account" + i, Snapshot: Snapshot("u" + i, "a" + i, "w" + i, "g", 1700000000))).ToArray();
        SeedProfiles(paths, accounts[0].Snapshot, accounts);
        var client = new BatchUsageClient((_, _) => Task.FromResult(UsageRecord.FromError("rate limit") with { HttpStatus = 429, RetryAfterSeconds = 120 }));
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), client, false);
        var result = await service.RefreshAllUsageAsync();
        Check(client.Calls == 2 && result.Contains("3 pendientes"), "429 did not stop queued accounts.");
        await service.RefreshAllUsageAsync(); Check(client.Calls == 2, "Retry-After was ignored.");
    });
    private static Task BulkCancellationAsync() => InTemporaryStoreAsync(async paths =>
    {
        var account = Snapshot("u", "a", "w", "g", 1700000000); SeedProfiles(paths, account, ("A", account));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BatchUsageClient(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return FakeUsageClient.Result(); });
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), client, false);
        using var cancellation = new CancellationTokenSource();
        var task = service.RefreshAllUsageAsync(cancellationToken: cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check((await service.RefreshAllUsageAsync()).Contains("en curso"), "Overlapping batch was not coalesced.");
        cancellation.Cancel(); bool canceled = false;
        try { await task; } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Cancellation was swallowed.");
        await service.RenameProfileAsync("A", "Renamed").WaitAsync(TimeSpan.FromSeconds(5));
        Check(Repository(paths).ListProfiles().Single().Name == "Renamed", "Cancellation leaked the operation gate.");
    });
    private static Task HistoryBenchmarkAsync() => InTemporaryStoreAsync(async paths =>
    {
        var (file, cache) = HistoryPaths(paths);
        using (var stream = new StreamWriter(file, false, new UTF8Encoding(false)))
        {
            await stream.WriteAsync(Meta() + Model("benchmark"));
            var text = Record("response_item", new { text = new string('x', 8000) });
            for (var i = 0; i < 1000; i++) { await stream.WriteAsync(text); await stream.WriteAsync(Tokens((i + 1) * 100, 100)); }
        }
        var scanner = new TokenHistory(paths.CodexHome, cache); var watch = Stopwatch.StartNew();
        var result = await scanner.RefreshAsync(milliseconds: 5000); var elapsed = watch.ElapsedMilliseconds;
        Check(!result.CatchingUp && result.Days.Sum(d => d.Total) == 100000, "Synthetic benchmark did not finish correctly.");
        watch.Restart(); await scanner.RefreshAsync();
        Check(scanner.LastBytesRead == 0, "Idle scan reread unchanged bytes.");
        Console.WriteLine($"BENCHMARK synthetic {new FileInfo(file).Length / 1048576d:0.0} MiB initial={elapsed}ms unchanged={watch.ElapsedMilliseconds}ms bytes={scanner.LastBytesRead}");
    });
}
