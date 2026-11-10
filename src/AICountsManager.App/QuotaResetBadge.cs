using C = System.Windows.Controls;
using W = System.Windows;
using M = System.Windows.Media;

namespace AICountsManager;

// A static visual: updated by the panel's existing visible-only clock, with no timer of its own.
internal sealed class QuotaResetBadge : C.Border
{
    private readonly C.TextBlock _text = new() { FontSize = 8.5, FontWeight = W.FontWeights.Bold };
    internal QuotaResetBadge()
    {
        Tag = "quota-reset-badge"; CornerRadius = new(3); Padding = new(4, 1, 4, 1);
        Margin = new(6, 0, 0, 0); VerticalAlignment = W.VerticalAlignment.Center;
        Visibility = W.Visibility.Collapsed; Child = _text;
    }
    internal void Update(QuotaResetNotice state, UiTheme theme, M.Color accent)
    {
        Visibility = state == QuotaResetNotice.None ? W.Visibility.Collapsed : W.Visibility.Visible;
        _text.Text = state == QuotaResetNotice.Expected ? "RESET" : "NEW";
        var color = state == QuotaResetNotice.Expected ? theme.Warning : accent;
        _text.Foreground = UiTheme.Brush(color);
        Background = UiTheme.Brush(color, theme.HighContrast ? (byte)0 : (byte)28);
        BorderThickness = new(theme.HighContrast ? 1 : 0); BorderBrush = UiTheme.Brush(color);
        W.Automation.AutomationProperties.SetName(this, state == QuotaResetNotice.Expected
            ? "Reinicio previsto. Actualiza para comprobar la cuota."
            : "Cuota completa sin iniciar. Envía un mensaje desde el proveedor para arrancarla.");
    }
}
