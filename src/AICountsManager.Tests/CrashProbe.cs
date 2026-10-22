using AICountsManager;
using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static Task ProbeTrayRefreshAsync(bool liveHistory = false, bool liveQuotas = false, bool antigravity = false) => InTemporaryStoreAsync(async paths =>
    {
        var accounts = Enumerable.Range(0, 3).Select(i => (Name: "Account" + i, Snapshot: Snapshot("u"+i,"a"+i,"w","g",1700000000))).ToArray();
        SeedProfiles(paths, accounts[0].Snapshot, accounts);
        if (liveHistory)
        {
            var livePaths = AppPaths.Build();
            paths = paths with { CodexHome = livePaths.CodexHome };
            var cache = Path.Combine(livePaths.AppHome, "cache", "token_history.json");
            if (File.Exists(cache)) File.Copy(cache, Path.Combine(paths.AppHome, "cache", "token_history.json"));
        }
        IUsageClient client = new DelayedProbeUsage();
        if (liveQuotas)
        {
            var realPaths = AppPaths.Build(); var realRepo = Repository(realPaths);
            var saved = new List<AuthSnapshot>();
            foreach (var profile in realRepo.ListProfiles()) saved.Add(await realRepo.LoadProfileSnapshotAsync(profile.Name));
            client = new RealProbeUsage(saved.ToArray(), realPaths.CodexConfigPath);
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            TrayApplicationContext? tray = null; var completed = false;
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using var service = new AICountsManagerService(paths, liveQuotas ? new CodexProcessController() : new FakeProcessController(false), new NoPollingAppServer(), client, false, antigravity ? AntigravityCloudProvider.Create() : null);
                tray = new TrayApplicationContext(service);
                var dispatcher = UiField<Control>(tray, "_dispatcher");
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await Task.Delay(300);
                        tray.ToggleWidget();
                        var form = UiField<WidgetForm>(tray, "_widget");
                        ((C.TextBlock)form.Visual.FindName("TitleLabel")).Text = "Prueba · datos ficticios";
                        var button = (C.Button)form.Visual.FindName("RefreshButton");
                        form.SetHistoryPreview(SampleHistory());
                        form.Visual.Dispatcher.UnhandledException += (_, e) => Console.Error.WriteLine("DISPATCHER: " + e.Exception);
                        for (var i = 0; i < (liveQuotas ? 2 : 40); i++)
                        {
                            if (liveQuotas) await Task.Delay(16000);
                            typeof(AICountsManagerService).GetField("_nextBatchAllowed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, DateTimeOffset.MinValue);
                            if (!form.Visible) tray.ToggleWidget();
                            form.Visual.Activate();
                            var historyPanel = UiField<HistoryPanel>(form, "_historyPanel");
                            if (i % 4 == 0) UiClick(UiField<C.Button>(historyPanel, "_models"));
                            if (i % 2 == 0) button.Focus();
                            else ((C.Button)UiField<C.Grid>(historyPanel, "_chart").Children[0]).Focus();
                            if (i % 2 == 0) UiClick(button);
                            else form.Visual.RaiseEvent(new I.KeyEventArgs(I.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(form.Visual)!, Environment.TickCount, I.Key.F5) { RoutedEvent = I.Keyboard.PreviewKeyDownEvent });
                            if (i % 3 == 0) { form.Hide(); await Task.Delay(10); tray.ToggleWidget(); }
                            await EventuallyAsync(() => Task.FromResult(button.IsEnabled));
                            Console.WriteLine($"REFRESH {i} visible={form.Visible} context={SynchronizationContext.Current?.GetType().Name}");
                        }
                        if (antigravity)
                        {
                            var account = service.BuildDashboardState().OtherAccounts!.Single();
                            Check(account.Fresh ? account.Quotas.Count >= 4 : account.Status == "error" && !string.IsNullOrWhiteSpace(account.Error), "Provider is neither ready nor reporting unavailability.");
                            Console.WriteLine("LIVE ANTIGRAVITY status=" + account.Status + " limits=" + account.Quotas.Count);
                        }
                        completed = true;
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { tray.ExitThread(); }
                });
                Application.Run(tray);
                if (completed) done.TrySetResult();
                else done.TrySetException(new Exception("Tray message loop exited before refresh probe completed."));
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { tray?.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(90));
    });
    private sealed class RealProbeUsage(AuthSnapshot[] saved, string config) : IUsageClient
    {
        private int _index;
        public async Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string path, CancellationToken token)
        {
            var result = await new UsageClient().ReadAsync(saved[(Interlocked.Increment(ref _index)-1) % saved.Length], config, token);
            Console.WriteLine($"REAL QUOTA status={result.Status} http={result.HttpStatus} plan={result.PlanType}");
            return result;
        }
    }
    private sealed class DelayedProbeUsage : IUsageClient
    {
        public async Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string path, CancellationToken token)
        { await Task.Delay(35, token); return UsageClient.NormalizeWhamUsage(PlanPayload("self_serve_business_prolite")); }
    }
}
