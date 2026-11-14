using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using S = System.Windows.Shapes;

namespace AICountsManager;

internal sealed record ProviderTokenSeries(string Provider, decimal Tokens);
internal enum UsageView { Today, Week, Quotas }

internal sealed class ProviderOverview : C.Border
{
    private UiTheme _theme;
    private readonly C.StackPanel _root = new(), _legend = new();
    private readonly C.Border _tabsShell = new() { CornerRadius = new(9), Padding = new(3) };
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
        _tabsShell.Child = tabs;
        _root.Children.Add(_tabsShell);
        _body.Margin = new(0, 7, 0, 0); _body.ColumnDefinitions.Add(new() { Width = new W.GridLength(110) }); _body.ColumnDefinitions.Add(new());
        _body.Children.Add(_rings); C.Grid.SetColumn(_legend, 1); _legend.VerticalAlignment = W.VerticalAlignment.Center; _body.Children.Add(_legend); _root.Children.Add(_body);
        _root.Children.Add(_history);
        _today.Click += (_, _) => Select(UsageView.Today); _week.Click += (_, _) => Select(UsageView.Week); _quotas.Click += (_, _) => Select(UsageView.Quotas); Render();
    }
    private static C.Button Tab(string label) => new() { Content = label, FontSize = 11, MinHeight = 25, Padding = new W.Thickness(8, 3, 8, 3) };
    private void Select(UsageView view)
    {
        SelectedView = view;
        if (view != UsageView.Quotas) _history.ShowPeriod(view == UsageView.Today);
        RenderSelection();
    }
    private void RenderSelection()
    {
        _tabsShell.Background = UiTheme.Brush(_theme.Text, _theme.Dark ? (byte)14 : (byte)10);
        StyleTab(_today, SelectedView == UsageView.Today);
        StyleTab(_week, SelectedView == UsageView.Week);
        StyleTab(_quotas, SelectedView == UsageView.Quotas);
        _body.Visibility = SelectedView == UsageView.Quotas ? W.Visibility.Visible : W.Visibility.Collapsed;
        _history.Visibility = SelectedView == UsageView.Quotas ? W.Visibility.Collapsed : W.Visibility.Visible;
    }
    private void StyleTab(C.Button tab, bool selected)
    {
        tab.FontWeight = selected ? W.FontWeights.SemiBold : W.FontWeights.Normal;
        tab.Foreground = UiTheme.Brush(selected ? _theme.Text : _theme.Muted);
        tab.Background = selected
            ? (_theme.Dark ? UiTheme.Brush(_theme.Text, 26) : UiTheme.Brush(M.Colors.White, 200))
            : M.Brushes.Transparent;
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

internal sealed class CurrentProviderCard : C.Border, IDisposable
{
    private UiTheme _theme;
    private ProviderUsage? _account;
    private readonly C.ContextMenu _menu = new();
    private readonly List<C.Button> _actions = [];
    internal double EstimatedHeight => _account is null ? 130 : 74 + _account.Quotas.GroupBy(q => q.Group).Count() * 24 + _account.Quotas.Count * 46;
    public event Func<string, Task>? ActivateRequested, RenameRequested, DeleteRequested;
    public bool MenuOpen => _menu.IsOpen;
    public void SetBusy(bool busy) { foreach (var action in _actions) action.IsEnabled = !busy; if (busy) _menu.IsOpen = false; }
    public void Dispose() { _menu.IsOpen = false; _menu.Items.Clear(); _menu.PlacementTarget = null; ActivateRequested = RenameRequested = DeleteRequested = null; Child = null; }
    public CurrentProviderCard(UiTheme theme) { _theme = theme; CornerRadius = new(11); Padding = new(12, 10, 12, 11); Margin = new(0, 0, 0, 8); BorderThickness = new(1); BorderBrush = M.Brushes.Transparent; }
    public void SetTheme(UiTheme theme) { _theme = theme; if (_account is not null) Update(_account); }
    public void Update(ProviderUsage account)
    {
        _menu.IsOpen = false; _menu.Items.Clear(); _actions.Clear();
        _account = account; W.Automation.AutomationProperties.SetName(this, "Cuenta " + ProviderIds.Name(account.Provider) + " · " + account.Name);
        var providerColor = ProviderIcons.Color(account.Provider, _theme);
        Background = UiTheme.Brush(_theme.Dark ? M.Colors.White : M.Colors.Black, account.IsActive ? (byte)17 : (byte)10);
        BorderBrush = UiTheme.Brush(account.IsActive ? providerColor : _theme.Text, account.IsActive ? (byte)45 : (byte)0);
        var stack = new C.StackPanel(); Child = stack;
        var header = new C.DockPanel { Margin = new(0, 0, 0, 9) };
        if (account.Provider == ProviderIds.Claude && account.ProfileId is { } id)
        {
            var actions = new C.StackPanel { Orientation = C.Orientation.Horizontal, VerticalAlignment = W.VerticalAlignment.Top };
            if (!account.IsActive)
            {
                var activate = new C.Button { Content = "Activar", FontSize = 10, Padding = new W.Thickness(8, 3, 8, 3), MinHeight = 24 };
                activate.Click += async (_, _) => { if (ActivateRequested is { } action) await action(id); }; actions.Children.Add(activate); _actions.Add(activate);
            }
            else
            {
                var status = new C.StackPanel { Margin = new W.Thickness(0, 0, 6, 0), VerticalAlignment = W.VerticalAlignment.Center };
                status.Children.Add(new C.TextBlock { Text = "● Activa", FontSize = 10, FontWeight = W.FontWeights.SemiBold, HorizontalAlignment = W.HorizontalAlignment.Right, Foreground = UiTheme.Brush(providerColor) });
                status.Children.Add(new C.TextBlock { Text = AICountsManagerService.FormatRelative(account.CheckedAt), FontSize = 9.5, HorizontalAlignment = W.HorizontalAlignment.Right, Foreground = UiTheme.Brush(_theme.Muted) });
                actions.Children.Add(status);
            }
            var more = new C.Button { Content = "\uE712", FontFamily = new M.FontFamily("Segoe Fluent Icons"), Width = 24, Padding = new W.Thickness(0), MinHeight = 24 };
            W.Automation.AutomationProperties.SetName(more, "Opciones de la cuenta");
            _actions.Add(more);
            AccountMenuStyle.Apply(_menu);
            _theme.Apply(_menu.Resources, false);
            _menu.Background = UiTheme.Brush(_theme.Base); _menu.Foreground = UiTheme.Brush(_theme.Text);
            var rename = new C.MenuItem { Header = "Renombrar" }; rename.Click += async (_, _) => { if (RenameRequested is { } action) await action(id); };
            var delete = new C.MenuItem { Header = "Eliminar perfil", IsEnabled = !account.IsActive }; delete.Click += async (_, _) => { if (DeleteRequested is { } action) await action(id); };
            _menu.Items.Add(rename); _menu.Items.Add(delete);
            more.Click += (_, _) => { _menu.PlacementTarget = more; _menu.IsOpen = true; }; actions.Children.Add(more);
            C.DockPanel.SetDock(actions, C.Dock.Right); header.Children.Add(actions);
        }
        header.Children.Add(ProviderIcons.Create(account.Provider, _theme));
        var identity = new C.StackPanel();
        var title = new C.Grid { HorizontalAlignment = W.HorizontalAlignment.Left };
        title.ColumnDefinitions.Add(new C.ColumnDefinition());
        title.ColumnDefinitions.Add(new C.ColumnDefinition { Width = W.GridLength.Auto });
        title.Children.Add(new C.TextBlock { Text = account.Name, FontSize = 13, FontWeight = W.FontWeights.SemiBold,
            VerticalAlignment = W.VerticalAlignment.Center, TextTrimming = W.TextTrimming.CharacterEllipsis });
        if (account.Plan is { Length: > 0 } plan)
        {
            var badge = new C.Border { CornerRadius = new(4), Padding = new(5, 2, 5, 2), Margin = new(6, 0, 0, 0),
                VerticalAlignment = W.VerticalAlignment.Center, Background = UiTheme.Brush(providerColor, _theme.HighContrast ? (byte)0 : (byte)20),
                Child = new C.TextBlock { Text = plan.Replace("Google AI ", ""), FontSize = 9.5, FontWeight = W.FontWeights.SemiBold,
                    Foreground = UiTheme.Brush(providerColor), TextTrimming = W.TextTrimming.CharacterEllipsis, MaxWidth = 110 } };
            C.Grid.SetColumn(badge, 1); title.Children.Add(badge);
        }
        identity.Children.Add(title);
        var fallback = account.Provider == ProviderIds.Claude && account.ProfileId is not null ? "Perfil local" : "Sesión actual · solo consulta";
        identity.Children.Add(new C.TextBlock { Text = account.Email ?? fallback, FontSize = 10,
            Margin = new(0, 2, 0, 0), Foreground = UiTheme.Brush(_theme.Muted), TextTrimming = W.TextTrimming.CharacterEllipsis });
        header.Children.Add(identity); stack.Children.Add(header);
        var groupIndex = 0;
        foreach (var group in account.Quotas.GroupBy(q => q.Group))
        {
            var color = ProviderIcons.Color(account.Provider, _theme, groupIndex++);
            stack.Children.Add(new C.TextBlock { Text = group.Key, FontSize = 10, Foreground = UiTheme.Brush(_theme.Muted),
                TextTrimming = W.TextTrimming.CharacterEllipsis, Margin = new(0, groupIndex == 1 ? 0 : 10, 0, 4) });
            var quotaIndex = 0;
            foreach (var q in group)
            {
                var used = q.CurrentUsed;
                var display = QuotaDisplay.Create(q.Window, used is double consumed ? 100 - consumed : null, account.Fresh, DateTimeOffset.UtcNow, account.CheckedAt);
                var statusColor = _theme.QuotaColor(display.Severity, color);
                var line = new C.DockPanel { Margin = new(0, quotaIndex++ == 0 ? 0 : 10, 0, 6) };
                if (display.Pace is not null)
                {
                    var forecast = new C.TextBlock { Text = display.ForecastText, Tag = "weekly-forecast", FontSize = 9.5, TextTrimming = W.TextTrimming.CharacterEllipsis,
                        HorizontalAlignment = W.HorizontalAlignment.Right,
                        Foreground = UiTheme.Brush(display.AtRisk ? _theme.Warning : _theme.Muted) };
                    C.DockPanel.SetDock(forecast, C.Dock.Right); line.Children.Add(forecast);
                }
                var heading = new C.StackPanel { Orientation = C.Orientation.Horizontal };
                heading.Children.Add(new C.TextBlock { Text = q.Label, FontSize = 11, FontWeight = W.FontWeights.SemiBold, TextTrimming = W.TextTrimming.CharacterEllipsis });
                var badge = new QuotaResetBadge(); badge.Update(display.ResetNotice, _theme, color); heading.Children.Add(badge);
                line.Children.Add(heading); stack.Children.Add(line);
                var grid = new C.Grid { Height = 4 }; grid.Children.Add(new C.Border { Background = UiTheme.Brush(_theme.Text, _theme.Dark ? (byte)26 : (byte)22), CornerRadius = new(2) });
                var fill = new C.Border { Background = UiTheme.Brush(statusColor), CornerRadius = new(2), HorizontalAlignment = W.HorizontalAlignment.Left };
                grid.SizeChanged += (_, _) => fill.Width = display.FillWidth(grid.ActualWidth); grid.Children.Add(fill);
                if (display.Reference is not null)
                {
                    var marker = new C.Border { Tag = "weekly-pace-marker", Width = 2, Height = 8, VerticalAlignment = W.VerticalAlignment.Center,
                        Background = UiTheme.Brush(_theme.Text, 170), HorizontalAlignment = W.HorizontalAlignment.Left };
                    grid.SizeChanged += (_, _) => marker.Margin = new W.Thickness(display.MarkerOffset(grid.ActualWidth), -2, 0, -2); grid.Children.Add(marker);
                }
                stack.Children.Add(grid);
                var footer = new C.DockPanel { Margin = new(0, 5, 0, 0) };
                var reset = new C.TextBlock { Text = display.ResetText ?? "Sin fecha", FontSize = 9.5, TextTrimming = W.TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = W.HorizontalAlignment.Right, Foreground = UiTheme.Brush(_theme.Muted) };
                C.DockPanel.SetDock(reset, C.Dock.Right); footer.Children.Add(reset);
                footer.Children.Add(new C.TextBlock { Text = display.RemainingText + (used.HasValue ? " restante" : ""),
                    FontSize = 10, TextTrimming = W.TextTrimming.CharacterEllipsis, Foreground = UiTheme.Brush(statusColor) });
                stack.Children.Add(footer);
            }
        }
        if (!account.Fresh || account.Error is not null)
            stack.Children.Add(new C.TextBlock { Text = account.Error ?? "Última lectura · pendiente de actualizar", FontSize = 10, TextWrapping = W.TextWrapping.Wrap, Foreground = UiTheme.Brush(_theme.Warning), Margin = new(0, 7, 0, 0) });
    }
}
