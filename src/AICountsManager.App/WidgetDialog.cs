using W = System.Windows;
using C = System.Windows.Controls;
using Interop = System.Windows.Interop;
using M = System.Windows.Media;

namespace AICountsManager;

internal static class WidgetDialog
{
    public static W.Window Create(W.Window owner, UiTheme theme, string title, string text, string action,
        C.TextBox? input, bool allowCancel = true, bool destructive = false)
    {
        var dialog = new W.Window
        {
            Owner = owner, Title = title, Width = Math.Min(356, owner.Width), SizeToContent = W.SizeToContent.Height,
            WindowStartupLocation = W.WindowStartupLocation.CenterOwner, WindowStyle = W.WindowStyle.None,
            ResizeMode = W.ResizeMode.NoResize, ShowInTaskbar = false, Background = M.Brushes.Transparent,
            Foreground = UiTheme.Brush(theme.Text), FontFamily = owner.FontFamily, FontSize = 12, UseLayoutRounding = true,
        };
        dialog.Resources.MergedDictionaries.Add(owner.Resources);

        var (icon, color) = (input is not null, destructive, allowCancel) switch
        {
            (true, _, _) => ("\uE70F", theme.Accent),
            (_, true, _) => ("\uE74D", theme.Critical),
            (_, _, false) => ("\uE7BA", theme.Warning),
            _ => ("\uE8AB", theme.Accent),
        };
        var header = new C.DockPanel { Margin = new(0, 0, 0, 9) };
        var iconTile = new C.Border
        {
            Width = 34, Height = 34, CornerRadius = new(8), Margin = new(0, 0, 10, 0),
            Background = UiTheme.Brush(color, 30), IsHitTestVisible = false,
            Child = new C.TextBlock
            {
                Text = icon, FontFamily = new M.FontFamily("Segoe Fluent Icons"), FontSize = 15,
                Foreground = UiTheme.Brush(color), HorizontalAlignment = W.HorizontalAlignment.Center,
                VerticalAlignment = W.VerticalAlignment.Center,
            },
        };
        C.DockPanel.SetDock(iconTile, C.Dock.Left);
        header.Children.Add(iconTile);
        header.Children.Add(new C.TextBlock
        {
            Text = title, FontSize = 16, FontWeight = W.FontWeights.SemiBold, TextWrapping = W.TextWrapping.Wrap,
            VerticalAlignment = W.VerticalAlignment.Center,
        });
        var content = new C.StackPanel();
        content.Children.Add(header);
        content.Children.Add(new C.TextBlock { Text = text, TextWrapping = W.TextWrapping.Wrap, Foreground = UiTheme.Brush(theme.Muted) });
        if (input is not null)
        {
            input.Margin = new(0, 10, 0, 0);
            content.Children.Add(input);
        }
        content.Children.Add(new C.Border { Height = 1, Background = UiTheme.Brush(theme.Text, 28), Margin = new(0, 14, 0, 10) });
        var buttons = new C.StackPanel { Orientation = C.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right };
        if (allowCancel)
        {
            var cancel = new C.Button { Content = "Cancelar", IsCancel = true, Margin = new(0, 0, 7, 0), MinWidth = 78 };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            buttons.Children.Add(cancel);
        }
        var confirm = new C.Button
        {
            Content = action, IsDefault = true, MinWidth = 82, FontWeight = W.FontWeights.SemiBold,
            Background = UiTheme.Brush(color, 57), BorderBrush = UiTheme.Brush(color, theme.HighContrast ? (byte)255 : (byte)124),
            BorderThickness = new(1),
        };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(confirm);
        content.Children.Add(buttons);

        var surface = new C.Border { Style = (W.Style)owner.FindResource("OverlaySurface"), Padding = new(16), Child = content };
        dialog.Content = surface;
        dialog.SourceInitialized += (_, _) =>
        {
            var handle = new Interop.WindowInteropHelper(dialog).Handle;
            if (!WindowMaterial.Apply(handle, theme.Dark, theme.Transparency && !theme.HighContrast)) return;
            Interop.HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = M.Colors.Transparent;
            surface.Background = UiTheme.Brush(theme.Surface, 110);
        };
        dialog.Loaded += (_, _) => { if (input is not null) { input.Focus(); input.SelectAll(); } };
        return dialog;
    }
}
