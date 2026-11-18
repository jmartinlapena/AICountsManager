using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using S = System.Windows.Shapes;

namespace AICountsManager;

internal sealed class CurrentProviderCard : C.Border, IDisposable
{
    private UiTheme _theme;
    private ProviderUsage? _account;
    private readonly C.ContextMenu _menu = new();
    private readonly List<C.Button> _actions = [];
    private readonly AvailableResetsView _resets = new();
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
        if (_resets.Parent is C.Panel previousParent) previousParent.Children.Remove(_resets);
        _resets.Update(account.Resets, _theme);
        if (account.Resets is not null) stack.Children.Add(_resets);
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
            stack.Children.Add(new C.TextBlock { Text = account.Error ?? "Última lectura · pendiente de actualizar", FontSize = 10, TextTrimming = W.TextTrimming.CharacterEllipsis, Foreground = UiTheme.Brush(_theme.Warning), Margin = new(0, 7, 0, 0) });
    }
}
