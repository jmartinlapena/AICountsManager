using AICountsManager;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static T UiField<T>(object owner, string name) => (T)owner.GetType().GetField(name,
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;
    private static void UiClick(C.Button button) => button.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
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
