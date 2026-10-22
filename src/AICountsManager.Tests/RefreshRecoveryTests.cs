using AICountsManager;
using C = System.Windows.Controls;
using System.Reflection;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static Task RefreshStorageFailureAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var snapshot = Snapshot("u", "a", "w", "g", 1700000000);
        SeedProfiles(paths, snapshot, ("Account", snapshot));
        var client = new PausedUsageClient();
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), client, false);
        using var form = new WidgetForm(service, new UiTheme(true, false, false), startClock: false);
        form.ApplyState(service.BuildDashboardState());
        var refresh = (C.Button)form.Visual.FindName("RefreshButton");
        var refreshMethod = typeof(WidgetForm).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)refreshMethod.Invoke(form, null)!;
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Only temporary test data: emulate the profile directory becoming unreadable
        // after the HTTP request starts, before the final dashboard reconstruction.
        var saved = Path.GetFullPath(paths.AccountsDir + "-saved");
        Check(saved.StartsWith(Path.GetFullPath(paths.AppHome) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe test path.");
        Directory.Move(paths.AccountsDir, saved);
        File.WriteAllText(paths.AccountsDir, "temporary unavailable directory");
        try
        {
            client.Release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(refresh.IsEnabled && form.Rows.Count == 1, "Failure left refresh disabled or removed cached rows.");
            Check(((C.TextBlock)form.Visual.FindName("FooterLabel")).Text.Contains("No se pudo"), "Read error is invisible.");
        }
        finally
        {
            File.Delete(paths.AccountsDir);
            Directory.Move(saved, paths.AccountsDir);
        }
        typeof(AICountsManagerService).GetField("_nextBatchAllowed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, DateTimeOffset.MinValue);
        await ((Task)refreshMethod.Invoke(form, null)!).WaitAsync(TimeSpan.FromSeconds(5));
        Check(refresh.IsEnabled && ((C.TextBlock)form.Visual.FindName("FooterLabel")).Text.Contains("1/1"), "Refresh did not recover.");
    }));

    private static Task TrayPostedFailureAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var snapshot = Snapshot("u", "a", "w", "g", 1700000000);
        SeedProfiles(paths, snapshot, ("Account", snapshot));
        using var service = CreateService(paths);
        using var tray = new TrayApplicationContext(service);
        var control = UiField<Control>(tray, "_dispatcher");
        var method = typeof(TrayApplicationContext).GetMethod("OnServiceStateChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var saved = Path.GetFullPath(paths.AccountsDir + "-saved");
        Check(saved.StartsWith(Path.GetFullPath(paths.AppHome) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Unsafe test path.");
        try
        {
            await Task.Delay(100);
            Directory.Move(paths.AccountsDir, saved); File.WriteAllText(paths.AccountsDir, "unavailable");
            await Task.Run(() => method.Invoke(tray, [service, EventArgs.Empty]));
            var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            control.BeginInvoke(() => posted.SetResult());
            await posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(File.ReadAllText(service.Diagnostics.FilePath).Contains("tray.apply-state"), "Posted failure did not leave diagnostic trace.");
            Check(!control.IsDisposed && !tray.WidgetCreated, "Posted failure closed the tray or created the flyout.");
            File.Delete(paths.AccountsDir); Directory.Move(saved, paths.AccountsDir);
            tray.ToggleWidget();
            Check(tray.WidgetVisible, "Tray could not reopen after a failed notification.");
        }
        finally
        {
            if (Directory.Exists(saved)) { File.Delete(paths.AccountsDir); Directory.Move(saved, paths.AccountsDir); }
            tray.ExitThread();
        }
    }));

    private static Task RefreshDisposalAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var snapshot = Snapshot("u", "a", "w", "g", 1700000000);
        SeedProfiles(paths, snapshot, ("Account", snapshot));
        var client = new PausedUsageClient();
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), client, false);
        using var form = new WidgetForm(service, new UiTheme(true, false, false), startClock: false);
        form.ApplyState(service.BuildDashboardState());
        var task = (Task)typeof(WidgetForm).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null)!;
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        form.Dispose(); service.Dispose();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!File.Exists(paths.UsageCachePath), "Cancelled refresh wrote quota data after disposal.");
    }));

    private static Task BoundedDiagnosticsAsync() => InTemporaryStoreAsync(paths =>
    {
        var diagnostics = new AppDiagnostics(paths.LogsDir);
        Exception error;
        try { throw new IOException("secret-access-token synthetic account@example.com", new InvalidOperationException("secret-refresh-token")); }
        catch (Exception ex) { error = ex; }
        for (var i = 0; i < 500; i++) diagnostics.Write("test.failure", error);
        Check(File.Exists(diagnostics.FilePath + ".previous"), "Diagnostics never rotated.");
        foreach (var path in new[] { diagnostics.FilePath, diagnostics.FilePath + ".previous" })
        {
            var text = File.ReadAllText(path);
            Check(new FileInfo(path).Length <= 128 * 1024, "Diagnostics exceeded its bound.");
            Check(text.Contains("System.IO.IOException") && text.Contains(nameof(BoundedDiagnosticsAsync)), "Missing exception type/stack.");
            Check(!text.Contains("secret-") && !text.Contains("account@example.com"), "Diagnostics exposed exception messages.");
        }
        var blocked = Path.Combine(paths.AppHome, "blocked-log-dir"); File.WriteAllText(blocked, "file");
        new AppDiagnostics(blocked).Write("test.failure", error);
        return Task.CompletedTask;
    });

    private sealed class PausedUsageClient : IUsageClient
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string path, CancellationToken token)
        {
            Started.TrySetResult(); await Release.Task.WaitAsync(token);
            return FakeUsageClient.Result();
        }
    }
}
