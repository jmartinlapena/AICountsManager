using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace AICountsManager;

internal sealed partial class ProviderOverview
{
    private void RenderNavigation(DateOnly start, DateOnly end, DateOnly today)
    {
        var monthly = SelectedView == UsageView.Calendar;
        var cutoff = MetricsPeriods.Cutoff(today);
        var previousDate = monthly ? start.AddMonths(-1) : start.AddDays(-7);
        var nextDate = monthly ? start.AddMonths(1) : start.AddDays(7);
        var periodName = monthly ? Cap(start.ToString("MMMM yyyy", MetricsHistory.Culture)) : WeekRange(start, end);
        var row = Columns(new(28), new(1, W.GridUnitType.Star), new(28));
        var previous = ActionButton(CenteredIcon("\uE76B"), "previous", monthly ? "Mes anterior" : "Semana anterior", () => ChoosePeriod(previousDate), 26);
        previous.IsEnabled = start.AddDays(-1) >= cutoff;
        var next = ActionButton(CenteredIcon("\uE76C"), "next", monthly ? "Mes siguiente" : "Semana siguiente", () => ChoosePeriod(nextDate), 26);
        next.IsEnabled = nextDate <= today;
        var title = new C.StackPanel { Orientation = C.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Center };
        title.Children.Add(Chevron("\uE787", 11));
        var caption = Label(periodName, 11.5, true);
        caption.Margin = new(8, 0, 10, 0);
        title.Children.Add(caption);
        title.Children.Add(Chevron("\uE70D", 9));
        var picker = ActionButton(title, "picker", (monthly ? "Elegir mes · " : "Elegir semana · ") + periodName,
            () => OpenPicker(_buttons["picker"], start, cutoff, today, monthly), 26);
        Put(row, previous, 0); Put(row, picker, 1); Put(row, next, 2);
        Add(new C.Border { Child = row, Background = UiTheme.Brush(_theme.Text, 13), CornerRadius = new(7) }, 10);
    }

    private void OpenPicker(C.Button anchor, DateOnly start, DateOnly cutoff, DateOnly today, bool monthly)
    {
        if (PickerOpen) { ClosePicker(); return; }
        const double width = 250;
        var content = new C.StackPanel();
        var frame = new C.Border
        {
            Width = width, Child = content, Padding = new(8), UseLayoutRounding = true,
            Style = (W.Style?)anchor.TryFindResource("OverlaySurface"),
        };
        _theme.Apply(frame.Resources, false);
        if (anchor.TryFindResource(typeof(C.Button)) is W.Style style) frame.Resources[typeof(C.Button)] = style;
        System.Windows.Documents.TextElement.SetFontFamily(frame, anchor.FontFamily);
        I.KeyboardNavigation.SetTabNavigation(content, I.KeyboardNavigationMode.Cycle);
        I.KeyboardNavigation.SetDirectionalNavigation(content, I.KeyboardNavigationMode.Contained);
        W.Automation.AutomationProperties.SetName(frame, monthly ? "Selector de mes" : "Selector de semana");
        var popup = new C.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = C.Primitives.PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, HorizontalOffset = (anchor.ActualWidth - width) / 2, VerticalOffset = 4, Child = frame,
        };
        _picker = popup;
        popup.Closed += (_, _) => { if (ReferenceEquals(_picker, popup)) _picker = null; };
        void Apply(DateOnly date)
        {
            popup.IsOpen = false;
            ChoosePeriod(date);
            if (_buttons.TryGetValue("picker", out var button)) button.Focus();
        }

        DateOnly Page(DateOnly date) => monthly ? new(date.Year, 1, 1) : MetricsPeriods.Month(date);
        var firstPage = Page(cutoff);
        var lastPage = Page(today);
        var selectedDate = !monthly && _selectedWeekDay is { } day && day >= start && day < start.AddDays(7) ? day : start;
        var page = Page(selectedDate < cutoff ? cutoff : selectedDate);
        C.Button? RenderPage(string? focusArrow = null)
        {
            content.Children.Clear();
            var header = Columns(new(28), new(1, W.GridUnitType.Star), new(28));
            C.Button Arrow(int direction, string id, string title)
            {
                var button = PickerButton(CenteredIcon(direction < 0 ? "\uE76B" : "\uE76C"), id, title,
                    () =>
                    {
                        page = monthly ? page.AddYears(direction) : page.AddMonths(direction);
                        RenderPage(id)?.Focus();
                    }, 27);
                button.IsEnabled = direction < 0 ? page > firstPage : page < lastPage;
                return button;
            }
            var previous = Arrow(-1, "picker-previous", monthly ? "Año anterior" : "Mes anterior");
            var next = Arrow(1, "picker-next", monthly ? "Año siguiente" : "Mes siguiente");
            var title = new C.StackPanel { Orientation = C.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Center };
            title.Children.Add(Chevron("\uE787", 11, _theme.Accent));
            var periodCaption = Label(Cap(page.ToString(monthly ? "yyyy" : "MMMM yyyy", MetricsHistory.Culture)), 12, true);
            periodCaption.Margin = new(6, 0, 0, 0);
            title.Children.Add(periodCaption);
            Put(header, previous, 0); Put(header, title, 1); Put(header, next, 2);
            content.Children.Add(new C.Border
            {
                Child = header, CornerRadius = new(8), Margin = new(0, 0, 0, 8),
                Background = UiTheme.Brush(_theme.Text, 12),
            });

            if (!monthly) content.Children.Add(CalendarWeekdays());
            var cells = new C.Primitives.UniformGrid { Columns = monthly ? 4 : 7, Margin = new(0, 3, 0, 0) };
            content.Children.Add(cells);
            var dates = monthly ? Enumerable.Range(0, 12).Select(page.AddMonths) :
                MetricsPeriods.Days(MetricsPeriods.Monday(page), MetricsPeriods.Monday(page.AddMonths(1).AddDays(-1)).AddDays(6));
            C.Button? preferred = null, firstEnabled = null;
            foreach (var date in dates)
            {
                var enabled = date >= (monthly ? MetricsPeriods.Month(cutoff) : cutoff) && date <= today;
                var selected = enabled && (monthly ? date == start : date >= start && date < start.AddDays(7));
                var current = monthly ? date == MetricsPeriods.Month(today) : date == today;
                var caption = monthly ? Cap(date.ToString("MMM", MetricsHistory.Culture).TrimEnd('.')) : date.Day.ToString(MetricsHistory.Culture);
                var name = date.ToString(monthly ? "MMMM yyyy" : "dddd, d 'de' MMMM 'de' yyyy", MetricsHistory.Culture);
                var button = PickerButton(caption, "picker-date-" + date.DayNumber, name, () => Apply(date), monthly ? 33 : 26);
                button.IsEnabled = enabled;
                button.Foreground = UiTheme.Brush(monthly || date.Month == page.Month ? _theme.Text : _theme.Muted);
                button.FontWeight = selected || current ? W.FontWeights.SemiBold : W.FontWeights.Normal;
                if (selected) button.Background = UiTheme.Brush(_theme.Accent, 55);
                else if (enabled && (monthly || date.Month == page.Month)) button.Background = UiTheme.Brush(_theme.Text, 12);
                if (current) { button.BorderBrush = UiTheme.Brush(_theme.Text); button.BorderThickness = new(1); }
                cells.Children.Add(button);
                if (!button.IsEnabled) continue;
                firstEnabled ??= button;
                if (date == selectedDate || (preferred is null && current)) preferred = button;
            }
            var arrow = focusArrow == "picker-previous" ? previous : next;
            return focusArrow is not null && arrow.IsEnabled ? arrow : preferred ?? firstEnabled;
        }
        frame.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != I.Key.Escape) return;
            popup.IsOpen = false; anchor.Focus(); e.Handled = true;
        };
        var initialFocus = RenderPage();
        popup.IsOpen = true;
        initialFocus?.Focus();
    }

    private static C.Button PickerButton(object content, string key, string name, Action action, double height)
    {
        var button = new C.Button
        {
            Content = content, Tag = key, FontSize = 11.5,
            MinHeight = 0, Height = height, Padding = new(0), Margin = new(1),
        };
        W.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { if (button.IsEnabled) action(); };
        return button;
    }

    private static string WeekRange(DateOnly start, DateOnly end)
    {
        if (start.Year != end.Year) return $"{start.ToString("d MMM yyyy", MetricsHistory.Culture)} – {end.ToString("d MMM yyyy", MetricsHistory.Culture)}";
        var left = start.Month == end.Month ? start.Day.ToString(MetricsHistory.Culture) : start.ToString("d MMM", MetricsHistory.Culture);
        return left + " – " + end.ToString("d MMM yyyy", MetricsHistory.Culture);
    }

    private C.TextBlock CenteredIcon(string glyph)
    {
        var icon = Chevron(glyph);
        icon.HorizontalAlignment = W.HorizontalAlignment.Center;
        return icon;
    }

    private C.Button? CurrentPeriodButton(bool visible, DateOnly today, string label)
    {
        if (!visible) return null;
        return ActionButton(Label(label + "  ›", 10.5, true, _theme.Accent), "current", label, () => ChoosePeriod(today), 16);
    }

    private void RenderMonth(DateOnly start, DateOnly end, DateOnly today)
    {
        Add(LargeTotal(), 11);
        Add(Label(start == MetricsPeriods.Month(today) ? "Tokens registrados este mes" : "Tokens registrados en el mes", 10.5, color: _theme.Muted), 2);
        var days = DisplayedPeriod.Days.ToDictionary(day => day.Date);
        Add(CalendarWeekdays(), 11);

        var cells = new C.Primitives.UniformGrid { Columns = 7, FirstColumn = (start.DayNumber - MetricsPeriods.Monday(start).DayNumber), Margin = new(0) };
        foreach (var date in MetricsPeriods.Days(start, end))
        {
            var day = days.GetValueOrDefault(date);
            var level = MetricsHistory.Heat(day?.Total ?? 0);
            var enabled = day is { Total: > 0 };
            var current = date == today;
            var selected = date == _selectedMonthDay && enabled;
            var foreground = level == 4 ? HeatText() : day is { Total: 0, Covered: false } ? _theme.Muted : _theme.Text;
            var plate = new C.Border
            {
                Height = 20, CornerRadius = new(4), Background = HeatBrush(level),
                BorderThickness = new(selected ? 2 : current ? 1 : 0),
                BorderBrush = selected || current ? UiTheme.Brush(foreground) : null,
                Opacity = day is null ? .39 : 1,
            };
            var number = Label(date.Day.ToString(MetricsHistory.Culture), 10, current || selected, foreground);
            number.HorizontalAlignment = W.HorizontalAlignment.Center;
            plate.Child = number;
            if (enabled)
            {
                var button = ActionButton(plate, "month-" + date.DayNumber, DayName(date, day),
                    () => { _selectedMonthDay = _selectedMonthDay == date ? null : date; Redraw(); }, 20);
                button.Margin = new(2, 1.5, 2, 1.5);
                cells.Children.Add(button);
            }
            else
            {
                plate.Margin = new(2, 1.5, 2, 1.5);
                W.Automation.AutomationProperties.SetName(plate, DayName(date, day));
                cells.Children.Add(plate);
            }
        }
        Add(cells, 5);

        var scale = new C.StackPanel { Orientation = C.Orientation.Horizontal };
        var less = Label("Menos", 9.5, color: _theme.Muted);
        less.Margin = new(0, 0, 4, 0);
        scale.Children.Add(less);
        for (var i = 0; i <= 4; i++)
            scale.Children.Add(new C.Border { Width = 9, Height = 8, CornerRadius = new(2), Margin = new(0, 0, 3, 0), Background = HeatBrush(i) });
        scale.Children.Add(Label("Más", 9.5, color: _theme.Muted));
        var ranking = days.Values.Where(d => d.Total > 0).OrderByDescending(d => d.Total).ThenBy(d => d.Date).Take(3).ToArray();
        var activeDays = days.Values.Count(d => d.Total > 0);
        Add(Pair(scale, Right(DisplayedPeriod.HasKnownTotal ? (activeDays == 1 ? "1 día con actividad" : $"{activeDays} días con actividad") : "Sin cobertura", 10, color: _theme.Muted), 17), 10);
        Add(Rule(), 7);
        if (ranking.FirstOrDefault() is { } first)
            Add(Pair(Label("Día de mayor uso", 10.5, color: _theme.Muted),
                Right($"{first.Date.ToString("d MMM", MetricsHistory.Culture)} · {DayAmount(first)}", 11, true), 20), 7);
        if (ranking.Length > 0)
        {
            var title = ranking.Length switch { 1 => "Día de mayor uso", 2 => "2 días de mayor uso", _ => "3 días de mayor uso" };
            Add(ActionButton(Pair(Label(title, 10.5, true, _theme.Accent), Chevron(_rankingExpanded ? "\uE70E" : "\uE70D", 9), 24),
                "ranking", title + (_rankingExpanded ? " · Ocultar" : " · Mostrar"), () => { _rankingExpanded = !_rankingExpanded; Redraw(); }, 24), 2);
            if (_rankingExpanded)
                foreach (var item in ranking)
                    Add(Pair(Label(Cap(item.Date.ToString("dddd d", MetricsHistory.Culture)), 10, color: _theme.Muted), Right(DayAmount(item), 10), 19), 0, 0, 8);
        }
        if (_selectedMonthDay is { } selectedDate && days.TryGetValue(selectedDate, out var selectedDay) && selectedDay.Total > 0)
        {
            Add(Rule(), 7);
            Add(Pair(Label(Cap(selectedDate.ToString("dddd d 'de' MMMM", MetricsHistory.Culture)), 11, true), Right(DayAmount(selectedDay), 11, true), 20), 6);
            foreach (var item in selectedDay.Series)
            {
                var provider = Columns(W.GridLength.Auto, new(1, W.GridUnitType.Star));
                Put(provider, Dot(item.Provider, 6, 5), 0);
                Put(provider, Label(ProviderIds.Name(item.Provider), 10.5, color: _theme.Muted), 1);
                Add(Pair(provider, Right(MetricsHistory.Number(item.Tokens), 10.5), 19), 0, 0, 4);
            }
        }
    }

    private C.Primitives.UniformGrid CalendarWeekdays()
    {
        var headings = new C.Primitives.UniformGrid { Columns = 7 };
        for (var day = 0; day < 7; day++)
        {
            var name = MetricsHistory.Culture.DateTimeFormat.AbbreviatedDayNames[(day + 1) % 7];
            var label = Label(Cap(name.TrimEnd('.')), 10, true, _theme.Muted);
            label.HorizontalAlignment = W.HorizontalAlignment.Center;
            headings.Children.Add(label);
        }
        return headings;
    }

    private static readonly byte[] HeatOpacity = [11, 38, 76, 129, 255];
    private M.Brush HeatBrush(int level) => level == 0 ? UiTheme.Brush(_theme.Text, HeatOpacity[0])
        : UiTheme.Brush(_theme.Accent, _theme.HighContrast ? (byte)(level == 4 ? 255 : 55 * level) : HeatOpacity[level]);

    private M.Color HeatText()
    {
        if (_theme.HighContrast) return W.SystemColors.HighlightTextColor;
        var color = _theme.Accent;
        return .2126 * color.R + .7152 * color.G + .0722 * color.B >= 140 ? M.Color.FromRgb(23, 35, 38) : M.Colors.White;
    }
}
