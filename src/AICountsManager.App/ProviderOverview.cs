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
    private readonly Dictionary<string, bool> _modelExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, C.Button> _buttons = new(StringComparer.Ordinal);
    private C.Primitives.Popup? _picker;
    private TokenHistorySnapshot _snapshot = TokenHistorySnapshot.Empty;
    private MetricsHistory? _history;
    private DateOnly? _lastRenderDate, _weekStart, _monthStart, _selectedWeekDay, _selectedMonthDay, _expandedWeekModels;
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
        Padding = new(12, 8, 12, 8);
        Margin = new(0, 0, 0, 10);
        Child = _root;
        System.Windows.Documents.Typography.SetNumeralAlignment(_content, W.FontNumeralAlignment.Tabular);
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
            if (_selectedWeekDay is { } selected && MetricsPeriods.Monday(selected) != week) _selectedWeekDay = _expandedWeekModels = null;
        }
        Redraw();
    }

    private void Redraw() { _dirty = true; RenderPending(); }

    public void RenderPending()
    {
        var today = _todayDate();
        if (!_dirty && _lastRenderDate == today) return;
        _dirty = false;
        if (_lastRenderDate != today) _history = null;
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

        if (SelectedView is UsageView.Week or UsageView.Quotas) Add(PeriodNavigation(start, end, today), 4);
        switch (SelectedView)
        {
            case UsageView.Today: RenderToday(today); break;
            case UsageView.Week: RenderWeek(start, today); break;
            case UsageView.Quotas: RenderQuotas(start, today); break;
            case UsageView.Calendar: RenderMonth(start, end, today); break;
        }
        if (focus is not null && _buttons.TryGetValue(focus, out var target) && target.IsEnabled) target.Focus();
    }

    private static DateOnly? RetainedAnchor(DateOnly? selected, DateOnly min, DateOnly current) =>
        selected is { } date && date < current ? date < min ? min : date : null;

    private void RenderToday(DateOnly today)
    {
        Add(Label(Cap(today.ToString("dddd, d 'de' MMMM", MetricsHistory.Culture)), 10.5, color: _theme.Muted), 7);
        Add(TokenTotal(), 2);

        if (Series.Count == 0)
        {
            Add(Label(DisplayedPeriod.HasKnownTotal ? "Sin actividad hoy" : "Sin historial disponible", 10, color: _theme.Muted), 10);
            return;
        }
        var bar = new C.Grid { Height = 4 };
        for (var i = 0; i < Series.Count; i++)
        {
            var item = Series[i];
            if (i > 0) bar.ColumnDefinitions.Add(new() { Width = new W.GridLength(2) });
            bar.ColumnDefinitions.Add(new() { Width = new W.GridLength((double)item.Tokens, W.GridUnitType.Star) });
            Put(bar, new C.Border { Background = ProviderBrush(item.Provider), CornerRadius = new(2) }, i * 2);
        }
        Add(bar, 6);
        var onlyProvider = Series.Count == 1;
        for (var i = 0; i < Series.Count; i++)
        {
            var item = Series[i];
            if (i != 0) Add(Rule(), 0);
            var expanded = _modelExpansion.GetValueOrDefault(item.Provider, onlyProvider);
            var row = Columns(W.GridLength.Auto, new(1, W.GridUnitType.Star), W.GridLength.Auto, new(45), new(17));
            Put(row, Dot(item.Provider), 0);
            Put(row, Label(ProviderIds.Name(item.Provider), 11, true), 1);
            Put(row, Right(MetricsHistory.Number(item.Tokens), 11, true), 2);
            Put(row, Right(Percent(item.Tokens), 10, color: _theme.Muted), 3);
            var arrow = Chevron("");
            Put(row, arrow, 4);
            var details = new C.Border();
            var button = ActionButton(row, "provider-" + item.Provider, ProviderIds.Name(item.Provider), null, 23);
            button.Click += (_, _) =>
            {
                expanded = !expanded;
                _modelExpansion[item.Provider] = expanded;
                UpdateModels();
            };
            Add(button, i == 0 ? 2 : 0);
            Add(details, 0, 4, 4);
            void UpdateModels()
            {
                arrow.Text = expanded ? "\uE70E" : "\uE70D";
                details.Child = expanded ? ModelList(today, item.Provider) : null;
                details.Visibility = details.Child is null ? W.Visibility.Collapsed : W.Visibility.Visible;
                W.Automation.AutomationProperties.SetName(button,
                    $"{ProviderIds.Name(item.Provider)} · {MetricsHistory.Number(item.Tokens)} tokens · {Percent(item.Tokens)} · {(expanded ? "Ocultar" : "Mostrar")} modelos");
            }
            UpdateModels();
        }
    }

    private C.Border? ModelList(DateOnly date, string provider)
    {
        var models = _history!.Models(date, provider);
        if (models.Count == 0) return null;
        var rows = new C.StackPanel();
        foreach (var model in models)
        {
            var count = Right(MetricsHistory.Number(model.Tokens), 10);
            count.Margin = new(8, 0, 0, 0);
            rows.Children.Add(Pair(Label(model.Name, 10, color: _theme.Muted), count, 16));
        }
        return new C.Border { Child = rows, BorderBrush = ProviderBrush(provider),
            BorderThickness = new(2, 0, 0, 0), Padding = new(8, 0, 0, 0) };
    }

    private void RenderWeek(DateOnly start, DateOnly today)
    {
        Add(Pair(TokenTotal(), CurrentPeriodButton(start != MetricsPeriods.Monday(today), today, "Esta semana"), 23), 2);
        var days = DisplayedPeriod.Days.ToDictionary(d => d.Date);
        var providerOrder = Series.Select(s => s.Provider).ToArray();
        var chart = new C.Primitives.UniformGrid { Columns = 7, Rows = 1 };
        var max = Math.Max(1, days.Values.Select(d => d.Total).DefaultIfEmpty(0).Max());
        var dateLabel = Label("", 10, color: _theme.Muted);
        var tokenLabel = Right("", 10, true);
        tokenLabel.Margin = new(4, 0, 0, 0);
        var choices = new Dictionary<DateOnly, (C.Button Button, C.TextBlock Caption)>();
        var key = Series.Count > 0 ? ProviderKey() : null;
        var dayRow = Pair(dateLabel, tokenLabel, 17);
        var arrow = Chevron("\uE70D", 9);
        var caption = Pair(dayRow, arrow, 17);
        dayRow.Margin = new(0, 0, 7, 0);
        var modelDetails = new C.StackPanel();
        var dayButton = ActionButton(caption, "week-models", "Modelos del día", null, 17);
        dayButton.Click += (_, _) =>
        {
            _expandedWeekModels = _expandedWeekModels == _selectedWeekDay ? null : _selectedWeekDay;
            UpdateDetail();
        };
        void UpdateDetail()
        {
            var selected = _selectedWeekDay is { } date ? days.GetValueOrDefault(date) : null;
            if (key is not null) key.Visibility = selected is null ? W.Visibility.Visible : W.Visibility.Collapsed;
            dayButton.Visibility = selected is null ? W.Visibility.Collapsed : W.Visibility.Visible;
            modelDetails.Children.Clear();
            dateLabel.Text = selected is null ? "" : Cap(selected.Date.ToString("dddd d", MetricsHistory.Culture));
            tokenLabel.Text = selected is null ? "" : DayAmount(selected);
            if (selected is null) return;
            var expanded = selected.Date == _expandedWeekModels;
            dayButton.IsEnabled = selected.Total > 0;
            arrow.Visibility = dayButton.IsEnabled ? W.Visibility.Visible : W.Visibility.Collapsed;
            arrow.Text = expanded ? "\uE70E" : "\uE70D";
            W.Automation.AutomationProperties.SetName(dayButton, DayName(selected.Date, selected) +
                (dayButton.IsEnabled ? expanded ? " · Ocultar modelos" : " · Mostrar modelos" : ""));
            if (!expanded) return;
            foreach (var series in selected.Series)
            {
                if (ModelList(selected.Date, series.Provider) is not { } models) continue;
                var heading = Label(ProviderIds.Name(series.Provider), 10, true);
                heading.Margin = new(0, 3, 0, 1);
                modelDetails.Children.Add(heading);
                modelDetails.Children.Add(models);
            }
        }
        void SelectDay(DateOnly date)
        {
            _expandedWeekModels = null;
            var previous = _selectedWeekDay;
            _selectedWeekDay = previous == date ? null : date;
            if (previous is { } old && choices.TryGetValue(old, out var deselected))
                StyleWeekDay(deselected.Button, deselected.Caption, false);
            if (_selectedWeekDay is { } selected)
            {
                var choice = choices[selected];
                StyleWeekDay(choice.Button, choice.Caption, true);
            }
            UpdateDetail();
        }
        for (var i = 0; i < 7; i++)
        {
            var date = start.AddDays(i);
            var day = days.GetValueOrDefault(date);
            var selected = date == _selectedWeekDay;
            var bar = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Bottom, Width = 27 };
            foreach (var provider in providerOrder)
                if (day?.Series.FirstOrDefault(s => s.Provider == provider) is { } series)
                    bar.Children.Add(new C.Border { Height = 35 * (double)(series.Tokens / max), Background = ProviderBrush(provider) });
            if (day is not null && day.Total == 0)
                bar.Children.Add(new C.Border { Height = day.Covered ? 2 : 1, Background = UiTheme.Brush(_theme.Muted, day.Covered ? (byte)85 : (byte)38) });
            var cell = new C.StackPanel();
            var band = new C.Grid { Height = 37 };
            band.Children.Add(bar);
            cell.Children.Add(band);
            cell.Children.Add(Rule());
            var label = Label(Weekdays[i], 10);
            label.HorizontalAlignment = W.HorizontalAlignment.Center;
            label.Margin = new(0, 4, 0, 0);
            cell.Children.Add(label);
            var button = ActionButton(cell, "week-" + date.DayNumber, DayName(date, day),
                () => SelectDay(date), 56);
            button.Margin = new(1, 0, 1, 0);
            button.IsEnabled = day is not null;
            button.BorderThickness = new(1);
            StyleWeekDay(button, label, selected);
            choices.Add(date, (button, label));
            chart.Children.Add(button);
        }
        Add(chart, 2);
        UpdateDetail();
        var footer = new C.Grid { MinHeight = 17 };
        if (key is not null) footer.Children.Add(key);
        footer.Children.Add(dayButton);
        Add(footer, 4);
        Add(modelDetails, 0);
    }

    private void StyleWeekDay(C.Button button, C.TextBlock caption, bool selected)
    {
        button.BorderBrush = selected ? UiTheme.Brush(_theme.Accent, 118) : M.Brushes.Transparent;
        button.Background = selected ? UiTheme.Brush(_theme.Accent, 22) : M.Brushes.Transparent;
        caption.Foreground = UiTheme.Brush(selected ? _theme.Text : _theme.Muted);
        caption.FontWeight = selected ? W.FontWeights.SemiBold : W.FontWeights.Normal;
    }

    private void RenderQuotas(DateOnly start, DateOnly today)
    {
        var body = Columns(new(112), new(1, W.GridUnitType.Star));
        Put(body, Ring(), 0);
        var legend = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Center, Margin = new(6, 0, 0, 0) };
        legend.Children.Add(Label("Reparto de tokens", 10, color: _theme.Muted));
        foreach (var item in Series)
        {
            var line = Columns(W.GridLength.Auto, new(1, W.GridUnitType.Star), W.GridLength.Auto);
            Put(line, Dot(item.Provider), 0);
            var provider = new C.StackPanel();
            provider.Children.Add(Label(ProviderIds.Name(item.Provider), 11, true));
            provider.Children.Add(Label(MetricsHistory.Number(item.Tokens) + " tokens", 10, color: _theme.Muted));
            Put(line, provider, 1);
            var percent = Right(Percent(item.Tokens), 11, true);
            percent.VerticalAlignment = W.VerticalAlignment.Top;
            Put(line, percent, 2);
            line.Margin = new(0, 1, 0, 2);
            legend.Children.Add(line);
        }
        if (Series.Count == 0) legend.Children.Add(Label(DisplayedPeriod.HasKnownTotal ? "Sin actividad esta semana" : "Sin historial disponible", 10, color: _theme.Muted));
        Put(body, legend, 1);
        Add(body, 6);
        if (CurrentPeriodButton(start != MetricsPeriods.Monday(today), today, "Esta semana") is { } current)
        {
            current.HorizontalAlignment = W.HorizontalAlignment.Right;
            Add(current, 1);
        }
    }

    private C.Grid Ring()
    {
        const double diameter = 104, stroke = 11, center = diameter / 2, radius = center - stroke / 2;
        var ring = new C.Grid { Width = diameter, Height = diameter, VerticalAlignment = W.VerticalAlignment.Center };
        ring.Children.Add(new S.Ellipse { StrokeThickness = stroke, Stroke = UiTheme.Brush(_theme.Text, 25) });
        var start = -Math.PI / 2;
        foreach (var item in Series)
        {
            var brush = ProviderBrush(item.Provider);
            var fraction = (double)(item.Tokens / DisplayedPeriod.Total);
            var end = start + fraction * Math.Tau;
            if (Series.Count == 1)
                ring.Children.Add(new S.Ellipse { StrokeThickness = stroke, Stroke = brush });
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
        var (amount, unit) = DisplayedPeriod.HasKnownTotal ? RingAmount(DisplayedPeriod.Total) : ("—", "tokens");
        var label = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Center, HorizontalAlignment = W.HorizontalAlignment.Center };
        var text = Label(amount, 17, true);
        text.TextAlignment = W.TextAlignment.Center;
        var suffix = Label(unit, 10, color: _theme.Muted);
        suffix.TextAlignment = W.TextAlignment.Center;
        label.Children.Add(text);
        label.Children.Add(suffix);
        ring.Children.Add(new C.Viewbox { Child = label, Width = 72, Height = 42, StretchDirection = C.StretchDirection.DownOnly });
        W.Automation.AutomationProperties.SetName(ring, DisplayedPeriod.HasKnownTotal ? TotalText + " tokens" : "Sin historial disponible");
        return ring;
    }

    internal static (string Amount, string Unit) RingAmount(decimal total)
    {
        if (total < 1_000) return (MetricsHistory.Number(total), "tokens");
        var divisor = total < 999_500 ? 1_000m : 1_000_000m;
        var scaled = total / divisor;
        var rounded = Math.Round(scaled, scaled < 10 ? 1 : 0, MidpointRounding.AwayFromZero);
        return (rounded.ToString("#,0.#", MetricsHistory.Culture),
            divisor == 1_000 ? "mil" : rounded == 1 ? "millón" : "millones");
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

    private C.StackPanel TokenTotal()
    {
        var number = new C.StackPanel { Orientation = C.Orientation.Horizontal };
        number.Children.Add(Label(TotalText, 17, true));
        var units = Label("tokens", 10, color: _theme.Muted);
        units.VerticalAlignment = W.VerticalAlignment.Bottom;
        units.Margin = new(5, 0, 0, 2);
        number.Children.Add(units);
        return number;
    }

    private string TotalText => DisplayedPeriod.HasKnownTotal ? MetricsHistory.Number(DisplayedPeriod.Total) : "—";
    private static string Cap(string text) => char.ToUpper(text[0], MetricsHistory.Culture) + text[1..];
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
    private C.Button ActionButton(object content, string key, string name, Action? action, double minHeight)
    {
        var button = new C.Button { Content = content, MinHeight = minHeight, Padding = new(0), Tag = key,
            HorizontalContentAlignment = W.HorizontalAlignment.Stretch, VerticalContentAlignment = W.VerticalAlignment.Center, Background = M.Brushes.Transparent };
        W.Automation.AutomationProperties.SetName(button, name);
        if (action is not null) button.Click += (_, _) => { if (button.IsEnabled) action(); };
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
