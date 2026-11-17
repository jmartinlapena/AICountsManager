using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using S = System.Windows.Shapes;

namespace AICountsManager;

internal enum UsageView { Today, Week, Quotas, Calendar }

// All navigational state belongs to this card. Its controls only use a delivered history snapshot.
internal sealed partial class ProviderOverview : C.Border
{
    private static readonly string[] Weekdays = ["L", "M", "X", "J", "V", "S", "D"];
    private readonly Func<DateOnly> _todayDate;
    private UiTheme _theme;
    private readonly C.StackPanel _root = new(), _content = new();
    private readonly C.Border _tabsShell = new() { CornerRadius = new(9), Padding = new(3) };
    private readonly C.Button _today = Tab("Hoy"), _week = Tab("Semana"), _quotas = Tab("Cuotas"), _calendar = Tab("Calendario");
    private readonly HashSet<string> _expandedProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, C.Button> _buttons = new(StringComparer.Ordinal);
    private C.Primitives.Popup? _picker;
    private TokenHistorySnapshot _snapshot = TokenHistorySnapshot.Empty;
    private MetricsHistory? _history;
    private DateOnly? _lastRenderDate, _weekStart, _monthStart, _selectedWeekDay, _selectedMonthDay;
    private bool _dirty = true, _rankingExpanded;

    public UsageView SelectedView { get; private set; } = UsageView.Today;
    internal bool PickerOpen => _picker?.IsOpen == true;
    internal MetricsPeriod DisplayedPeriod { get; private set; } = new([], [], [], false, false, false);
    internal IReadOnlyList<ProviderTokenSeries> Series => DisplayedPeriod.Series;
    internal DateOnly? SelectedDay => SelectedView == UsageView.Calendar ? _selectedMonthDay : _selectedWeekDay;
    internal DateOnly PeriodStart => SelectedView == UsageView.Calendar
        ? _monthStart ?? MetricsPeriods.Month(_todayDate())
        : _weekStart ?? MetricsPeriods.Monday(_todayDate());

    public ProviderOverview(UiTheme theme, Func<DateOnly>? today = null)
    {
        _theme = theme;
        _todayDate = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        CornerRadius = new(13);
        Padding = new(12, 9, 12, 12);
        Margin = new(0, 0, 0, 10);
        Child = _root;
        var tabs = new C.Primitives.UniformGrid { Columns = 4 };
        C.Button[] buttons = [_today, _week, _quotas, _calendar];
        for (var i = 0; i < buttons.Length; i++)
        {
            var view = (UsageView)i;
            buttons[i].Click += (_, _) => Select(view);
            tabs.Children.Add(buttons[i]);
        }
        _tabsShell.Child = tabs;
        _root.Children.Add(_tabsShell);
        _root.Children.Add(_content);
    }

    public void SetTheme(UiTheme theme, bool render = true)
    {
        if (_theme != theme) { _theme = theme; _dirty = true; }
        if (render) RenderPending();
    }

    public void Update(TokenHistorySnapshot snapshot, bool render = true)
    {
        if (!ReferenceEquals(_snapshot, snapshot)) { _snapshot = snapshot; _history = null; _dirty = true; }
        if (render) RenderPending();
    }

    public void ClosePicker()
    {
        if (_picker is { IsOpen: true } picker) picker.IsOpen = false;
    }

    internal void Select(UsageView view)
    {
        if (view != SelectedView) { SelectedView = view; _dirty = true; ClosePicker(); }
        RenderPending();
    }

    internal void ChoosePeriod(DateOnly date)
    {
        if (SelectedView == UsageView.Today) return;
        var today = _todayDate();
        date = date < MetricsPeriods.Cutoff(today) ? MetricsPeriods.Cutoff(today) : date > today ? today : date;
        if (SelectedView == UsageView.Calendar)
        {
            var month = MetricsPeriods.Month(date);
            _monthStart = month == MetricsPeriods.Month(today) ? null : month;
            if (_selectedMonthDay is { } selected && MetricsPeriods.Month(selected) != month) _selectedMonthDay = null;
        }
        else
        {
            var week = MetricsPeriods.Monday(date);
            _weekStart = week == MetricsPeriods.Monday(today) ? null : week;
            if (_selectedWeekDay is { } selected && MetricsPeriods.Monday(selected) != week) _selectedWeekDay = null;
        }
        Redraw();
    }

    private void Redraw() { _dirty = true; RenderPending(); }

    public void RenderPending()
    {
        var today = _todayDate();
        if (!_dirty && _lastRenderDate == today) return;
        _dirty = false;
        _lastRenderDate = today;
        ClosePicker();
        var focus = (System.Windows.Input.Keyboard.FocusedElement as C.Button)?.Tag as string;
        _buttons.Clear();
        _content.Children.Clear();
        Background = UiTheme.Brush(_theme.Dark ? M.Colors.White : M.Colors.Black, _theme.Dark ? (byte)12 : (byte)10);
        _tabsShell.Background = UiTheme.Brush(_theme.Dark ? M.Colors.Black : _theme.Text, 18);
        C.Button[] tabs = [_today, _week, _quotas, _calendar];
        for (var i = 0; i < tabs.Length; i++) StyleTab(tabs[i], (UsageView)i == SelectedView);

        _weekStart = RetainedAnchor(_weekStart, MetricsPeriods.Monday(MetricsPeriods.Cutoff(today)), MetricsPeriods.Monday(today));
        _monthStart = RetainedAnchor(_monthStart, MetricsPeriods.Month(MetricsPeriods.Cutoff(today)), MetricsPeriods.Month(today));
        _history ??= new MetricsHistory(_snapshot, today);
        var start = SelectedView == UsageView.Today ? today : PeriodStart;
        var end = SelectedView switch { UsageView.Today => today, UsageView.Calendar => start.AddMonths(1).AddDays(-1), _ => start.AddDays(6) };
        DisplayedPeriod = _history.Read(start, end, today);

        if (SelectedView != UsageView.Today) RenderNavigation(start, end, today);
        switch (SelectedView)
        {
            case UsageView.Today: RenderToday(today); break;
            case UsageView.Week: RenderWeek(start, today); break;
            case UsageView.Quotas: RenderQuotas(start, today); break;
            case UsageView.Calendar: RenderMonth(start, end, today); break;
        }
        RenderCoverage();
        if (focus is not null && _buttons.TryGetValue(focus, out var target) && target.IsEnabled) target.Focus();
    }

    private static DateOnly? RetainedAnchor(DateOnly? selected, DateOnly min, DateOnly current) =>
        selected is { } date && date < current ? date < min ? min : date : null;

    private void RenderToday(DateOnly today)
    {
        Add(Label(Cap(today.ToString("dddd, d 'de' MMMM", MetricsHistory.Culture)), 11, color: _theme.Muted), 14);
        var number = new C.StackPanel { Orientation = C.Orientation.Horizontal };
        number.Children.Add(LargeTotal());
        var units = Label("tokens", 11, color: _theme.Muted);
        units.VerticalAlignment = W.VerticalAlignment.Bottom;
        units.Margin = new(8, 0, 0, 6);
        number.Children.Add(units);
        Add(number, 4);

        if (Series.Count == 0)
        {
            Add(Label(DisplayedPeriod.HasKnownTotal ? "Sin actividad hoy" : "Sin historial disponible", 10, color: _theme.Muted), 10);
            return;
        }
        var bar = new C.Grid { Height = 6 };
        for (var i = 0; i < Series.Count; i++)
        {
            var item = Series[i];
            if (i > 0) bar.ColumnDefinitions.Add(new() { Width = new W.GridLength(2) });
            bar.ColumnDefinitions.Add(new() { Width = new W.GridLength((double)item.Tokens, W.GridUnitType.Star) });
            Put(bar, new C.Border { Background = ProviderBrush(item.Provider), CornerRadius = new(2) }, i * 2);
        }
        Add(bar, 12);
        var onlyProvider = Series.Count == 1;
        for (var i = 0; i < Series.Count; i++)
        {
            var item = Series[i];
            if (i != 0) Add(Rule(), 0);
            var expanded = onlyProvider || _expandedProviders.Contains(item.Provider);
            var row = Columns(W.GridLength.Auto, new(1, W.GridUnitType.Star), W.GridLength.Auto, new(56), new(20));
            Put(row, Dot(item.Provider), 0);
            Put(row, Label(ProviderIds.Name(item.Provider), 12, true), 1);
            Put(row, Label(MetricsHistory.Number(item.Tokens), 12, true), 2);
            Put(row, Right(Percent(item.Tokens), 10.5, color: _theme.Muted), 3);
            if (!onlyProvider) Put(row, Chevron(expanded ? "\uE70E" : "\uE70D"), 4);
            if (onlyProvider) { row.Height = 33; Add(row, i == 0 ? 4 : 0); }
            else
            {
                var button = ActionButton(row, "provider-" + item.Provider,
                    $"{ProviderIds.Name(item.Provider)} · {MetricsHistory.Number(item.Tokens)} tokens · {Percent(item.Tokens)} · {(expanded ? "Ocultar" : "Mostrar")} modelos",
                    () => { if (!_expandedProviders.Add(item.Provider)) _expandedProviders.Remove(item.Provider); Redraw(); }, 32);
                Add(button, i == 0 ? 4 : 0);
            }

            if (!expanded) continue;
            var models = new C.StackPanel();
            foreach (var model in _history!.Models(today, item.Provider))
                models.Children.Add(Pair(Label(model.Name, 10.5, color: _theme.Muted), Right(MetricsHistory.Number(model.Tokens), 10.5), 19));
            if (models.Children.Count > 0) Add(new C.Border { Child = models,
                Background = UiTheme.Brush(_theme.Text, 12), CornerRadius = new(6), Padding = new(11, 3, 14, 5) }, 0, 8, 15);
        }
    }

    private void RenderWeek(DateOnly start, DateOnly today)
    {
        Add(LargeTotal(), 11);
        Add(Pair(Label("Tokens de la semana", 10.5, color: _theme.Muted), CurrentPeriodButton(start != MetricsPeriods.Monday(today), today, "Esta semana"), 16), 2);
        var days = DisplayedPeriod.Days.ToDictionary(d => d.Date);
        var providerOrder = Series.Select(s => s.Provider).ToArray();
        var chart = new C.Primitives.UniformGrid { Columns = 7, Rows = 1 };
        var max = Math.Max(1, days.Values.Select(d => d.Total).DefaultIfEmpty(0).Max());
        for (var i = 0; i < 7; i++)
        {
            var date = start.AddDays(i);
            var day = days.GetValueOrDefault(date);
            var selected = date == _selectedWeekDay;
            var bar = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Bottom, Width = 27 };
            foreach (var provider in providerOrder)
                if (day?.Series.FirstOrDefault(s => s.Provider == provider) is { } series)
                    bar.Children.Add(new C.Border { Height = 68 * (double)(series.Tokens / max), Background = ProviderBrush(provider) });
            if (day is not null && day.Total == 0)
                bar.Children.Add(new C.Border { Height = day.Covered ? 2 : 1, Background = UiTheme.Brush(_theme.Muted, day.Covered ? (byte)85 : (byte)38) });
            var cell = new C.StackPanel();
            var band = new C.Grid { Height = 79 };
            band.Children.Add(bar);
            cell.Children.Add(band);
            cell.Children.Add(Rule());
            var label = Label(Weekdays[i], 10, selected, selected ? _theme.Text : _theme.Muted);
            label.HorizontalAlignment = W.HorizontalAlignment.Center;
            label.Margin = new(0, 4, 0, 0);
            cell.Children.Add(label);
            var button = ActionButton(cell, "week-" + date.DayNumber, DayName(date, day),
                () => { _selectedWeekDay = _selectedWeekDay == date ? null : date; Redraw(); }, 102);
            button.Margin = new(1, 0, 1, 0);
            button.IsEnabled = day is not null;
            button.BorderThickness = new(1);
            button.BorderBrush = selected ? UiTheme.Brush(_theme.Accent, 118) : M.Brushes.Transparent;
            if (selected) button.Background = UiTheme.Brush(_theme.Accent, 22);
            chart.Children.Add(button);
        }
        Add(chart, 9);
        if (_selectedWeekDay is { } selectedDate && days.TryGetValue(selectedDate, out var selectedDay))
            Add(Pair(Label(Cap(selectedDate.ToString("dddd d", MetricsHistory.Culture)), 11, true), Right(DayAmount(selectedDay), 11, true), 20), 9);
        if (Series.Count > 0) Add(ProviderKey(), 7);
    }

    private void RenderQuotas(DateOnly start, DateOnly today)
    {
        Add(Label("Reparto de tokens", 12, true), 12);
        var body = Columns(new(120), new(1, W.GridUnitType.Star));
        body.MinHeight = 130;
        Put(body, Ring(), 0);
        var legend = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
        foreach (var item in Series)
        {
            var line = Columns(W.GridLength.Auto, new(1, W.GridUnitType.Star), W.GridLength.Auto);
            Put(line, Dot(item.Provider), 0);
            var provider = new C.StackPanel();
            provider.Children.Add(Label(ProviderIds.Name(item.Provider), 12, true));
            provider.Children.Add(Label(MetricsHistory.Number(item.Tokens) + " tokens", 10, color: _theme.Muted));
            Put(line, provider, 1);
            var percent = Right(Percent(item.Tokens), 11, true);
            percent.VerticalAlignment = W.VerticalAlignment.Top;
            Put(line, percent, 2);
            line.Margin = new(0, 4, 0, 5);
            legend.Children.Add(line);
        }
        if (Series.Count == 0) legend.Children.Add(Label(DisplayedPeriod.HasKnownTotal ? "Sin actividad esta semana" : "Sin historial disponible", 10, color: _theme.Muted));
        Put(body, legend, 1);
        Add(body, 8);
        Add(Rule(), 8);
        var current = start == MetricsPeriods.Monday(today);
        Add(Pair(Label(current ? "Semana actual" : "Semana anterior", 10.5, color: _theme.Muted),
            CurrentPeriodButton(!current, today, "Esta semana"), 19), 5);
    }

    private C.Canvas Ring()
    {
        const double center = 50, radius = 44, stroke = 12;
        var ring = new C.Canvas { Width = 100, Height = 100, VerticalAlignment = W.VerticalAlignment.Center, Margin = new(5, 0, 0, 0), HorizontalAlignment = W.HorizontalAlignment.Left };
        ring.Children.Add(new S.Ellipse { Width = 100, Height = 100, StrokeThickness = stroke, Stroke = UiTheme.Brush(_theme.Text, 25) });
        var start = -Math.PI / 2;
        foreach (var item in Series)
        {
            var brush = ProviderBrush(item.Provider);
            var fraction = (double)(item.Tokens / DisplayedPeriod.Total);
            var end = start + fraction * Math.Tau;
            if (Series.Count == 1)
                ring.Children.Add(new S.Ellipse { Width = 100, Height = 100, StrokeThickness = stroke, Stroke = brush });
            else
            {
                var geometry = new M.StreamGeometry();
                var gap = Math.Min(.009, (end - start) / 4);
                using (var path = geometry.Open())
                {
                    W.Point Point(double angle) => new(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
                    path.BeginFigure(Point(start + gap), false, false);
                    path.ArcTo(Point(end - gap), new(radius, radius), 0, end - start - 2 * gap > Math.PI, M.SweepDirection.Clockwise, true, false);
                }
                geometry.Freeze();
                ring.Children.Add(new S.Path { Data = geometry, Stroke = brush, StrokeThickness = stroke });
            }
            start = end;
        }
        var label = new C.StackPanel { Width = 80, IsHitTestVisible = false };
        var text = Label(DisplayedPeriod.HasKnownTotal ? Compact(DisplayedPeriod.Total) : "—", 17, true);
        text.TextAlignment = W.TextAlignment.Center;
        var unit = Label("tokens", 9.5, color: _theme.Muted);
        unit.TextAlignment = W.TextAlignment.Center;
        label.Children.Add(text);
        label.Children.Add(unit);
        C.Canvas.SetTop(label, 34); C.Canvas.SetLeft(label, 10);
        ring.Children.Add(label);
        W.Automation.AutomationProperties.SetName(ring, DisplayedPeriod.HasKnownTotal ? MetricsHistory.Number(DisplayedPeriod.Total) + " tokens" : "Sin historial disponible");
        return ring;
    }

    private C.Primitives.UniformGrid ProviderKey()
    {
        var legend = new C.Primitives.UniformGrid { Columns = Math.Min(Series.Count, 4) };
        foreach (var item in Series)
        {
            var row = Columns(W.GridLength.Auto, new(1, W.GridUnitType.Star));
            Put(row, Dot(item.Provider, 6, 4), 0);
            Put(row, Label(ProviderIds.Name(item.Provider), 10, color: _theme.Muted), 1);
            legend.Children.Add(row);
        }
        return legend;
    }

    private void RenderCoverage()
    {
        var period = DisplayedPeriod;
        if (!period.Partial) return;
        var details = new List<string>();
        if (period.CatchingUp) details.Add("Indexando historial…");
        else if (period.HasKnownTotal && period.MissingProviders.Count > 0) details.Add("Historial parcial");
        if (period.Clipped) details.Add("Periodo recortado por la retención");
        if (period.MissingProviders.Count > 0) details.Add("Cobertura incompleta: " + string.Join(", ", period.MissingProviders.Select(ProviderIds.Name)));
        if (details.Count == 0) details.Add("Historial parcial");
        var label = Label(string.Join(" · ", details), 9.5, color: _theme.Muted);
        label.TextWrapping = W.TextWrapping.Wrap;
        Add(label, 7);
    }

    private C.TextBlock LargeTotal() => Label(DisplayedPeriod.HasKnownTotal ? MetricsHistory.Number(DisplayedPeriod.Total) : "—", 26, true);
    private static string Cap(string text) => char.ToUpper(text[0], MetricsHistory.Culture) + text[1..];
    private static string Compact(decimal total) => total switch
    {
        >= 1_000_000_000 => (total / 1_000_000_000).ToString("0.#", MetricsHistory.Culture) + " mil M",
        >= 1_000_000 => (total / 1_000_000).ToString("0.#", MetricsHistory.Culture) + " M",
        >= 1_000 => (total / 1_000).ToString("0.#", MetricsHistory.Culture) + " k",
        _ => MetricsHistory.Number(total),
    };
    private string Percent(decimal amount) => (DisplayedPeriod.Total == 0 ? "0" : (amount * 100 / DisplayedPeriod.Total).ToString("0", MetricsHistory.Culture)) + " %";
    private static string DayAmount(MetricsDay day) => day.Total > 0 || day.Covered ? MetricsHistory.Number(day.Total) + " tokens" : "Sin cobertura";
    private static string DayName(DateOnly date, MetricsDay? day) => $"{date.ToString("dddd, d MMMM yyyy", MetricsHistory.Culture)} · {(day is null ? "No disponible" : DayAmount(day))}";
    private C.TextBlock Label(string text, double size, bool bold = false, M.Color? color = null) => new()
    { Text = text, FontSize = size, FontWeight = bold ? W.FontWeights.SemiBold : W.FontWeights.Normal, Foreground = UiTheme.Brush(color ?? _theme.Text),
        TextTrimming = W.TextTrimming.CharacterEllipsis, VerticalAlignment = W.VerticalAlignment.Center };
    private C.TextBlock Right(string text, double size, bool bold = false, M.Color? color = null)
    {
        var label = Label(text, size, bold, color);
        label.HorizontalAlignment = W.HorizontalAlignment.Right;
        return label;
    }
    private C.TextBlock Chevron(string glyph, double size = 11, M.Color? color = null)
    {
        var label = Label(glyph, size, color: color ?? _theme.Muted);
        label.FontFamily = new M.FontFamily("Segoe Fluent Icons");
        label.HorizontalAlignment = W.HorizontalAlignment.Right;
        return label;
    }
    private M.Brush ProviderBrush(string provider) => UiTheme.Brush(ProviderIcons.Color(provider, _theme));
    private W.FrameworkElement Dot(string provider, double diameter = 7, double gap = 9) => new S.Ellipse
    { Height = diameter, Width = diameter, Fill = ProviderBrush(provider), Margin = new(1, 0, gap, 0), VerticalAlignment = W.VerticalAlignment.Center };
    private C.Border Rule() => new() { Height = 1, Background = UiTheme.Brush(_theme.Text, 28) };
    private void Add(W.FrameworkElement item, double top, double bottom = 0, double left = 0)
    { item.Margin = new(left, top, 0, bottom); _content.Children.Add(item); }
    private static C.Grid Columns(params W.GridLength[] widths)
    {
        var grid = new C.Grid();
        foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = width });
        return grid;
    }
    private static void Put(C.Grid grid, W.UIElement item, int col) { C.Grid.SetColumn(item, col); grid.Children.Add(item); }
    private static C.Grid Pair(W.UIElement left, W.UIElement? right, double minHeight)
    {
        var row = Columns(new(1, W.GridUnitType.Star), W.GridLength.Auto);
        row.MinHeight = minHeight;
        Put(row, left, 0);
        if (right is not null) Put(row, right, 1);
        return row;
    }
    private C.Button ActionButton(object content, string key, string name, Action action, double minHeight)
    {
        var button = new C.Button { Content = content, MinHeight = minHeight, Padding = new(0), Tag = key,
            HorizontalContentAlignment = W.HorizontalAlignment.Stretch, VerticalContentAlignment = W.VerticalAlignment.Center, Background = M.Brushes.Transparent };
        W.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { if (button.IsEnabled) action(); };
        _buttons.Add(key, button);
        return button;
    }
    private static C.Button Tab(string text) => new() { Content = text, FontSize = 11, MinHeight = 24, Padding = new(3, 3, 3, 3) };
    private void StyleTab(C.Button tab, bool selected)
    {
        tab.FontWeight = selected ? W.FontWeights.SemiBold : W.FontWeights.Normal;
        tab.Foreground = UiTheme.Brush(selected ? _theme.Text : _theme.Muted);
        tab.Background = selected ? UiTheme.Brush(_theme.Accent, _theme.HighContrast ? (byte)64 : (byte)53) : M.Brushes.Transparent;
        W.Automation.AutomationProperties.SetHelpText(tab, selected ? "Vista seleccionada" : "Cambiar vista");
    }
}
