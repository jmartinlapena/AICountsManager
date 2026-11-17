using AICountsManager;
using System.Diagnostics;
using System.Globalization;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using W = System.Windows;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static C.Button MetricButton(ProviderOverview panel, string id) => MetricButtons(panel)[id];
    private static Dictionary<string, C.Button> MetricButtons(ProviderOverview panel) => UiField<Dictionary<string, C.Button>>(panel, "_buttons");
    private static string[] MetricText(ProviderOverview panel) => Descendants(panel).OfType<C.TextBlock>().Select(t => t.Text).ToArray();
    private static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject root)
    {
        foreach (var child in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static Task HistoryDaySelectionAsync() => RunOnUiThreadAsync(() =>
    {
        var theme = new UiTheme(true, false, false);
        var today = new DateOnly(2026, 11, 17);
        var panel = new ProviderOverview(theme, () => today);
        var data = ReferenceMetricsHistory();
        panel.Update(data);
        Check(panel.SelectedView == UsageView.Today && panel.DisplayedPeriod.Total == 146_800 && MetricText(panel).Contains("146.800")
            && !MetricButtons(panel).ContainsKey("previous"), "Today showed a navigable period or an incorrect total.");
        UiClick(MetricButton(panel, "provider-claude"));
        Check(MetricText(panel).Contains("Claude Sonnet") && MetricText(panel).Contains("42.400") && !MetricText(panel).Contains("Gemini Pro"),
            "Provider toggle showed the wrong models or totals.");

        panel.Select(UsageView.Week);
        var week = panel.PeriodStart;
        var weeklyTotal = panel.DisplayedPeriod.Total;
        var thisDay = "week-" + today.DayNumber;
        UiClick(MetricButton(panel, thisDay));
        Check(panel.SelectedDay == today && panel.DisplayedPeriod.Total == weeklyTotal
            && MetricText(panel).Contains("146.800 tokens"), "A week day selection replaced the week total or lost the exact number.");
        Check(!MetricButton(panel, "week-" + today.AddDays(1).DayNumber).IsEnabled, "A future day can be selected.");
        var focusBorder = MetricButton(panel, thisDay).BorderBrush;
        Check(focusBorder is M.SolidColorBrush b && b.Color.A > 0, "Selected week column has no visual outline.");
        panel.Select(UsageView.Quotas);
        Check(panel.PeriodStart == week && panel.DisplayedPeriod.Total == weeklyTotal, "Week and Quotas diverged on the current week.");

        UiClick(MetricButton(panel, "previous"));
        Check(panel.DisplayedPeriod.Total == 980_800 && panel.PeriodStart == new DateOnly(2026, 11, 9), "The previous week was not shared.");
        panel.Select(UsageView.Week);
        var wed = new DateOnly(2026, 11, 11);
        UiClick(MetricButton(panel, "week-" + wed.DayNumber));
        Check(MetricText(panel).Contains("980.800") && MetricText(panel).Contains("240.600 tokens"), "The exact day replaced the weekly summary.");
        panel.Update(data with { });
        panel.SetTheme(theme with { Dark = false });
        panel.Select(UsageView.Quotas);
        Check(panel.DisplayedPeriod.Total == 980_800 && MetricText(panel).Contains("980,8 k"), "A new snapshot or theme lost the chosen week.");
        panel.Select(UsageView.Week);
        Check(panel.SelectedDay == wed, "Weekly date selection was not restored.");
        UiClick(MetricButton(panel, "current"));
        Check(panel.PeriodStart == week && panel.SelectedDay is null, "This week did not clear an unrelated selection.");

        panel.Select(UsageView.Calendar);
        Check(panel.DisplayedPeriod.Total == 1_765_700 && MetricText(panel).Contains("15 días con actividad"), "Calendar total or activity-day count differs.");
        Check(!MetricButtons(panel).ContainsKey("month-" + today.AddDays(1).DayNumber), "A future calendar date can be selected.");
        UiClick(MetricButton(panel, "month-" + today.DayNumber));
        Check(panel.SelectedDay == today && panel.DisplayedPeriod.Total == 1_765_700 && MetricText(panel).Contains("146.800 tokens")
            && MetricText(panel).Contains("70.600") && MetricText(panel).Contains("24.800"), "Calendar day detail did not preserve the month and match Today.");
        UiClick(MetricButton(panel, "ranking"));
        Check(MetricText(panel).Contains("240.600 tokens"), "The monthly ranking did not reveal the top days.");
        panel.Select(UsageView.Today);
        today = today.AddDays(1);
        panel.RenderPending();
        Check(panel.DisplayedPeriod.Days.Single().Date == today, "Today did not follow the local day.");
        return Task.CompletedTask;
    });

    private static Task MetricsCoverageAsync() => RunOnUiThreadAsync(() =>
    {
        var today = new DateOnly(2027, 1, 5);
        var cutoff = MetricsPeriods.Cutoff(today);
        var cursorDay = new DateOnly(2027, 1, 4);
        var known = MetricsSnapshot(today, [(new DateOnly(2026, 12, 31), "gpt", 40L), (new DateOnly(2027, 1, 1), "gpt", 50L),
            (cutoff, "gpt", 7L), (cutoff.AddDays(-1), "expired", 900L), (today.AddDays(1), "future", 800L)]);
        var cursor = MetricsSnapshot(today, [(new DateOnly(2026, 12, 31), "cursor", 30L)]) with { CoveredDays = new HashSet<DateOnly> { cursorDay } };
        var merged = ProviderHistory.Merge(new Dictionary<string, TokenHistorySnapshot> { [ProviderIds.Codex] = known, [ProviderIds.Cursor] = cursor });
        var panel = new ProviderOverview(new UiTheme(true, false, false), () => today);
        panel.Update(merged);
        Check(!panel.DisplayedPeriod.HasKnownTotal && MetricText(panel).Contains("—"), "Cursor missing coverage became today's zero.");
        var history = new MetricsHistory(merged, today);
        Check(history.Read(cursorDay, cursorDay, today).HasKnownTotal && history.Read(cursorDay, cursorDay, today).Total == 0,
            "A fully observed zero was hidden.");
        var complete = ProviderHistory.Merge(new Dictionary<string, TokenHistorySnapshot> { [ProviderIds.Codex] = known });
        var interrupted = new MetricsHistory(complete with { Partial = true, CatchingUp = true }, today).Read(cursorDay, cursorDay, today);
        Check(interrupted.Partial && interrupted.CatchingUp, "An incomplete combined refresh lost its history status.");
        panel.Select(UsageView.Week);
        panel.ChoosePeriod(new DateOnly(2027, 1, 1));
        Check(panel.PeriodStart == new DateOnly(2026, 12, 28) && panel.DisplayedPeriod.Total == 120 && panel.DisplayedPeriod.Partial
            && MetricText(panel).Any(t => t.Contains("28 dic 2026") && t.Contains("3 ene 2027")), "Cross-year week lost dates, cached activity, or coverage warning.");
        panel.Select(UsageView.Quotas);
        Check(panel.DisplayedPeriod.Total == 120 && panel.DisplayedPeriod.MissingProviders.SequenceEqual([ProviderIds.Cursor]), "Quota coverage differs from the chosen week.");
        panel.Select(UsageView.Week);
        panel.ChoosePeriod(cutoff);
        Check(panel.DisplayedPeriod.Total == 7 && panel.DisplayedPeriod.Clipped && !MetricButton(panel, "previous").IsEnabled,
            "The week crossing retention was skipped or included expired data.");
        panel.Select(UsageView.Calendar);
        panel.ChoosePeriod(cutoff);
        Check(panel.DisplayedPeriod.Total == 7 && panel.DisplayedPeriod.Clipped && !MetricButton(panel, "previous").IsEnabled,
            "The month crossing retention was skipped or included expired data.");
        return Task.CompletedTask;
    });

    private static Task MetricsNavigationIoAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var today = new DateOnly(2026, 11, 17);
        var client = new FakeUsageClient();
        var provider = new FakeQuotaProvider();
        var auth = Snapshot("u", "a", "w", "1", 1700000000); SeedProfiles(paths, auth, ("Personal", auth));
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), client, false, provider);
        using var form = new WidgetForm(service, new UiTheme(true, false, false), false, () => today);
        form.Visual.Topmost = false; form.Visual.Left = form.Visual.Top = -20000;
        form.ApplyState(service.BuildDashboardState());
        form.SetHistoryPreview(ReferenceMetricsHistory());
        form.Visual.Show();
        var panel = UiField<ProviderOverview>(form, "_overview");
        string[] Files() => Directory.EnumerateFiles(paths.AppHome, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => path + "|" + File.GetLastWriteTimeUtc(path).Ticks).ToArray();
        var before = Files();
        UiClick(MetricButton(panel, "provider-claude"));
        panel.Select(UsageView.Week);
        var wed = new DateOnly(2026, 11, 11);
        C.Calendar OpenCalendar()
        {
            UiClick(MetricButton(panel, "picker"));
            Check(panel.PickerOpen, "The period caption did not open its date selector.");
            var popup = UiField<C.Primitives.Popup>(panel, "_picker");
            return (C.Calendar)((C.Border)popup.Child).Child;
        }
        OpenCalendar().SelectedDate = wed.ToDateTime(TimeOnly.MinValue);
        Check(panel.PeriodStart == new DateOnly(2026, 11, 9) && !panel.PickerOpen, "Selecting a date did not navigate to its week.");
        UiClick(MetricButton(panel, "week-" + wed.DayNumber));
        var week = panel.PeriodStart;
        panel.Select(UsageView.Quotas);
        panel.Select(UsageView.Calendar);
        var calendar = OpenCalendar();
        Check(calendar.DisplayMode == C.CalendarMode.Year, "The monthly selector did not open at the month level.");
        calendar.DisplayDate = new DateTime(2026, 10, 1);
        calendar.DisplayMode = C.CalendarMode.Month;
        Check(panel.PeriodStart == new DateOnly(2026, 10, 1) && !panel.PickerOpen, "Selecting a month did not update the calendar view.");
        UiClick(MetricButton(panel, "next"));
        UiClick(MetricButton(panel, "ranking"));
        UiClick(MetricButton(panel, "month-" + today.DayNumber));
        panel.Select(UsageView.Week);
        Check(client.Calls == 0 && provider.Calls == 0 && before.SequenceEqual(Files()) && service.History.UpdatedAt is null,
            "A local metric action wrote files, queried quotas or read the history.");
        var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(form.Visual)!,
            Environment.TickCount, System.Windows.Input.Key.F5) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        var refresh = (C.Button)form.Visual.FindName("RefreshButton");
        form.Visual.RaiseEvent(key);
        await EventuallyAsync(() => Task.FromResult(refresh.IsEnabled && client.Calls == 1));
        Check(panel.PeriodStart == week && panel.SelectedDay == wed && panel.SelectedView == UsageView.Week, "F5 reset the displayed period or day.");
        var refreshed = panel.DisplayedPeriod.Total;
        panel.Select(UsageView.Quotas);
        Check(panel.PeriodStart == week && panel.DisplayedPeriod.Total == refreshed, "F5 desynchronized Week and Quotas.");
    }));

    private static Task MetricsMaterialAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        using var service = CreateService(paths);
        foreach (var (transparency, contrast) in new[] { (true, false), (false, false), (true, true) })
        {
            var theme = new UiTheme(true, transparency, contrast);
            using var form = new WidgetForm(service, theme, false);
            form.ApplyState(SampleState());
            form.Visual.Topmost = false; form.Visual.Left = form.Visual.Top = -20000;
            form.Visual.Show();
            var shell = (C.Border)form.Visual.FindName("Shell");
            var brush = (M.SolidColorBrush)shell.Background;
            var backdrop = WindowMaterial.ReadBackdrop(form.Handle);
            if (form.AcrylicEnabled)
                Check(brush.Color.A == 0 && ((M.SolidColorBrush)form.Visual.Background).Color.A == 0 && backdrop == 3 && transparency && !contrast,
                    "The outer shell still tints enabled DWM acrylic or used the wrong material.");
            else
                Check(brush.Color == theme.Base, "The window did not fall back to the accessible solid background.");
            var bounds = (form.Visual.Left, form.Visual.Top, form.Visual.Width, form.Visual.Height);
            form.ShowSettings(true);
            Check(ReferenceEquals(brush, shell.Background) && backdrop == WindowMaterial.ReadBackdrop(form.Handle)
                && bounds == (form.Visual.Left, form.Visual.Top, form.Visual.Width, form.Visual.Height), "Settings changed the actual window bounds or shell material.");
            Console.WriteLine($"MATERIAL transparency={transparency} highContrast={contrast} active={form.AcrylicEnabled} DWM={backdrop} tintAlpha={brush.Color.A}");
        }
        return Task.CompletedTask;
    }));

    private static Task RenderMetricsAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        Directory.CreateDirectory(output);
        var date = new DateOnly(2026, 11, 17);
        using var service = CreateService(paths);
        using var form = CreateMetricsDemo(service, acrylic: false);
        form.Visual.Topmost = false; form.Visual.Left = form.Visual.Top = -20000;
        form.Visual.Show(); form.Visual.UpdateLayout();
        var panel = UiField<ProviderOverview>(form, "_overview");
        UiClick(MetricButton(panel, "provider-claude"));
        SaveResetDemo(form.Visual, Path.Combine(output, "hoy.png"));
        panel.Select(UsageView.Week);
        panel.ChoosePeriod(date.AddDays(-7));
        UiClick(MetricButton(panel, "week-" + new DateOnly(2026, 11, 11).DayNumber));
        form.Visual.UpdateLayout();
        SaveResetDemo(form.Visual, Path.Combine(output, "semana.png"));
        panel.Select(UsageView.Quotas);
        form.Visual.UpdateLayout();
        SaveResetDemo(form.Visual, Path.Combine(output, "cuotas.png"));
        panel.Select(UsageView.Calendar);
        form.Visual.UpdateLayout();
        SaveResetDemo(form.Visual, Path.Combine(output, "calendario.png"));
        return Task.CompletedTask;
    }));

    private static WidgetForm CreateMetricsDemo(AICountsManagerService service, bool acrylic)
    {
        var date = new DateOnly(2026, 11, 17);
        var now = DateTimeOffset.UtcNow;
        var theme = new UiTheme(true, acrylic, false, M.Color.FromRgb(29, 45, 46), M.Color.FromRgb(76, 122, 122));
        var form = new WidgetForm(service, theme, false, () => date);
        form.Visual.Height = 710;
        var personal = SampleProfile("Personal", true) with { PlanType = "prolite", Email = "personal@ejemplo.com", SessionLimitNotApplicable = true,
            WeeklyWindow = new(28, 10080, now.AddDays(4).AddHours(17).AddMinutes(15).ToUnixTimeSeconds()), WeeklyRemainingPercent = 72, CheckedAt = now.ToString("O") };
        var claude = new ProviderUsage(ProviderIds.Claude, "example", "Claude personal", "claude@ejemplo.com", "Pro", now.ToString("O"), "ok", null,
            [new("five_hour", "Claude", "Sesión · 5 h", 14, 300, now.AddHours(2).AddMinutes(12).AddSeconds(15).ToUnixTimeSeconds()),
             new("seven_day", "Claude", "Semanal", 42, 10080, now.AddDays(3).AddHours(4).ToUnixTimeSeconds())]);
        form.ApplyState(SampleState() with { ActiveProfile = personal, Profiles = [personal], OtherAccounts = [claude, SampleAntigravity()] });
        form.SetHistoryPreview(ReferenceMetricsHistory());
        return form;
    }

    private static TokenHistorySnapshot ReferenceMetricsHistory()
    {
        var month = new DateOnly(2026, 11, 1);
        var today = month.AddDays(16);
        var sources = new Dictionary<string, List<(DateOnly Day, string Model, long Count)>>
        { [ProviderIds.Codex] = [], [ProviderIds.Claude] = [], [ProviderIds.Antigravity] = [] };
        void Add(int day, long codex, long claude, long agy)
        {
            var date = month.AddDays(day - 1);
            sources[ProviderIds.Codex].Add((date, "GPT", codex));
            sources[ProviderIds.Claude].Add((date, "Claude Sonnet", claude));
            sources[ProviderIds.Antigravity].Add((date, "Gemini Pro", agy));
        }
        long[] early = [72_000, 74_200, 85_200, 167_400, 47_900, 63_100];
        for (var i = 0; i < early.Length; i++) Add(2 + i, early[i] / 2, early[i] / 4, early[i] / 4);
        long[] daily = [98_000, 176_000, 240_600, 142_000, 209_000, 49_000, 66_200];
        long[] codex = [61_000, 85_000, 143_000, 85_000, 117_000, 25_500, 42_600];
        long[] claude = [23_000, 60_000, 67_000, 42_000, 59_000, 16_000, 12_000];
        for (var i = 0; i < daily.Length; i++) Add(9 + i, codex[i], claude[i], daily[i] - codex[i] - claude[i]);
        Add(16, 70_000, 30_000, 28_300);
        Add(17, 70_600, 42_400, 24_800);
        sources[ProviderIds.Claude].Add((today, "Claude Opus", 9_000));
        return ProviderHistory.Merge(sources.ToDictionary(p => p.Key, p => MetricsSnapshot(today, p.Value)));
    }

    private static TokenHistorySnapshot MetricsSnapshot(DateOnly asOf, IEnumerable<(DateOnly Day, string Model, long Count)> tokens)
    {
        var days = tokens.GroupBy(row => row.Day).Select(group => new TokenDay(group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            group.ToDictionary(row => row.Model, row => row.Count))).ToArray();
        var stamp = new DateTimeOffset(asOf.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local));
        return new(days, false, stamp.ToString("O"), null);
    }
}
