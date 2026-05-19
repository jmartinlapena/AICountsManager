using System.Drawing;
using System.Windows.Forms;

namespace CodexManager;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CodexManagerService _service;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly WidgetForm _widget;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public TrayApplicationContext()
    {
        _service = new CodexManagerService();
        _widget = new WidgetForm(_service);
        _widget.HideRequested += (_, _) => _widget.Hide();

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Salir", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "CodexManager",
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseUp += OnTrayMouseUp;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        _service.StateChanged += (_, _) => ApplyState();
        ApplyState();
        _ = RefreshAsync();
    }

    protected override void ExitThreadCore()
    {
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _widget.Dispose();
        base.ExitThreadCore();
    }

    private static Icon LoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
    }

    private void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_widget.Visible)
            {
                _widget.Hide();
            }
            else
            {
                _widget.ShowNearTray();
                _widget.Activate();
            }
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _service.RefreshActiveProfileAsync();
        }
        catch
        {
            ApplyState();
        }
    }

    private void ApplyState()
    {
        var state = _service.BuildDashboardState();
        _widget.ApplyState(state);
        _notifyIcon.Text = _service.BuildTrayTooltip(state);
    }
}
