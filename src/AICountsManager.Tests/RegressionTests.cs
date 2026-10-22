using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AICountsManager;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static Task ProfileNamesAreSafeAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories();
        var repo = Repository(paths);
        foreach (var name in new[] { ".", "..", "CON", "con.txt", "NUL", "COM1", "LPT9.txt", "Cuenta.", "../x", "a/b", "a\\b", "C:foo" })
        {
            var rejected = false;
            try { repo.GetProfileAuthPath(name); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, $"Unsafe name accepted: {name}");
        }
        Check(repo.SanitizeProfileName("  José trabajo  ") == "José trabajo", "Valid Unicode name rejected.");
        return Task.CompletedTask;
    });

    private static Task MetadataNameIsNotAuthoritativeAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories();
        var repo = Repository(paths);
        repo.AddOrUpdateFromSnapshot("Safe", Snapshot("u", "a", "w", "g", 1700000000));
        AtomicFile.WriteText(Path.Combine(paths.AccountsDir, "Safe", "meta.json"), "{\"Name\":\"..\"}");
        Check(repo.ListProfiles().Single().Name == "Safe", "Metadata escaped the real profile directory.");
        return Task.CompletedTask;
    });

    private static Task MissingAuthRollbackAsync() => CheckMissingAuthRollbackAsync(false);
    private static Task MissingAuthExceptionRollbackAsync() => CheckMissingAuthRollbackAsync(true);
    private static Task CheckMissingAuthRollbackAsync(bool throws) => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories();
        var repo = Repository(paths);
        repo.AddOrUpdateFromSnapshot("Target", Snapshot("u", "a", "w", "g", 1700000000));
        using var service = CreateService(paths, appServer: new FakeAppServerClient(
            new AuthVerificationResult(AuthVerificationStatus.RefreshTokenInvalidated, null, "Synthetic rejection"),
            onVerify: throws ? () => throw new IOException("Synthetic verification failure") : null));
        await service.SetStrictVerificationAsync(true);
        var result = await service.SwitchToProfileAsync("Target");
        Check(result.Status == (throws ? SwitchProfileStatus.Failed : SwitchProfileStatus.RequiresReauthentication), "Wrong failure result.");
        Check(!File.Exists(paths.ActiveAuthPath), "Failed switch left incoming credentials installed.");
        Check(repo.LoadAppConfig().ActiveProfile is null, "Failed switch retained active profile.");
        Check(!File.Exists(paths.TransactionJournalPath), "Rollback did not clear its journal.");
    });

    private static Task UiProfileActionsAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        using var service = CreateService(paths);
        AtomicFile.WriteBytes(paths.ActiveAuthPath, Snapshot("u1", "a1", "w1", "g", 1700000000).Bytes);
        await service.AddCurrentProfileAsync("First");
        await service.RenameProfileAsync("First", "Renamed");
        await service.SetStrictVerificationAsync(true);
        AtomicFile.WriteBytes(paths.ActiveAuthPath, Snapshot("u2", "a2", "w2", "g", 1700000000).Bytes);
        await service.AddCurrentProfileAsync("Second");
        await service.DeleteProfileAsync("Renamed");
        Check(Repository(paths).ListProfiles().Single().Name == "Second", "UI operations failed.");
        var rejected = false;
        try { await service.DeleteProfileAsync("Second"); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Active profile was deleted.");
    }));

    private static Task ExpiredUsageIsUnknownAsync() => InTemporaryStoreAsync(paths =>
    {
        var snapshot = Snapshot("u", "a", "w", "g", 1700000000);
        SeedProfiles(paths, snapshot, ("A", snapshot));
        using var service = CreateService(paths);
        Repository(paths).SaveUsage("A", FakeUsageClient.Result() with
        {
            Session = new UsageWindow(87, 300, DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds()),
        });
        var p = service.BuildDashboardState().ActiveProfile;
        Check(p.SessionRemainingPercent is null && p.Status == "stale", "Expired quota incorrectly presented as fresh/full.");
        Check(p.WeeklyRemainingPercent == 80, "Valid weekly quota was lost.");
        return Task.CompletedTask;
    });

    private static Task UsageErrorsPreserveObservationAsync() => InTemporaryStoreAsync(async paths =>
    {
        var snapshot = Snapshot("u", "a", "w", "g", 1700000000);
        SeedProfiles(paths, snapshot, ("A", snapshot));
        var repo = Repository(paths);
        var before = FakeUsageClient.Result();
        repo.SaveUsage("A", before);
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), new FailingUsageClient(), false);
        var result = await service.RefreshActiveProfileAsync();
        var after = repo.LoadUsageCache().Accounts["A"];
        Check(result?.Status == "error", "Refresh hid its failure from the caller.");
        Check(after.Session == before.Session && after.CheckedAt == before.CheckedAt && after.Status == "error", "Last observation was lost or incorrectly marked fresh.");
    });

    private static Task FileModeIsRootSettingAsync() => InTemporaryStoreAsync(paths =>
    {
        using var service = CreateService(paths);
        AtomicFile.WriteText(paths.CodexConfigPath, "model = \"test\"\n[projects.example]\ntrust_level = \"trusted\"\n");
        service.EnsureFileModeConfig();
        var text = File.ReadAllText(paths.CodexConfigPath);
        Check(text.IndexOf("cli_auth_credentials_store", StringComparison.Ordinal) < text.IndexOf("[projects", StringComparison.Ordinal), "Setting was inserted inside a table.");
        service.EnsureFileModeConfig();
        Check(text == File.ReadAllText(paths.CodexConfigPath), "File-mode update was not idempotent.");
        return Task.CompletedTask;
    });

    private static DashboardState SampleState() => new(
        SampleProfile("Personal", true),
        [SampleProfile("Personal", true) with { PlanType = "prolite", SessionLimitNotApplicable = true, SessionWindow = new(null, null, null), SessionUsedPercent = null, SessionRemainingPercent = null },
         SampleProfile("Trabajo", false) with { PlanType = "plus" },
         SampleProfile("Empresa", false) with { PlanType = "self_serve_business_prolite", SessionLimitNotApplicable = true, SessionWindow = new(null, null, null), SessionUsedPercent = null, SessionRemainingPercent = null }],
        "Estado de ejemplo", "", "", false, true, "Hace 2 min");
    private static ProfileState SampleProfile(string name, bool active) => new(name, active, 35, 65, "18:30", 18, 82, "26/10", "Hace 2 min", "ok", null, name.ToLowerInvariant() + "@example.test", new UsageWindow(35, 300, DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds()), new UsageWindow(18, 10080, DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds()));
    private static Task UiCardLifetimeAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        using var service = CreateService(paths);
        using var form = new WidgetForm(service, new UiTheme(false, false, false));
        var state = SampleState(); form.ApplyState(state);
        var original = form.Rows.First();
        for (var i = 0; i < 100; i++) form.ApplyState(state);
        Check(ReferenceEquals(original, form.Rows.First()) && !original.IsDisposed, "Refresh recreated a live row.");
        form.ApplyState(state with { Profiles = state.Profiles.Skip(1).ToArray() });
        Check(original.IsDisposed, "Removed row was not released.");
        form.Visual.Left = -20000; form.Visual.Top = -20000; form.Visual.Show();
        form.Visual.Close();
        Check(!form.Visible && !form.IsDisposed, "Closing the flyout must hide it without destroying it.");
        form.Visual.Show();
        Check(form.Visible, "The flyout could not reopen after closing.");
        return Task.CompletedTask;
    }));

    private static async Task RunOnUiThreadAsync(Func<Task> action, int timeoutSeconds = 30)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Size = new Size(1, 1) };
            host.Shown += async (_, _) =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
    }

    private static Task PreviewUiAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        using var service = CreateService(paths);
        using var form = new WidgetForm(service, startClock: false);
        form.Visual.Title = "AICountsManager · Vista previa";
        form.Visual.Topmost = false;
        form.Visual.ShowInTaskbar = true;
        form.Visual.Left = Math.Max(0, System.Windows.SystemParameters.WorkArea.Right - 480);
        form.Visual.Top = Math.Max(0, System.Windows.SystemParameters.WorkArea.Bottom - 580);
        form.Visual.Height = 710;
        form.ApplyState(SampleState()); form.SetHistoryPreview(SampleHistory()); form.Visual.Show(); form.Visual.Activate();
        Console.WriteLine("Preview only. Acrylic enabled: " + form.AcrylicEnabled);
        await Task.Delay(TimeSpan.FromSeconds(120));
    }, 150));

    private static TokenHistorySnapshot SampleHistory() => new(Enumerable.Range(0, 14).Select(i => new TokenDay(DateTime.Today.AddDays(i - 13).ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["gpt-6"] = 320000 + i * 13000, ["gpt-6-mini"] = 80000 + i * 17000 })).ToArray(), false, DateTimeOffset.UtcNow.ToString("O"), null);
    private static Task RenderUiAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        Directory.CreateDirectory(output);
        using var service = CreateService(paths);
        foreach (var dark in new[] { false, true })
        {
            using var form = new WidgetForm(service, new UiTheme(dark, false, false, dark ? System.Windows.Media.Color.FromRgb(34, 31, 29) : null, System.Windows.Media.Color.FromRgb(152, 105, 48)));
            form.Visual.Topmost = false; form.Visual.Left = -20000; form.Visual.Top = -20000;
            form.Visual.Height = 710; form.ApplyState(SampleState()); form.SetHistoryPreview(SampleHistory()); form.Visual.Show(); form.Visual.UpdateLayout();
            Save(form.Visual, Path.Combine(output, dark ? "interfaz-oscura.png" : "interfaz-clara.png"));
            var history = UiField<HistoryPanel>(form, "_historyPanel");
            UiClick(UiField<System.Windows.Controls.Button>(UiField<ProviderOverview>(form, "_overview"), "_week"));
            UiClick((System.Windows.Controls.Button)UiField<System.Windows.Controls.Grid>(history, "_chart").Children[0]);
            form.Visual.UpdateLayout();
            Save(form.Visual, Path.Combine(output, dark ? "dia-seleccionado-oscuro.png" : "dia-seleccionado-claro.png"));
            UiClick(UiField<System.Windows.Controls.Button>(UiField<ProviderOverview>(form, "_overview"), "_today"));
            var menu = (System.Windows.Controls.ContextMenu)typeof(AccountRow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form.Rows.First())!;
            menu.PlacementTarget = form.Rows.First(); menu.IsOpen = true;
            form.Visual.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ((System.Windows.Controls.MenuItem)menu.Items[0]).Focus();
            SaveElement(menu, Path.Combine(output, dark ? "menu-oscuro.png" : "menu-claro.png"));
            menu.IsOpen = false;
            form.ShowSettings(true); form.Visual.UpdateLayout();
            Save(form.Visual, Path.Combine(output, dark ? "ajustes-oscuros.png" : "ajustes-claros.png"));
        }
        using (var empty = new WidgetForm(service, new UiTheme(false, false, false)))
        {
            empty.Visual.Topmost = false; empty.Visual.Left = -20000; empty.Visual.Top = -20000;
            empty.ApplyState(SampleState() with { Profiles = Array.Empty<ProfileState>() }); empty.Visual.Show(); empty.Visual.UpdateLayout();
            Save(empty.Visual, Path.Combine(output, "sin-cuentas.png"));
        }
        Console.WriteLine("UI previews rendered with synthetic data: " + Path.GetFullPath(output));
        return Task.CompletedTask;
    }));
    private static void Save(System.Windows.Window window, string path) => SaveElement(window, path);
    private static void SaveElement(System.Windows.FrameworkElement window, string path)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); png.Save(output);
    }
    private sealed class FailingUsageClient : IUsageClient
    {
        public Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string path, CancellationToken cancellationToken) => throw new IOException("Synthetic offline error");
    }
}
