using AICountsManager;
using C = System.Windows.Controls;
using W = System.Windows;
using M = System.Windows.Media;
namespace AICountsManager.Tests;
internal static partial class Program
{
    private static IEnumerable<W.FrameworkElement> QuotaElements(W.DependencyObject parent)
    {
        for (var i = 0; i < M.VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = M.VisualTreeHelper.GetChild(parent, i); if (child is W.FrameworkElement e) yield return e; foreach (var nested in QuotaElements(child)) yield return nested; }
    }
    private static Task ProviderDisplayParityAsync() => RunOnUiThreadAsync(() =>
    {
        var theme = new UiTheme(true, false, false); using var codex = new AccountRow(theme); var external = new CurrentProviderCard(theme);
        var panel = new C.StackPanel(); panel.Children.Add(codex); panel.Children.Add(external);
        var window = new W.Window { Width = 382, Height = 700, Left = -20000, Top = -20000, ShowInTaskbar = false, Content = panel };
        try
        {
            window.Show();
            foreach (var (used, minutes, hours, fresh) in new (double?, int, double, bool)[]
                { (.0066,10080,162,true), (35,10080,115,true), (70,10080,84,true), (0,10080,167,true), (100,10080,24,true), (75,300,2,true), (null,10080,72,true), (70,10080,-1,true), (70,10080,72,false) })
            {
                var quota = new ProviderQuota("test", "Test", "Cuota", used, minutes, DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds());
                var remaining = quota.CurrentUsed is double value ? 100 - value : (double?)null;
                var profile = SampleState().Profiles[0] with { Status = fresh ? "ok" : "stale", SessionLimitNotApplicable = false,
                    WeeklyWindow = minutes == 10080 ? quota.Window : null, SessionWindow = minutes == 300 ? quota.Window : null,
                    WeeklyRemainingPercent = minutes == 10080 ? remaining : null, SessionRemainingPercent = minutes == 300 ? remaining : null };
                codex.Update(profile, false);
                external.Update(SampleAntigravity() with { Status = profile.Status, Quotas = [quota] }); window.UpdateLayout();
                var index = minutes == 300 ? 0 : 1; var elements = QuotaElements(external).ToArray();
                var texts = elements.OfType<C.TextBlock>().Select(t => t.Text).ToArray();
                Check(texts.Contains(UiField<C.TextBlock[]>(codex,"_percent")[index].Text.Replace(" restante", "")), "Provider percentage differs from Codex.");
                Check(texts.Contains(UiField<C.TextBlock[]>(codex,"_reset")[index].Text), "Provider reset counter differs from Codex.");
                var forecast = elements.OfType<C.TextBlock>().SingleOrDefault(e => Equals(e.Tag,"weekly-forecast"))?.Text ?? "";
                Check(forecast == UiField<C.TextBlock[]>(codex,"_forecast")[index].Text, "Provider forecast differs from Codex.");
                var marker = elements.OfType<C.Border>().SingleOrDefault(e => Equals(e.Tag,"weekly-pace-marker"));
                var codexMarker = UiField<C.Border[]>(codex,"_markers")[index];
                Check((marker is not null) == (codexMarker.Visibility == W.Visibility.Visible), "Provider marker visibility differs from Codex.");
                if (marker is not null)
                {
                    var relativeExternal = marker.Margin.Left / (((C.Grid)marker.Parent).ActualWidth - 2);
                    var relativeCodex = codexMarker.Margin.Left / (UiField<C.Border[]>(codex,"_tracks")[index].ActualWidth - 2);
                    Check(Math.Abs(relativeExternal-relativeCodex)<.0001, "Provider marker position differs from Codex.");
                }
                Check(!elements.OfType<C.Button>().Any(), "External quota card gained account actions.");
            }
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
    private static Task CurrentProviderBoundaryAsync() => InTemporaryStoreAsync(async paths =>
    {
        var provider = new FakeQuotaProvider { Id = "future-client" };
        var rejected = false;
        try { await QuotaQuery.ReadAsync(provider, new CodexQuotaRequest(Snapshot("u","a","w","g",1700000000), paths.CodexConfigPath), default); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && provider.Calls == 0, "External adapter received a saved Codex credential request.");
        var usage = new FakeUsageClient(); rejected = false;
        try { await QuotaQuery.ReadAsync(new CodexQuotaProvider(usage),new CurrentQuotaRequest(),default); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && usage.Calls == 0, "Codex request contract was bypassed.");
        rejected = false;
        try { _ = new CurrentProviderRegistry(paths, [new FakeQuotaProvider { Id = "future-client", CanSwitchAccounts = true }]); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "External switching capability was accepted.");
    });
    private static Task RegisteredProvidersUiAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var future = new FakeQuotaProvider { Id = "future-client", Result = SampleAntigravity() with { Provider = "future-client" } };
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, new FakeQuotaProvider(), additionalProviders: [future]);
        await service.RefreshCurrentProvidersAsync();
        using var form = new WidgetForm(service, new UiTheme(true,false,false), false); form.ApplyState(service.BuildDashboardState()); form.ShowSettings(true);
        Check(form.Visual.Title == "AICountsManager" && ((C.TextBlock)form.Visual.FindName("TitleLabel")).Text == "AICountsManager", "The window or header has an outdated product name.");
        var toggles = UiField<Dictionary<string,C.CheckBox>>(form,"_providerToggles");
        Check(toggles.Count == 2 && toggles.ContainsKey("future-client"), "Registered adapter missing from generic settings.");
        Check(UiField<C.ContextMenu>(form,"_addMenu").Items.OfType<C.MenuItem>().Any(m => m.Header.ToString()!.Contains("future-client")), "Registered adapter missing from add menu.");
        Check(((C.StackPanel)form.Visual.FindName("OrderList")).Children.Count == 2, "Registered adapters missing from unified ordering.");
        toggles["future-client"].IsChecked = false; toggles["future-client"].RaiseEvent(new W.RoutedEventArgs(C.Primitives.ButtonBase.ClickEvent));
        await EventuallyAsync(() => Task.FromResult(toggles["future-client"].IsEnabled));
        Check(service.BuildDashboardState().OtherAccounts!.Single().Provider == ProviderIds.Antigravity, "Generic toggle disabled the wrong provider.");
        Check(new CurrentProviderRegistry(paths,[future]).Settings.Single().Enabled == false, "Generic provider selection did not persist.");
    }));
}
