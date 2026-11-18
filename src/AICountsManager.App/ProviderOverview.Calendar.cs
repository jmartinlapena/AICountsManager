using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace AICountsManager;

internal sealed partial class ProviderOverview
{
    private C.Border PeriodNavigation(DateOnly start, DateOnly end, DateOnly today)
    {
        var monthly = SelectedView == UsageView.Calendar;
        var cutoff = MetricsPeriods.Cutoff(today);
        var previousDate = monthly ? start.AddMonths(-1) : start.AddDays(-7);
        var nextDate = monthly ? start.AddMonths(1) : start.AddDays(7);
        var periodName = monthly ? Cap(start.ToString("MMMM yyyy", MetricsHistory.Culture)) : WeekRange(start, end);
        var arrowWidth = monthly ? 17 : 26;
        var row = Columns(new(arrowWidth), new(1, W.GridUnitType.Star), new(arrowWidth));
        var previous = ActionButton(CenteredIcon("\uE76B"), "previous", monthly ? "Mes anterior" : "Semana anterior", () => ChoosePeriod(previousDate), 23);
        previous.IsEnabled = start.AddDays(-1) >= cutoff;
        var next = ActionButton(CenteredIcon("\uE76C"), "next", monthly ? "Mes siguiente" : "Semana siguiente", () => ChoosePeriod(nextDate), 23);
        next.IsEnabled = nextDate <= today;
        var title = new C.StackPanel { Orientation = C.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Center };
        if (!monthly) title.Children.Add(Chevron("\uE787", 10));
        var caption = Label(monthly ? Cap(start.ToString("MMM yyyy", MetricsHistory.Culture)) : periodName, monthly ? 10 : 11, true);
        caption.Margin = new(monthly ? 0 : 6, 0, monthly ? 3 : 6, 0);
        title.Children.Add(caption);
        title.Children.Add(Chevron("\uE70D", 9));
        var picker = ActionButton(title, "picker", (monthly ? "Elegir mes · " : "Elegir semana · ") + periodName,
            () => OpenPicker(_buttons["picker"], start, cutoff, today, monthly), 23);
        Put(row, previous, 0); Put(row, picker, 1); Put(row, next, 2);
        return new C.Border { Child = row, Background = UiTheme.Brush(_theme.Text, 13), CornerRadius = new(7) };
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
        var dateFocusCue = anchor.TryFindResource("CalendarFocusCue") as W.Style;
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
                if (dateFocusCue is not null) button.FocusVisualStyle = dateFocusCue;
                if (current) button.Content = new C.TextBlock { Text = caption, TextDecorations = W.TextDecorations.Underline };
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
        var days = DisplayedPeriod.Days.ToDictionary(day => day.Date);
        var layout = Columns(new(7, W.GridUnitType.Star), new(10), new(5, W.GridUnitType.Star));
        var calendar = new C.StackPanel();
        Put(layout, calendar, 0);
        calendar.Children.Add(CalendarWeekdays());
        var summary = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Center };
        Put(layout, summary, 2);
        var navigation = PeriodNavigation(start, end, today);
        navigation.Margin = new(0, 0, 0, 4);
        summary.Children.Add(navigation);

        var palette = CalendarPalette();
        var cells = new C.Primitives.UniformGrid { Columns = 7, FirstColumn = (start.DayNumber - MetricsPeriods.Monday(start).DayNumber), Margin = new(0) };
        foreach (var date in MetricsPeriods.Days(start, end))
        {
            var day = days.GetValueOrDefault(date);
            var level = _history!.Heat(day?.Total ?? 0);
            var enabled = day is { Total: > 0 };
            var current = date == today;
            var selected = date == _selectedMonthDay && enabled;
            var number = new C.TextBlock { Text = date.Day.ToString(MetricsHistory.Culture),
                TextAlignment = W.TextAlignment.Center };
            if (current) number.TextDecorations = W.TextDecorations.Underline;
            var name = DayName(date, day) + (current ? " · Hoy" : "") + (selected ? " · Seleccionado" : "");
            var button = enabled
                ? ActionButton(number, "month-" + date.DayNumber, name,
                    () => { _selectedMonthDay = _selectedMonthDay == date ? null : date; Redraw(); }, 0)
                : new C.Button { Content = number, IsEnabled = false };
            button.SetResourceReference(C.Control.StyleProperty, "CalendarDayButton");
            W.Automation.AutomationProperties.SetName(button, name);
            button.Background = palette[level].Fill;
            button.Foreground = palette[level].Ink;
            C.Primitives.Selector.SetIsSelected(button, selected);
            button.FontSize = 10;
            button.FontWeight = current || selected ? W.FontWeights.SemiBold : W.FontWeights.Normal;
            if (day is null && !_theme.HighContrast) button.Opacity = .52;
            cells.Children.Add(button);
        }
        calendar.Children.Add(cells);
        var amount = Label(TotalText, 16, true);
        summary.Children.Add(new C.Viewbox { Child = amount, Height = 25, StretchDirection = C.StretchDirection.DownOnly,
            HorizontalAlignment = W.HorizontalAlignment.Left, MaxWidth = 120 });
        summary.Children.Add(Label("tokens del mes", 10, color: _theme.Muted));

        var scale = new C.StackPanel { Orientation = C.Orientation.Horizontal };
        var less = Label("Menos", 9.5, color: _theme.Muted);
        less.Margin = new(0, 0, 4, 0);
        scale.Children.Add(less);
        foreach (var tone in palette)
            scale.Children.Add(new C.Border { Width = 8, Height = 8, CornerRadius = new(2), Margin = new(0, 0, 2, 0), Background = tone.Fill });
        scale.Children.Add(Label("Más", 9.5, color: _theme.Muted));
        summary.Children.Add(new C.Viewbox { Child = scale, Height = 14, Margin = new(0, 6, 0, 2),
            HorizontalAlignment = W.HorizontalAlignment.Left, StretchDirection = C.StretchDirection.DownOnly });
        Add(layout, 5);
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

    // Opaque shades keep their spacing even over an accented acrylic backdrop.
    private (M.Brush Fill, M.Brush Ink)[] CalendarPalette()
    {
        var accent = _theme.HighContrast ? W.SystemColors.HighlightColor : _theme.SystemAccent ?? _theme.Accent;
        var min = Math.Min(accent.R, Math.Min(accent.G, accent.B));
        var span = Math.Max(accent.R, Math.Max(accent.G, accent.B)) - min;
        byte Channel(byte value) => (byte)(44 + (value - min) * 152 / span);
        var hue = span < 8 ? accent : M.Color.FromRgb(Channel(accent.R), Channel(accent.G), Channel(accent.B));

        var luminance = Luminance(hue);
        var dark = _theme.HighContrast ? Luminance(W.SystemColors.WindowColor) < .18 : _theme.Dark;
        double[] stops = dark ? [.052, .14, .28, .49, .82] : [.82, .49, .28, .14, .052];
        var palette = new (M.Brush Fill, M.Brush Ink)[stops.Length + 1];
        palette[0] = (UiTheme.Brush(_theme.Text, 14), UiTheme.Brush(_theme.HighContrast ? _theme.Text : _theme.Muted));
        for (var i = 0; i < stops.Length; i++)
        {
            var target = stops[i];
            var end = target < luminance ? 0 : 1;
            var mix = (target - luminance) / (end - luminance);
            float Blend(float channel) => (float)(channel + (end - channel) * mix);
            var fill = M.Color.FromScRgb(1, Blend(hue.ScR), Blend(hue.ScG), Blend(hue.ScB));
            var ink = target > .179 ? M.Colors.Black : M.Colors.White;
            palette[i + 1] = (UiTheme.Brush(fill), UiTheme.Brush(ink));
        }
        return palette;
    }

    private static double Luminance(M.Color color) => .2126 * color.ScR + .7152 * color.ScG + .0722 * color.ScB;
}
