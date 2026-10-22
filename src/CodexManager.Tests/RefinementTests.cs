using CodexManager;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace CodexManager.Tests;

internal static partial class Program
{
    private static T UiField<T>(object owner, string name) => (T)owner.GetType().GetField(name,
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;
    private static void UiClick(C.Button button) => button.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
    private static Task HistoryDaySelectionAsync() => RunOnUiThreadAsync(() =>
    {
        var theme = new UiTheme(true, false, false);
        var panel = new HistoryPanel(theme);
        var today = DateOnly.FromDateTime(DateTime.Today); var monday = HistoryPanel.Monday(today);
        var data = new TokenHistorySnapshot(Enumerable.Range(0, 14).Select(i => new TokenDay(today.AddDays(i - 13).ToString("yyyy-MM-dd"),
            new Dictionary<string, long> { ["model-" + i] = 1_234_567 + i })).ToArray(), false, DateTimeOffset.UtcNow.ToString("O"), null);
        panel.Update(data); UiClick(UiField<C.Button>(panel, "_week"));
        var total = UiField<C.TextBlock>(panel, "_total"); var period = UiField<C.TextBlock>(panel, "_period");
        var chart = UiField<C.Grid>(panel, "_chart");
        var summary = total.Text; var summaryPeriod = period.Text;
        var names = chart.Children.Cast<C.Button>().Select(W.Automation.AutomationProperties.GetName).ToArray();
        UiClick((C.Button)chart.Children[0]);
        var expected = data.Days.Single(d => d.Date == monday.ToString("yyyy-MM-dd"));
        Check(total.Text == expected.Total.ToString("N0") && period.Text == monday.ToString("dddd, dd MMM yyyy"), "Day click did not show exact tokens/date.");
        Check(chart.Children.Cast<C.Button>().Select(W.Automation.AutomationProperties.GetName).SequenceEqual(names), "Selecting a day shifted the chart.");
        UiClick(UiField<C.Button>(panel, "_models"));
        var details = UiField<C.StackPanel>(panel, "_details");
        Check(details.Children.Count == 1 && ((C.TextBlock)((C.DockPanel)details.Children[0]).Children[1]).Text == expected.Models.Keys.Single(), "Model breakdown still includes the whole week.");
        panel.Update(data); panel.SetTheme(theme with { Dark = false });
        Check(total.Text == expected.Total.ToString("N0"), "Refresh or theme change cleared selected day.");
        UiClick((C.Button)chart.Children[0]);
        Check(total.Text == summary && period.Text == summaryPeriod, "Second click failed to restore weekly total.");
        foreach (var (button, i) in chart.Children.Cast<C.Button>().Select((b, i) => (b, i)))
            Check(button.IsEnabled == (monday.AddDays(i) <= today), "Future day is selectable.");
        UiClick((C.Button)chart.Children[0]); UiClick(UiField<C.Button>(panel, "_previous"));
        Check(!period.Text.Contains(monday.ToString("dddd, dd MMM yyyy")), "Week navigation retained unrelated daily selection.");
        UiClick(UiField<C.Button>(panel, "_today"));
        var yesterday = today.AddDays(-1); UiClick((C.Button)chart.Children[5]);
        Check(total.Text == data.Days.Single(d => d.Date == yesterday.ToString("yyyy-MM-dd")).Total.ToString("N0"), "Daily view click did not select yesterday.");
        panel.Update(data with { Days = Array.Empty<TokenDay>() });
        Check(total.Text == "0", "Day with no activity did not show zero.");
        panel.Update(TokenHistorySnapshot.Empty);
        Check(total.Text == "—", "Unknown history was represented as a measured zero.");
        return Task.CompletedTask;
    });

    private static Task BusinessPremiumAsync() => InTemporaryStoreAsync(paths =>
    {
        var usage = UsageClient.NormalizeWhamUsage(PlanPayload("self_serve_business_prolite"));
        Check(usage.SessionLimitNotApplicable && AccountPlan.Label(usage.PlanType) == "Business Premium", "Premium tier not distinguished.");
        var repo = Repository(paths);
        repo.SaveUsage("Business", usage with { SessionLimitNotApplicable = false });
        Check(repo.LoadUsageCache().Accounts["Business"].SessionLimitNotApplicable, "Existing 2.2.2 Business cache did not compact without a refresh.");
        repo.SaveUsage("Business", UsageRecord.FromError("offline"));
        Check(repo.LoadUsageCache().Accounts["Business"].SessionLimitNotApplicable, "Failed update lost known Premium capability.");
        var payload = PlanPayload("self_serve_business_prolite");
        payload["rate_limit"]!["secondary_window"] = System.Text.Json.Nodes.JsonNode.Parse("{\"used_percent\":12,\"limit_window_seconds\":18000,\"reset_at\":2000000000}");
        Check(!UsageClient.NormalizeWhamUsage(payload).SessionLimitNotApplicable, "Present Premium session limit was hidden.");
        foreach (var plan in new[] { "business", "team", "self_serve_business_usage_based" })
            Check(!UsageClient.NormalizeWhamUsage(PlanPayload(plan)).SessionLimitNotApplicable, "Other Business tier wrongly treated as Premium.");
        Check(!UsageClient.NormalizeWhamUsage(System.Text.Json.Nodes.JsonNode.Parse("{\"plan_type\":\"self_serve_business_prolite\"}")).SessionLimitNotApplicable,
            "Empty Premium response implied an unlimited session.");
        return Task.CompletedTask;
    });

    private static Task QuietRefreshUiAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var snapshot = Snapshot("u", "a", "w", "g", 1700000000); SeedProfiles(paths, snapshot, ("Account", snapshot));
        var usage = new FakeUsageClient(); using var service = CreateService(paths, usage: usage);
        using var form = new WidgetForm(service, new UiTheme(true, false, false), startClock: false);
        form.ApplyState(service.BuildDashboardState()); form.SetHistoryPreview(SampleHistory());
        form.Visual.Left = -20000; form.Visual.Top = -20000; form.Visual.Show(); form.Visual.UpdateLayout();
        Check(form.Visual.FindName("MessagePanel") is null, "Redundant status bar still exists.");
        var refresh = (C.Button)form.Visual.FindName("RefreshButton"); UiClick(refresh);
        await EventuallyAsync(() => Task.FromResult(refresh.IsEnabled)); var calls = usage.Calls;
        UiClick(refresh); await EventuallyAsync(() => Task.FromResult(refresh.IsEnabled));
        Check(usage.Calls == calls, "Removing cooldown message removed rate limiting.");
        Check(((C.TextBlock)form.Visual.FindName("FooterLabel")).Text.Contains("1/1"), "Footer lost updated account count.");
        static void CheckNoTips(W.DependencyObject element)
        {
            if (element is W.FrameworkElement f) Check(f.ToolTip is null, "Hover annotation remains: " + f.GetType().Name);
            for (var i = 0; i < M.VisualTreeHelper.GetChildrenCount(element); i++) CheckNoTips(M.VisualTreeHelper.GetChild(element, i));
        }
        CheckNoTips(form.Visual); form.ShowSettings(true); form.Visual.UpdateLayout(); CheckNoTips(form.Visual);
        // User-action failures remain visible even though routine status messages are gone.
        var shown = false;
        _ = form.Visual.Dispatcher.BeginInvoke(() =>
        {
            var dialog = form.Visual.OwnedWindows.Cast<W.Window>().Single(); shown = dialog.Title == "No se pudo completar";
            dialog.Close();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        typeof(WidgetForm).GetMethod("ShowError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, ["Synthetic failure"]);
        Check(shown, "Removing status bar also hid actionable failures.");
    }));
}
