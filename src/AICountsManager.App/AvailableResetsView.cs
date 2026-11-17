using System.Globalization;
using C = System.Windows.Controls;
using W = System.Windows;
using M = System.Windows.Media;

namespace AICountsManager;

// Shared by every provider. Expanding only reveals local metadata; it never starts a request or a timer.
internal sealed class AvailableResetsView : C.StackPanel
{
    private readonly C.Button _toggle = new() { Padding = new(0, 4, 0, 4), MinHeight = 26,
        HorizontalContentAlignment = W.HorizontalAlignment.Stretch, Background = M.Brushes.Transparent, BorderThickness = new(0) };
    private readonly C.TextBlock _label = new() { FontSize = 10.5, TextTrimming = W.TextTrimming.CharacterEllipsis };
    private readonly C.TextBlock _arrow = new() { FontFamily = new M.FontFamily("Segoe Fluent Icons"), FontSize = 10,
        HorizontalAlignment = W.HorizontalAlignment.Right, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly C.StackPanel _details = new() { Margin = new(0, 2, 0, 8), Visibility = W.Visibility.Collapsed };
    private ResetInventory? _inventory;
    private UiTheme _theme = new(true, false, false);
    private bool _expanded;
    public event Action? HeightChanged;
    internal bool Expanded => _expanded;
    internal double EstimatedHeight => Visibility != W.Visibility.Visible ? 0 : 28 + (_expanded ? _details.Children.Count * 32 + 12 : 0);

    public AvailableResetsView()
    {
        var heading = new C.Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        heading.Children.Add(_label); C.Grid.SetColumn(_arrow, 1); heading.Children.Add(_arrow); _toggle.Content = heading;
        _toggle.Click += (_, _) => { SetExpanded(!_expanded); HeightChanged?.Invoke(); };
        Children.Add(_toggle); Children.Add(_details); Visibility = W.Visibility.Collapsed;
    }
    internal void SetExpanded(bool expanded)
    {
        _expanded = expanded; _arrow.Text = expanded ? "\uE70E" : "\uE70D";
        _details.Visibility = expanded ? W.Visibility.Visible : W.Visibility.Collapsed;
        W.Automation.AutomationProperties.SetHelpText(_toggle, expanded ? "Ocultar resets disponibles" : "Mostrar resets disponibles");
        RenderDetails();
    }
    public void Update(ResetInventory? inventory, UiTheme theme, DateTimeOffset? at = null)
    {
        _inventory = inventory; _theme = theme;
        var now = at ?? DateTimeOffset.UtcNow;
        var count = inventory?.CountAt(now);
        Visibility = inventory is null || inventory.Status == "unsupported" || count == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        _label.Text = count is null ? "Resets pendientes de consultar" : $"{count} reset{(count == 1 ? " disponible" : "s disponibles")}";
        if (count is not null && inventory is not null && (inventory.Status == "stale" || now - inventory.CheckedAtUtc > TimeSpan.FromMinutes(15))) _label.Text += " · última lectura";
        _label.Foreground = _arrow.Foreground = UiTheme.Brush(theme.Muted);
        var accessibleLabel = _label.Text;
        var expiry = count > 0 ? inventory?.ActiveItems(now).FirstOrDefault(i => i.ExpiresAtUtc is not null)?.ExpiresAtUtc : null;
        if (expiry is { } next && next - now <= TimeSpan.FromDays(7))
        {
            var hint = " · Caduca pronto: " + UsagePresentation.Duration((next - now).TotalSeconds);
            accessibleLabel += hint;
            _label.Inlines.Add(new System.Windows.Documents.Run(hint)
            { Foreground = UiTheme.Brush(theme.Warning) });
        }
        W.Automation.AutomationProperties.SetName(_toggle, accessibleLabel);
        _arrow.Text = _expanded ? "\uE70E" : "\uE70D";
        RenderDetails();
    }
    internal static string ExpiryLabel(AvailableReset reset, TimeZoneInfo? zone = null)
    {
        if (reset.ExpiresAtUtc is not { } expiry) return reset.ExpiryKnown ? "Sin caducidad" : "Caducidad no informada";
        var local = TimeZoneInfo.ConvertTime(expiry, zone ?? TimeZoneInfo.Local);
        return local.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) + " (" + local.ToString("zzz", CultureInfo.InvariantCulture) + ")";
    }
    private void RenderDetails()
    {
        _details.Children.Clear();
        if (!_expanded || Visibility != W.Visibility.Visible || _inventory is null) return;
        var items = _inventory.ActiveItems(DateTimeOffset.UtcNow);
        foreach (var item in items)
        {
            var row = new C.Grid { Margin = new(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new W.GridLength(150) });
            var identity = new C.StackPanel { Margin = new(0, 0, 6, 0) };
            identity.Children.Add(Text($"{item.Count} × {item.Label}", 10.5, _theme.Text));
            if (item.Scope.Length != 0) identity.Children.Add(Text(item.Scope, 9.5, _theme.Muted));
            if (item.UsableNow == false) identity.Children.Add(Text("No utilizable todavía", 9.5, _theme.Muted));
            row.Children.Add(identity);
            var expiry = new C.StackPanel { HorizontalAlignment = W.HorizontalAlignment.Right };
            if (item.ExpiresAtUtc is not null) expiry.Children.Add(Text("Caduca", 9.5, _theme.Muted, true));
            expiry.Children.Add(Text(ExpiryLabel(item), 9.5, _theme.Muted, true));
            C.Grid.SetColumn(expiry, 1); row.Children.Add(expiry); _details.Children.Add(row);
        }
        if (!_inventory.DetailsComplete) _details.Children.Add(Text("Detalle pendiente de actualizar", 9.5, _theme.Muted));
        if (items.Count > 0) _details.Children.Add(Text("Hora local de Windows", 9.5, _theme.Muted));
        if (_inventory.Status == "stale") _details.Children.Add(Text("Última consulta: " + TimeZoneInfo.ConvertTime(_inventory.CheckedAtUtc, TimeZoneInfo.Local)
            .ToString("g", CultureInfo.CurrentCulture), 9.5, _theme.Muted));
    }
    private static C.TextBlock Text(string value, double size, M.Color color, bool right = false) => new()
    { Text = value, FontSize = size, TextWrapping = W.TextWrapping.Wrap, Foreground = UiTheme.Brush(color), TextAlignment = right ? W.TextAlignment.Right : W.TextAlignment.Left };
}
