using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace AicountsManager;

// Small native controls, no chart library, animation, network or timer of its own.
internal sealed class HistoryPanel : C.Border
{
    private UiTheme _theme;
    private TokenHistorySnapshot _snapshot = TokenHistorySnapshot.Empty;
    private readonly C.StackPanel _stack = new(), _details = new();
    private readonly C.Grid _chart = new();
    private readonly C.TextBlock _total = Text(25, true), _period = Text(10), _status = Text(9.5);
    private readonly C.Button _previous = Button("‹"), _next = Button("›"), _models = Button("Modelos ⌄");
    private int _offset;
    private bool _daily = true, _expanded;
    private DateOnly? _selectedDay;
    public HistoryPanel(UiTheme theme)
    {
        _theme = theme; Margin = new W.Thickness(0, 7, 0, 0);
        Child = _stack;
        _previous.Click += (_, _) => { _offset--; _selectedDay = null; Render(); };
        _next.Click += (_, _) => { _offset = Math.Min(0, _offset + 1); _selectedDay = null; Render(); };
        var number = new C.StackPanel { Orientation = C.Orientation.Horizontal, Margin = new W.Thickness(0, 4, 0, 0) };
        number.Children.Add(_total); var unit = Text(10); unit.Text = "tokens"; unit.VerticalAlignment = W.VerticalAlignment.Bottom; unit.Margin = new W.Thickness(6, 0, 0, 5); number.Children.Add(unit); _stack.Children.Add(number);
        var nav = new C.DockPanel(); C.DockPanel.SetDock(_next, C.Dock.Right); C.DockPanel.SetDock(_previous, C.Dock.Right); nav.Children.Add(_next); nav.Children.Add(_previous); _period.VerticalAlignment = W.VerticalAlignment.Center; nav.Children.Add(_period); _stack.Children.Add(nav);
        _chart.Height = 57; _chart.Margin = new W.Thickness(0, 6, 0, 5); _stack.Children.Add(_chart);
        var footer = new C.DockPanel(); C.DockPanel.SetDock(_models, C.Dock.Right); footer.Children.Add(_models); _status.VerticalAlignment = W.VerticalAlignment.Center; footer.Children.Add(_status); _stack.Children.Add(footer);
        _models.Click += (_, _) => { _expanded = !_expanded; Render(); }; _stack.Children.Add(_details);
        W.Automation.AutomationProperties.SetName(_previous, "Periodo anterior");
        W.Automation.AutomationProperties.SetName(_next, "Periodo siguiente");
        Render();
    }
    private static C.TextBlock Text(double size, bool bold = false) => new() { FontSize = size, FontWeight = bold ? W.FontWeights.SemiBold : W.FontWeights.Normal, TextTrimming = W.TextTrimming.CharacterEllipsis };
    private static C.Button Button(string text) => new() { Content = text, FontSize = 10, Padding = new W.Thickness(8, 3, 8, 3), MinHeight = 23 };
    public void ShowPeriod(bool daily) { _daily = daily; _offset = 0; _selectedDay = null; Render(); }
    public void Update(TokenHistorySnapshot snapshot) { _snapshot = snapshot; Render(); }
    public void SetTheme(UiTheme theme) { _theme = theme; Render(); }
    public void ShowReadError() { _snapshot = _snapshot with { Partial = true, Error = "No se pudo leer el historial local." }; Render(); }
    internal static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    private void Render()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var selected = _daily ? today.AddDays(_offset) : Monday(today).AddDays(_offset * 7);
        var start = _daily ? selected.AddDays(-6) : selected;
        var end = _daily ? selected : selected.AddDays(6);
        if (_selectedDay is { } previous && (previous < start || previous > end || previous > today)) _selectedDay = null;
        var from = _selectedDay ?? (_daily ? selected : start);
        var to = _selectedDay ?? end;
        var selectedDays = _snapshot.Days.Where(d => DateOnly.TryParse(d.Date, out var date) && date >= from && date <= to).ToArray();
        var total = selectedDays.Sum(d => (decimal)d.Total);
        _total.Text = _snapshot.UpdatedAt is null ? "—" : _selectedDay.HasValue ? total.ToString("N0") : UsagePresentation.Tokens((long)Math.Min(long.MaxValue, total));
        _total.FontSize = _selectedDay.HasValue && total >= 1_000_000_000_000m ? 18 : 25;
        _period.Text = _selectedDay is { } day ? day.ToString("dddd, dd MMM yyyy") : _daily ? selected == today ? "Hoy · " + selected.ToString("dd MMM") : selected.ToString("dddd, dd MMM") : $"{start:dd MMM} – {end:dd MMM yyyy}";
        _period.Foreground = _status.Foreground = UiTheme.Brush(_theme.Muted);
        _status.Text = _snapshot.Error is not null ? "No se pudo leer el historial" : _snapshot.CatchingUp ? "Indexando historial…" : _snapshot.Partial ? "Historial local · parcial" : "Historial local · 365 días";
        _next.IsEnabled = _offset < 0; _previous.IsEnabled = selected.AddDays(_daily ? -1 : -7) >= today.AddDays(1 - TokenHistory.RetentionDays);
        _chart.Children.Clear(); _chart.ColumnDefinitions.Clear();
        var days = Enumerable.Range(0, 7).Select(i => start.AddDays(i)).ToArray();
        var amounts = days.Select(d => _snapshot.Days.FirstOrDefault(t => t.Date == d.ToString("yyyy-MM-dd"))?.Total ?? 0).ToArray();
        var max = Math.Max(1, amounts.Max());
        for (var i = 0; i < 7; i++)
        {
            _chart.ColumnDefinitions.Add(new C.ColumnDefinition());
            var cell = new C.Grid();
            cell.RowDefinitions.Add(new C.RowDefinition()); cell.RowDefinitions.Add(new C.RowDefinition { Height = new W.GridLength(16) });
            var highlighted = _selectedDay.HasValue ? days[i] == _selectedDay : days[i] == selected || !_daily;
            var bar = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Bottom };
            if (_snapshot.Sources is { } sources)
            {
                foreach (var source in sources.Reverse())
                {
                    var models = source.Value.Days.FirstOrDefault(d => d.Date == days[i].ToString("yyyy-MM-dd"))?.Models;
                    if (models is null) continue;
                    foreach (var model in models.OrderBy(m => m.Key, StringComparer.Ordinal))
                        if (model.Value > 0) bar.Children.Add(new C.Border { Height = 38d * model.Value / max, Background = UiTheme.Brush(ProviderIcons.Color(source.Key, _theme, ModelVariant(model.Key)), highlighted ? (byte)230 : (byte)95) });
                }
            }
            else if (amounts[i] > 0) bar.Children.Add(new C.Border { Height = Math.Max(3, 38d * amounts[i] / max), CornerRadius = new W.CornerRadius(3), Background = UiTheme.Brush(_theme.Accent, highlighted ? (byte)230 : (byte)95) });
            if (bar.Children.Count == 0) bar.Children.Add(new C.Border { Height = 2, Background = UiTheme.Brush(_theme.Muted, 70) });
            var label = Text(9); label.Text = days[i].ToString("ddd"); label.HorizontalAlignment = W.HorizontalAlignment.Center; label.Foreground = UiTheme.Brush(highlighted ? _theme.Text : _theme.Muted); C.Grid.SetRow(label, 1); cell.Children.Add(bar); cell.Children.Add(label);
            var date = days[i];
            var button = new C.Button { Content = cell, Margin = new W.Thickness(3, 0, 3, 0), Padding = new W.Thickness(0),
                MinHeight = 0, Background = M.Brushes.Transparent, BorderThickness = new W.Thickness(0),
                HorizontalContentAlignment = W.HorizontalAlignment.Stretch, VerticalContentAlignment = W.VerticalAlignment.Stretch,
                IsEnabled = date <= today && date >= today.AddDays(1 - TokenHistory.RetentionDays),
                Cursor = System.Windows.Input.Cursors.Hand };
            W.Automation.AutomationProperties.SetName(button, $"{date:dddd dd MMM yyyy}: {amounts[i]:N0} tokens");
            button.Click += (_, _) => { _selectedDay = _selectedDay == date ? null : date; Render(); };
            C.Grid.SetColumn(button, i); _chart.Children.Add(button);
        }
        if (_snapshot.Sources is { } providerSources)
        {
            var unknown = providerSources.Where(s => s.Value.UpdatedAt is null).Select(s => ProviderIds.Name(s.Key)).ToArray();
            if (unknown.Length > 0) _status.Text = string.Join(", ", unknown) + " · sin historial";
            else _status.Text = "Historial de uso" + (_snapshot.Partial ? " · parcial" : "");
        }
        _models.Content = _expanded ? "Modelos ⌃" : "Modelos ⌄";
        _details.Children.Clear(); _details.Visibility = _expanded ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (_expanded)
        {
            if (_snapshot.Sources is { } totals)
                foreach (var source in totals)
                {
                    var count = source.Value.Days.Where(d => DateOnly.TryParse(d.Date, out var date) && date >= from && date <= to).Sum(d => (decimal)d.Total);
                    if (source.Value.UpdatedAt is not null) AddModel(ProviderIds.Name(source.Key) + " · total", count, source.Key);
                }
            if (_snapshot.Sources is { } modelSources)
            {
                foreach (var source in modelSources.Where(s => s.Value.UpdatedAt is not null))
                {
                    var models = source.Value.Days.Where(d => DateOnly.TryParse(d.Date, out var date) && date >= from && date <= to)
                        .SelectMany(d => d.Models).GroupBy(m => m.Key).Select(g => (Name: g.Key, Total: g.Sum(m => (decimal)m.Value))).Where(m => m.Total > 0).OrderByDescending(m => m.Total).ToArray();
                    foreach (var model in models.Take(5)) AddModel(ProviderIds.Name(source.Key) + " · " + model.Name, model.Total, source.Key);
                    if (models.Length > 5) AddModel(ProviderIds.Name(source.Key) + " · otros modelos", models.Skip(5).Sum(m => m.Total), source.Key);
                }
            }
            else
            {
                var models = selectedDays.SelectMany(d => d.Models).GroupBy(m => m.Key).Select(g => (Name: g.Key, Total: g.Sum(m => (decimal)m.Value))).Where(m => m.Total > 0).OrderByDescending(m => m.Total).ToArray();
                foreach (var model in models.Take(5)) AddModel(model.Name, model.Total);
                if (models.Length > 5) AddModel("Otros modelos", models.Skip(5).Sum(m => m.Total));
            }
        }
    }
    private static int ModelVariant(string model) => model.Aggregate(0, (hash, c) => (hash * 31 + c) & 0x7fffffff) % 3;
    private void AddModel(string name, decimal count, string? provider = null)
    {
        if (count <= 0) return;
        var line = new C.DockPanel { Margin = new W.Thickness(0, 5, 0, 0) };
        var value = Text(10, true); value.Text = UsagePresentation.Tokens((long)Math.Min(count, long.MaxValue)); C.DockPanel.SetDock(value, C.Dock.Right); line.Children.Add(value);
        var label = Text(10); label.Text = name; label.Foreground = UiTheme.Brush(ProviderIcons.Color(provider ?? (name.StartsWith("Antigravity ·", StringComparison.Ordinal) ? ProviderIds.Antigravity : ProviderIds.Codex), _theme, ModelVariant(name.Contains(" · ") ? name[(name.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : name))); line.Children.Add(label); _details.Children.Add(line);
    }
}
