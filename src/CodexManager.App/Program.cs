using System.Drawing;
using System.Windows.Forms;

namespace CodexManager;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\CodexManager.SingleInstance",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var diagnostics = new AppDiagnostics(AppPaths.Build().LogsDir);
        diagnostics.Write("process.start version=" + typeof(Program).Assembly.GetName().Version);
        UnhandledExceptionEventHandler unhandled = (_, e) => diagnostics.Write("process.unhandled terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
        AppDomain.CurrentDomain.UnhandledException += unhandled;
        // Recoverable refresh failures are handled at their own boundary. Unknown
        // failures still terminate, but leave a trace instead of disappearing silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new TrayApplicationContext());
            diagnostics.Write("process.message-loop-returned");
        }
        catch (Exception ex)
        {
            diagnostics.Write("process.fatal", ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            diagnostics.Write("process.exit code=" + Environment.ExitCode);
            AppDomain.CurrentDomain.UnhandledException -= unhandled;
            singleInstance.ReleaseMutex();
        }
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CodexManagerService _service;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private WidgetForm? _widget;
    private readonly Control _dispatcher = new();
    internal bool WidgetCreated => _widget is not null;
    internal bool WidgetVisible => _widget?.Visible == true;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _exiting;
    private readonly CancellationTokenSource _lifetime = new();

    public TrayApplicationContext(CodexManagerService? service = null)
    {
        _service = service ?? new CodexManagerService();
        // Marshal watcher notifications without creating the WPF/DWM flyout at startup.
        _ = _dispatcher.Handle;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Salir", null, (_, _) =>
        {
            if (_service.IsMutationInProgress)
                _notifyIcon?.ShowBalloonTip(3000, "CodexManager", "Espera a que termine la operación de cuentas antes de salir.", ToolTipIcon.Info);
            else { _service.Diagnostics.Write("tray.user-exit"); ExitThread(); }
        });

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

        _service.StateChanged += OnServiceStateChanged;
        ApplyState();
        _ = RefreshAsync();
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        _service.Diagnostics.Write("tray.exit-thread");
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _service.StateChanged -= OnServiceStateChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _widget?.Dispose();
        _dispatcher.Dispose();
        _service.Dispose();
        _lifetime.Dispose();
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
            ToggleWidget();
        }
    }

    internal void ToggleWidget()
    {
        if (_widget is null)
        {
            _widget = new WidgetForm(_service);
            _ = _widget.Handle;
            _widget.HideRequested += (_, _) => _widget.Hide();
        }
        if (_widget.Visible) _widget.Hide();
        else { _widget.ShowNearTray(); _widget.Activate(); }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _service.RefreshCurrentProvidersAsync(_lifetime.Token);
        }
        catch (Exception ex)
        {
            if (!_exiting) ReportRefreshFailure("tray.refresh", ex);
        }
    }

    private void ReportRefreshFailure(string operation, Exception ex)
    {
        if (_exiting) return;
        _service.Diagnostics.Write(operation, ex);
        try { _widget?.ShowRefreshError(); }
        catch (Exception displayError) { _service.Diagnostics.Write("tray.error-display", displayError); }
    }

    private void ApplyState()
    {
        if (_exiting || _dispatcher.IsDisposed)
        {
            return;
        }
        try
        {
            var state = _service.BuildDashboardState();
            _widget?.ApplyState(state);
            _notifyIcon.Text = _service.BuildTrayTooltip(state);
        }
        catch (Exception ex) { ReportRefreshFailure("tray.apply-state", ex); }
    }

    private void OnServiceStateChanged(object? sender, EventArgs e)
    {
        if (_exiting || _dispatcher.IsDisposed)
        {
            return;
        }
        if (_dispatcher.InvokeRequired)
        {
            try
            {
                _dispatcher.BeginInvoke(() => { if (!_exiting && !_dispatcher.IsDisposed) ApplyState(); });
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }
        ApplyState();
    }
}
