using AICountsManager;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
namespace AICountsManager.Tests;
internal static partial class Program
{
    private static Task WeeklyOnlyPaceAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        var now = DateTimeOffset.UtcNow;
        Check(QuotaPace.Calculate(new(75, 300, now.AddHours(2).ToUnixTimeSeconds()), now) is null, "Five-hour window has a pace projection.");
        Check(QuotaPace.Calculate(new(75, 1440, now.AddHours(12).ToUnixTimeSeconds()), now) is null, "Daily window has a weekly pace projection.");
        var reading = SampleAntigravity() with { Quotas = SampleAntigravity().Quotas.Select(q => q with { UsedPercent = 75, ResetsAt = now.AddMinutes(q.Minutes == 300 ? 120 : 5760).ToUnixTimeSeconds() }).ToArray() };
        var card = new CurrentProviderCard(new UiTheme(true, false, false)); card.Update(reading);
        static IEnumerable<W.FrameworkElement> Descendants(W.DependencyObject parent)
        {
            for (var i = 0; i < M.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = M.VisualTreeHelper.GetChild(parent, i);
                if (child is W.FrameworkElement element) yield return element;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        var elements = Descendants(card).ToArray();
        Check(elements.Count(e => Equals(e.Tag, "weekly-pace-marker")) == 3, "External card marker not restricted to its three weekly limits.");
        var forecasts = elements.OfType<C.TextBlock>().Where(e => Equals(e.Tag, "weekly-forecast")).ToArray();
        Check(forecasts.Length == 3 && forecasts.All(f => f.Text.StartsWith("⚠ Límite en ")), "External weekly limit forecast missing.");
        var week = reading.Quotas.First().Window;
        Check(forecasts[0].Text == UsagePresentation.Forecast(QuotaPace.Calculate(week, DateTimeOffset.UtcNow)!), "Provider-specific forecast diverged from common calculation.");
        var young = reading with { Quotas = reading.Quotas.Select(q => q with { ResetsAt = now.AddMinutes(q.Minutes!.Value).ToUnixTimeSeconds() }).ToArray() };
        card.Update(young);
        Check(Descendants(card).Count(e => Equals(e.Tag, "weekly-pace-marker")) == 3 && !Descendants(card).Any(e => Equals(e.Tag, "weekly-forecast")), "New weekly window hides reference or invents a premature forecast.");
        card.Update(reading with { Status = "stale" });
        Check(!Descendants(card).Any(e => Equals(e.Tag, "weekly-pace-marker") || Equals(e.Tag, "weekly-forecast")), "Stale external data still projects usage.");
        card.Update(reading with { Quotas = reading.Quotas.Select(q => q with { UsedPercent = null }).ToArray() });
        Check(!Descendants(card).Any(e => Equals(e.Tag, "weekly-pace-marker")), "Missing external quota shows a pace marker.");
        using var row = new AccountRow(new UiTheme(true, false, false));
        row.Update(SampleState().Profiles[0] with { Status = "ok", SessionWindow = new(75,300,now.AddHours(2).ToUnixTimeSeconds()), WeeklyWindow = week }, false);
        var markers = UiField<C.Border[]>(row, "_markers"); var projected = UiField<C.TextBlock[]>(row, "_forecast");
        Check(markers[0].Visibility == W.Visibility.Collapsed && projected[0].Text == "" && markers[1].Visibility == W.Visibility.Visible && projected[1].Text == forecasts[0].Text, "Codex does not use the same weekly-only calculation.");
        return Task.CompletedTask;
    }));
    private static Task UnifiedOrderPersistsAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("u-a", "a", "w", "g", 1700000000); var b = Snapshot("u-b", "b", "w", "g", 1700000000);
        SeedProfiles(paths, a, ("A", a), ("B", b)); var repo = Repository(paths);
        repo.SaveAppConfig(repo.LoadAppConfig() with { ProfileOrder = ["B", "A"] });
        var provider = new FakeQuotaProvider { Result = SampleAntigravity() with { Name = "A" } };
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, provider);
        var agy = AccountOrdering.Current(ProviderIds.Antigravity);
        var auth = File.ReadAllBytes(paths.ActiveAuthPath);
        Check(service.BuildDashboardState().OrderedAccounts.Select(x => x.Id).SequenceEqual(new[] { "codex:B", "codex:A", agy }), "Legacy account order was not preserved.");
        await service.SetAccountOrderAsync(["codex:B", agy, "codex:A"]);
        Check(Repository(paths).LoadAppConfig().AccountOrder!.SequenceEqual(new[] { "codex:B", agy, "codex:A" }), "Unified order not persisted.");
        using (var restarted = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, provider))
            Check(restarted.BuildDashboardState().OrderedAccounts[1].Id == agy, "Provider position lost on restart.");
        await service.RenameProfileAsync("B", "Renamed");
        Check(service.BuildDashboardState().OrderedAccounts[0].Id == "codex:Renamed" && service.BuildDashboardState().OrderedAccounts[1].Id == agy, "Rename moved provider/account position.");
        await service.SetAntigravityEnabledAsync(false);
        await service.SetAccountOrderAsync(["codex:A", "codex:Renamed"]);
        await service.SetAntigravityEnabledAsync(true);
        Check(service.BuildDashboardState().OrderedAccounts.Select(x => x.Id).SequenceEqual(new[] { "codex:A", agy, "codex:Renamed" }), "Disabled provider position was lost.");
        await service.DeleteProfileAsync("Renamed");
        Check(!repo.LoadAppConfig().AccountOrder!.Contains("codex:Renamed"), "Deleted account retained order key.");
        var rejected = false;
        try { await service.SetAccountOrderAsync(["codex:A", "codex:A"]); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.ReadAllBytes(paths.ActiveAuthPath).SequenceEqual(auth), "Invalid order accepted or active credentials changed.");
        Check(service.BuildDashboardState().Profiles.Count == 1, "External account was written as a switchable Codex profile.");
    });
    private static Task UnifiedOrderUiAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var a = Snapshot("u-a", "a", "w", "g", 1700000000); SeedProfiles(paths, a, ("A", a));
        var provider = new FakeQuotaProvider { Result = SampleAntigravity() with { Name = "A" } };
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, provider);
        await service.SetAntigravityEnabledAsync(true);
        using var form = new WidgetForm(service, new UiTheme(true, false, false), false);
        form.ApplyState(service.BuildDashboardState()); form.ShowSettings(true);
        var order = (C.StackPanel)form.Visual.FindName("OrderList");
        Check(order.Children.Count == 2, "External account absent from settings.");
        UiClick((C.Button)((C.Grid)order.Children[1]).Children[1]);
        await EventuallyAsync(() => Task.FromResult(order.IsEnabled));
        Check(((C.TextBlock)((C.Grid)order.Children[0]).Children[0]).Text == "Antigravity · A", "External settings row did not move above Codex.");
        var cards = (C.StackPanel)form.Visual.FindName("AccountList");
        var card = cards.Children[0];
        Check(card is CurrentProviderCard && cards.Children[1] is AccountRow, "External card is not interleaved with Codex cards.");
        form.ApplyState(service.BuildDashboardState());
        Check(ReferenceEquals(card, cards.Children[0]), "Refresh rebuilt or moved external card.");
        await service.SetAntigravityEnabledAsync(false); form.ApplyState(service.BuildDashboardState());
        Check(cards.Children.Count == 1 && cards.Children[0] is AccountRow, "Disabling provider removed another card.");
    }));
}
