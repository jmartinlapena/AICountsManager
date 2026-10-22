using System.Text.Json.Nodes;
using CodexManager;
using W = System.Windows;
using C = System.Windows.Controls;

namespace CodexManager.Tests;

internal static partial class Program
{
    private static JsonObject PlanPayload(string? plan = "pro") => new()
    {
        ["plan_type"] = plan,
        ["rate_limit"] = new JsonObject
        {
            ["allowed"] = true, ["limit_reached"] = false,
            ["primary_window"] = new JsonObject
            {
                ["used_percent"] = 28d, ["limit_window_seconds"] = 604800,
                ["reset_at"] = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds(),
            },
            ["secondary_window"] = null,
        },
    };

    private static Task PlanNormalizationAsync()
    {
        foreach (var (raw, label) in new[] { ("pro", "Pro 200"), ("prolite", "Pro 100"), ("promax", "Pro 500") })
        {
            var usage = UsageClient.NormalizeWhamUsage(PlanPayload(raw));
            Check(usage.PlanType == raw && AccountPlan.Label(raw) == label, "Incorrect plan label.");
            Check(usage.SessionLimitNotApplicable && usage.Session.UsedPercent is null && usage.Weekly.UsedPercent == 28, "Weekly-only Pro not recognized.");
            var swapped = PlanPayload(raw); var limits = swapped["rate_limit"]!.AsObject();
            limits["secondary_window"] = limits["primary_window"]!.DeepClone(); limits["primary_window"] = null;
            Check(UsageClient.NormalizeWhamUsage(swapped).SessionLimitNotApplicable, "Window position affected applicability.");
        }
        Check(AccountPlan.Label("plus") == "Plus" && AccountPlan.Label("new_plan") == "Otro plan"
            && AccountPlan.Label(null) is null, "Fallback label is misleading.");
        var invalidPlan = PlanPayload(); invalidPlan["plan_type"] = new JsonObject();
        Check(UsageClient.NormalizeWhamUsage(invalidPlan).PlanType is null, "Malformed plan broke valid quotas.");
        return Task.CompletedTask;
    }

    private static Task MissingSessionIsNotUnlimitedAsync()
    {
        foreach (var plan in new string?[] { null, "plus", "new_pro_plan", "business" })
            Check(!UsageClient.NormalizeWhamUsage(PlanPayload(plan)).SessionLimitNotApplicable, "Unknown/non-Pro plan was collapsed.");
        foreach (var json in new[] { "{}", "{\"plan_type\":\"pro\"}", "{\"plan_type\":\"pro\",\"rate_limit\":{}}" })
            Check(!UsageClient.NormalizeWhamUsage(JsonNode.Parse(json)).SessionLimitNotApplicable, "Empty response was treated as unlimited.");
        foreach (var duration in new int?[] { 18000, 3600, null })
        {
            var payload = PlanPayload();
            payload["rate_limit"]!["secondary_window"] = new JsonObject { ["used_percent"] = 10d, ["limit_window_seconds"] = duration };
            Check(!UsageClient.NormalizeWhamUsage(payload).SessionLimitNotApplicable, "Present or ambiguous session window was hidden.");
        }
        foreach (var value in new double?[] { null, -1, 101 })
        {
            var payload = PlanPayload(); payload["rate_limit"]!["primary_window"]!["used_percent"] = value;
            Check(!UsageClient.NormalizeWhamUsage(payload).SessionLimitNotApplicable, "Incomplete/invalid weekly quota inferred unlimited session.");
        }
        return Task.CompletedTask;
    }

    private static Task PlanCacheAndDashboardAsync() => InTemporaryStoreAsync(paths =>
    {
        var snapshot = Snapshot("u", "a", "w", "g", 1700000000);
        SeedProfiles(paths, snapshot, ("A", snapshot));
        var repo = Repository(paths); using var service = CreateService(paths);
        repo.SaveUsage("A", UsageClient.NormalizeWhamUsage(PlanPayload("prolite")));
        var cached = Repository(paths).LoadUsageCache().Accounts["A"];
        Check(cached.PlanType == "prolite" && cached.SessionLimitNotApplicable, "Plan lost on cache reload.");
        var profile = service.BuildDashboardState().Profiles.Single();
        Check(profile.PlanType == "prolite" && profile.SessionLimitNotApplicable, "Dashboard dropped plan/capability.");
        repo.SaveUsage("A", UsageRecord.FromError("offline"));
        profile = service.BuildDashboardState().Profiles.Single();
        Check(profile.Status == "error" && profile.PlanType == "prolite" && profile.SessionLimitNotApplicable, "Failure lost last known plan.");
        repo.SaveUsage("A", FakeUsageClient.Result());
        profile = service.BuildDashboardState().Profiles.Single();
        Check(profile.PlanType is null && !profile.SessionLimitNotApplicable && profile.SessionWindow!.UsedPercent.HasValue, "New observation kept obsolete capability.");
        // A contradictory cache must never hide a known session limit.
        repo.SaveUsage("A", FakeUsageClient.Result() with { PlanType = "pro", SessionLimitNotApplicable = true });
        Check(!repo.LoadUsageCache().Accounts["A"].SessionLimitNotApplicable, "Contradictory cache hid a session quota.");
        // Compatibility with actual pre-feature JSON: both new properties are absent.
        File.WriteAllText(paths.UsageCachePath, "{\"Accounts\":{\"A\":{\"Status\":\"ok\",\"Weekly\":{\"UsedPercent\":28,\"WindowDurationMins\":10080}}}}");
        cached = repo.LoadUsageCache().Accounts["A"];
        Check(cached.PlanType is null && !cached.SessionLimitNotApplicable && cached.Weekly.UsedPercent == 28, "Old cache compatibility broken.");
        return Task.CompletedTask;
    });

    private static Task AppServerPlanAsync()
    {
        var result = JsonNode.Parse("{\"rateLimits\":{\"planType\":\"pro\",\"primary\":{\"usedPercent\":28,\"windowDurationMins\":10080,\"resetsAt\":2000000000}}}");
        var usage = CodexAppServerClient.NormalizeRateLimits(result);
        Check(usage.PlanType == "pro" && usage.SessionLimitNotApplicable, "App-server plan normalization differs from HTTP.");
        result!["rateLimits"]!["primary"]!["windowDurationMins"] = 300;
        Check(!CodexAppServerClient.NormalizeRateLimits(result).SessionLimitNotApplicable, "App-server hid present session.");
        return Task.CompletedTask;
    }

    private static Task CompactPlanCardAsync() => RunOnUiThreadAsync(() =>
    {
        static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(o)!;
        foreach (var dark in new[] { false, true })
        {
            using var row = new AccountRow(new UiTheme(dark, false, false));
            var host = new W.Window { Content = row, Width = 330, SizeToContent = W.SizeToContent.Height,
                WindowStyle = W.WindowStyle.None, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            try
            {
            var profile = SampleProfile("Cuenta con un nombre deliberadamente muy largo", false) with { PlanType = "prolite" };
            void Layout() { host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); host.UpdateLayout(); }
            row.Update(profile, false); host.Show(); Layout(); var fullHeight = row.ActualHeight;
            var label = Field<C.TextBlock>(row, "_plan"); var name = Field<C.TextBlock>(row, "_name"); var badge = Field<C.Border>(row, "_planBadge");
            Check(label.Text == "Pro 100" && label.ActualWidth > 30 && name.ActualWidth > 30, "Long name displaced plan label.");
            var nameEnd = name.TransformToAncestor(row).Transform(new W.Point(name.ActualWidth, 0)).X;
            var badgeStart = badge.TransformToAncestor(row).Transform(new W.Point(0, 0)).X;
            Check(nameEnd <= badgeStart, "Account name overlaps plan badge.");
            row.Update(profile with { SessionLimitNotApplicable = true, SessionWindow = new(null, null, null) }, false); Layout();
            var sections = Field<C.StackPanel[]>(row, "_quotaSections");
            Check(sections[0].Visibility == W.Visibility.Collapsed && sections[1].Margin.Top == 0
                && fullHeight - row.ActualHeight > 40, $"Hiding session did not compact the card: full={fullHeight}, compact={row.ActualHeight}, visibility={sections[0].Visibility}, top={sections[1].Margin.Top}.");
            row.Update(profile with { PlanType = "plus" }, false); Layout();
            Check(sections[0].Visibility == W.Visibility.Visible && Math.Abs(row.ActualHeight - fullHeight) < 1, "Reused card could not restore session section.");
            row.Update(profile with { PlanType = null }, false);
            Check(badge.Visibility == W.Visibility.Collapsed, "Unqueried account showed a fabricated plan.");
            }
            finally { host.Close(); }
        }
        return Task.CompletedTask;
    });
}
