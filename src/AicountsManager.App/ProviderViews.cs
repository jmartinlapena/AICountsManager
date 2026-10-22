using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using S = System.Windows.Shapes;

namespace AicountsManager;

internal sealed record ProviderTokenSeries(string Provider, decimal Tokens);
internal enum UsageView { Today, Week, Quotas }

internal sealed class ProviderOverview : C.Border
{
    private UiTheme _theme;
    private readonly C.StackPanel _root = new(), _legend = new();
    private readonly C.Grid _body = new();
    private readonly C.Canvas _rings = new() { Width = 100, Height = 100 };
    private readonly C.Button _today = Tab("Hoy"), _week = Tab("Semana"), _quotas = Tab("Cuotas");
    private readonly HistoryPanel _history;
    private IReadOnlyList<ProviderTokenSeries> _series = [];
    private bool _measured, _partial;
    public UsageView SelectedView { get; private set; } = UsageView.Today;
    public ProviderOverview(UiTheme theme, HistoryPanel history)
    {
        _theme = theme; _history = history; CornerRadius = new(13); Padding = new(12, 9, 12, 10); Margin = new(0, 0, 0, 10); Child = _root;
        var tabs = new C.Primitives.UniformGrid { Columns = 3 };
        tabs.Children.Add(_today); tabs.Children.Add(_week); tabs.Children.Add(_quotas);
        _root.Children.Add(tabs);
        _body.Margin = new(0, 7, 0, 0); _body.ColumnDefinitions.Add(new() { Width = new W.GridLength(110) }); _body.ColumnDefinitions.Add(new());
        _body.Children.Add(_rings); C.Grid.SetColumn(_legend, 1); _legend.VerticalAlignment = W.VerticalAlignment.Center; _body.Children.Add(_legend); _root.Children.Add(_body);
        _root.Children.Add(_history);
        _today.Click += (_, _) => Select(UsageView.Today); _week.Click += (_, _) => Select(UsageView.Week); _quotas.Click += (_, _) => Select(UsageView.Quotas); Render();
    }
    private static C.Button Tab(string label) => new() { Content = label, FontSize = 11, MinHeight = 27, Padding = new W.Thickness(8, 4, 8, 4) };
    private void Select(UsageView view)
    {
        SelectedView = view;
        if (view != UsageView.Quotas) _history.ShowPeriod(view == UsageView.Today);
        RenderSelection();
    }
    private void RenderSelection()
    {
        _today.Background = UiTheme.Brush(_theme.Text, SelectedView == UsageView.Today ? (byte)20 : (byte)0);
        _week.Background = UiTheme.Brush(_theme.Text, SelectedView == UsageView.Week ? (byte)20 : (byte)0);
        _quotas.Background = UiTheme.Brush(_theme.Text, SelectedView == UsageView.Quotas ? (byte)20 : (byte)0);
        _body.Visibility = SelectedView == UsageView.Quotas ? W.Visibility.Visible : W.Visibility.Collapsed;
        _history.Visibility = SelectedView == UsageView.Quotas ? W.Visibility.Collapsed : W.Visibility.Visible;
    }
    public void SetTheme(UiTheme theme) { _theme = theme; Render(); }
    public void Update(TokenHistorySnapshot snapshot)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = HistoryPanel.Monday(today);
        var sources = snapshot.Sources ?? new Dictionary<string, TokenHistorySnapshot> { [ProviderIds.Codex] = snapshot };
        _measured = sources.Values.Any(s => s.UpdatedAt is not null);
        _partial = snapshot.Partial || snapshot.CatchingUp || snapshot.Error is not null ||
            sources.Values.Any(s => s.UpdatedAt is null || s.Partial || s.CatchingUp || s.Error is not null);
        _series = sources.Where(s => s.Value.UpdatedAt is not null)
            .Select(s => new ProviderTokenSeries(s.Key, s.Value.Days
                .Where(d => DateOnly.TryParse(d.Date, out var date) && date >= monday && date <= today)
                .Sum(d => (decimal)Math.Max(0, d.Total))))
            .Where(s => s.Tokens > 0).ToArray();
        Render();
    }
    internal IReadOnlyList<ProviderTokenSeries> Series => _series;
    private void Render()
    {
        Background = UiTheme.Brush(_theme.Dark ? M.Colors.White : M.Colors.Black, _theme.Dark ? (byte)12 : (byte)10);
        RenderSelection();
        _rings.Children.Clear(); _legend.Children.Clear();
        const double radius = 40, thickness = 14;
        var track = new S.Ellipse { Width = radius * 2 + thickness, Height = radius * 2 + thickness, Stroke = UiTheme.Brush(_theme.Text, 24), StrokeThickness = thickness };
        C.Canvas.SetLeft(track, 50 - radius - thickness / 2); C.Canvas.SetTop(track, 50 - radius - thickness / 2); _rings.Children.Add(track);
        var total = _series.Sum(s => s.Tokens);
        var start = -Math.PI / 2;
        foreach (var item in _series)
        {
            var color = ProviderIcons.Color(item.Provider, _theme);
            var fraction = (double)(item.Tokens / total);
            var end = start + fraction * Math.PI * 2;
            S.Shape segment;
            if (_series.Count == 1)
            {
                segment = new S.Ellipse { Width = radius * 2 + thickness, Height = radius * 2 + thickness, Stroke = UiTheme.Brush(color), StrokeThickness = thickness };
                C.Canvas.SetLeft(segment, 50 - radius - thickness / 2); C.Canvas.SetTop(segment, 50 - radius - thickness / 2);
            }
            else
            {
                var geometry = new M.StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new W.Point(50 + radius * Math.Cos(start), 50 + radius * Math.Sin(start)), false, false);
                    context.ArcTo(new W.Point(50 + radius * Math.Cos(end), 50 + radius * Math.Sin(end)), new W.Size(radius, radius), 0, fraction > .5, M.SweepDirection.Clockwise, true, false);
                }
                geometry.Freeze();
                segment = new S.Path { Data = geometry, Stroke = UiTheme.Brush(color), StrokeThickness = thickness };
            }
            _rings.Children.Add(segment); start = end;
            var row = new C.DockPanel { Margin = new(0, 0, 0, 7) };
            var value = new C.TextBlock { Text = UsagePresentation.Tokens((long)Math.Min(item.Tokens, long.MaxValue)), FontSize = 11, FontWeight = W.FontWeights.SemiBold };
            C.DockPanel.SetDock(value, C.Dock.Right); row.Children.Add(value);
            row.Children.Add(new S.Ellipse { Width = 6, Height = 6, Fill = UiTheme.Brush(color), Margin = new(0, 0, 7, 0) });
            row.Children.Add(new C.TextBlock { Text = ProviderIds.Name(item.Provider), FontSize = 10.5, TextTrimming = W.TextTrimming.CharacterEllipsis });
            W.Automation.AutomationProperties.SetName(row, $"{ProviderIds.Name(item.Provider)}: {item.Tokens:N0} tokens esta semana, {UsagePresentation.Percent(fraction * 100)} del uso registrado");
            _legend.Children.Add(row);
        }
        var center = new C.StackPanel { Width = 64, IsHitTestVisible = false };
        center.Children.Add(new C.TextBlock { Text = _measured ? UsagePresentation.Tokens((long)Math.Min(total, long.MaxValue)) : "—", TextAlignment = W.TextAlignment.Center, FontSize = 15, FontWeight = W.FontWeights.SemiBold });
        center.Children.Add(new C.TextBlock { Text = "tokens", TextAlignment = W.TextAlignment.Center, FontSize = 9, Foreground = UiTheme.Brush(_theme.Muted) });
        C.Canvas.SetLeft(center, 18); C.Canvas.SetTop(center, 34); _rings.Children.Add(center);
        var status = !_measured ? "Historial no disponible" : total == 0 ? "Sin actividad esta semana" : "Tokens · semana actual";
        if (_measured && _partial) status += "\nHistorial parcial";
        _legend.Children.Add(new C.TextBlock { Text = status, FontSize = 9, Foreground = UiTheme.Brush(_theme.Muted), TextWrapping = W.TextWrapping.Wrap });
    }
}

internal sealed class CurrentProviderCard : C.Border
{
    private UiTheme _theme;
    private ProviderUsage? _account;
    public CurrentProviderCard(UiTheme theme) { _theme = theme; CornerRadius = new(11); Padding = new(12, 10, 12, 11); Margin = new(0, 0, 0, 8); }
    public void SetTheme(UiTheme theme) { _theme = theme; if (_account is not null) Update(_account); }
    public void Update(ProviderUsage account)
    {
        _account = account; W.Automation.AutomationProperties.SetName(this, "Cuenta " + ProviderIds.Name(account.Provider) + " · " + account.Name); Background = UiTheme.Brush(_theme.Dark ? M.Colors.White : M.Colors.Black, 10);
        var stack = new C.StackPanel(); Child = stack;
        var header = new C.DockPanel(); header.Children.Add(ProviderIcons.Create(account.Provider, _theme));
        var identity = new C.StackPanel();
        identity.Children.Add(new C.TextBlock { Text = account.Name + (account.Plan is { Length: > 0 } ? " · " + account.Plan.Replace("Google AI ", "") : ""), FontSize = 12, FontWeight = W.FontWeights.SemiBold, TextTrimming = W.TextTrimming.CharacterEllipsis });
        identity.Children.Add(new C.TextBlock { Text = account.Email ?? "Sesión actual · solo consulta", FontSize = 10, Foreground = UiTheme.Brush(_theme.Muted), TextTrimming = W.TextTrimming.CharacterEllipsis });
        header.Children.Add(identity); stack.Children.Add(header);
        var groupIndex = 0;
        foreach (var group in account.Quotas.GroupBy(q => q.Group))
        {
            var color = ProviderIcons.Color(account.Provider, _theme, groupIndex++);
            stack.Children.Add(new C.TextBlock { Text = group.Key, FontSize = 10.5, Foreground = UiTheme.Brush(_theme.Muted), Margin = new(0, 10, 0, 4) });
            foreach (var q in group)
            {
                var used = q.CurrentUsed; var line = new C.DockPanel();
                var display = QuotaDisplay.Create(q.Window, used is double consumed ? 100 - consumed : null, account.Fresh, DateTimeOffset.UtcNow);
                var percent = new C.TextBlock { Text = display.RemainingText, FontSize = 11, Foreground = UiTheme.Brush(color) };
                C.DockPanel.SetDock(percent, C.Dock.Right); line.Children.Add(percent);
                line.Children.Add(new C.TextBlock { Text = q.Label + " restante", FontSize = 10.5 }); stack.Children.Add(line);
                var grid = new C.Grid { Height = 4, Margin = new(0, 4, 0, 3) }; grid.Children.Add(new C.Border { Background = UiTheme.Brush(_theme.Text, 24), CornerRadius = new(2) });
                var fill = new C.Border { Background = UiTheme.Brush(color), CornerRadius = new(2), HorizontalAlignment = W.HorizontalAlignment.Left };
                grid.SizeChanged += (_, _) => fill.Width = display.FillWidth(grid.ActualWidth); grid.Children.Add(fill);
                if (display.Reference is not null)
                {
                    var marker = new C.Border { Tag = "weekly-pace-marker", Width = 2, Height = 7, Background = UiTheme.Brush(_theme.Text, 180), HorizontalAlignment = W.HorizontalAlignment.Left };
                    grid.SizeChanged += (_, _) => marker.Margin = new W.Thickness(display.MarkerOffset(grid.ActualWidth), -1.5, 0, -1.5); grid.Children.Add(marker);
                }
                stack.Children.Add(grid);
                var footer = new C.DockPanel { Margin = new(0, 0, 0, 5) };
                if (display.Pace is not null)
                {
                    var forecast = new C.TextBlock { Text = display.ForecastText, Tag = "weekly-forecast", FontSize = 9,
                        Foreground = UiTheme.Brush(display.AtRisk ? _theme.Warning : _theme.Muted), Margin = new(6, 0, 0, 0) };
                    C.DockPanel.SetDock(forecast, C.Dock.Right); footer.Children.Add(forecast);
                }
                footer.Children.Add(new C.TextBlock { Text = display.ResetText, FontSize = 9, Foreground = UiTheme.Brush(_theme.Muted) });
                stack.Children.Add(footer);
            }
        }
        if (!account.Fresh || account.Error is not null)
            stack.Children.Add(new C.TextBlock { Text = account.Error ?? "Última lectura · pendiente de actualizar", FontSize = 10, TextWrapping = W.TextWrapping.Wrap, Foreground = UiTheme.Brush(_theme.Warning), Margin = new(0, 7, 0, 0) });
    }
}
