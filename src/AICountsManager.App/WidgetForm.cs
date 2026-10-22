using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Win32;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using I = System.Windows.Input;
using Interop = System.Windows.Interop;
using Threading = System.Windows.Threading;

namespace AICountsManager;

// The tray remains WinForms; WPF owns only the translucent, DPI-independent flyout.
internal sealed class WidgetForm : IDisposable
{
    private readonly AICountsManagerService _service;
    private readonly W.Window _window;
    private readonly UiTheme? _fixedTheme;
    private UiTheme _theme;
    private readonly Dictionary<string, AccountRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Threading.DispatcherTimer _clock;
    private DashboardState? _state;
    private readonly HistoryPanel _historyPanel;
    private readonly ProviderOverview _overview;
    private readonly Dictionary<string, CurrentProviderCard> _externalRows = new();
    private readonly C.ContextMenu _addMenu = new();
    private readonly Dictionary<string, C.CheckBox> _providerToggles = new();
    private readonly Threading.DispatcherTimer _historyTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _historyReading;
    private readonly bool _clockEnabled;
    private CancellationTokenSource? _historyCancellation;

    private bool _busy, _applying, _settingsVisible, _dialogOpen;
    private Interop.HwndSource? _source;
    public event EventHandler? HideRequested;
    public bool IsDisposed { get; private set; }
    public bool Visible => _window.IsVisible;
    public bool InvokeRequired => !_window.Dispatcher.CheckAccess();
    public IntPtr Handle => new Interop.WindowInteropHelper(_window).EnsureHandle();
    internal W.Window Visual => _window;
    internal IReadOnlyCollection<AccountRow> Rows => _rows.Values;
    internal bool AcrylicEnabled { get; private set; }
    private T Find<T>(string name) where T : W.FrameworkElement => (T)_window.FindName(name);

    public WidgetForm(AICountsManagerService service, UiTheme? theme = null, bool startClock = true)
    {
        _clockEnabled = startClock;
        _service = service; _fixedTheme = theme; _theme = theme ?? UiTheme.Read();
        using var stream = typeof(WidgetForm).Assembly.GetManifestResourceStream("AICountsManager.WidgetWindow.xaml")
            ?? throw new InvalidOperationException("No se encontró la interfaz de AICountsManager.");
        _window = (W.Window)System.Windows.Markup.XamlReader.Load(stream);
        _historyPanel = new HistoryPanel(_theme);
        _overview = new ProviderOverview(_theme, _historyPanel);
        var dashboard = Find<C.StackPanel>("DashboardStack");
        dashboard.Children.Insert(0, _overview);
        AccountMenuStyle.Apply(_addMenu);
        var codexAdd = new C.MenuItem { Header = "Guardar cuenta de Codex" };
        codexAdd.Click += async (_, _) => await AddAsync();
        _addMenu.Items.Add(codexAdd);
        foreach (var provider in _service.CurrentProviders)
        {
            var item = new C.MenuItem { Header = "Conectar " + provider.Name + " actual" };
            item.Click += async (_, _) => await RunAsync(async () => { await _service.SetCurrentProviderEnabledAsync(provider.Id, true); UpdateHistoryPanels(_service.History); return provider.Name + " conectado."; });
            _addMenu.Items.Add(item);
            var toggle = new C.CheckBox { Content = provider.Name + " · sesión actual" };
            toggle.Click += async (_, _) =>
            {
                if (!_applying) await RunAsync(async () => { await _service.SetCurrentProviderEnabledAsync(provider.Id, toggle.IsChecked == true); UpdateHistoryPanels(_service.History); return "Proveedor actualizado."; });
            };
            _providerToggles.Add(provider.Id, toggle); Find<C.StackPanel>("ProviderToggles").Children.Add(toggle);
        }
        _historyTimer = new Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _historyTimer.Tick += async (_, _) => { _historyTimer.Stop(); if (Visible) await UpdateHistoryAsync(); };
        _window.IsVisibleChanged += (_, _) => { if (!Visible) { _historyTimer.Stop(); _historyCancellation?.Cancel(); _clock?.Stop(); } else if (_clockEnabled) _clock?.Start(); };

        _window.Closing += (_, e) => { if (!IsDisposed) { e.Cancel = true; _window.Hide(); } };
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_window);
        _window.SourceInitialized += (_, _) =>
        {
            _source = Interop.HwndSource.FromHwnd(Handle);
            _source?.AddHook(WindowMessage);
            ApplyTheme();
        };
        _window.Deactivated += (_, _) => _window.Dispatcher.BeginInvoke(() =>
        {
            if (!IsDisposed && !_window.IsActive && !_dialogOpen && !_addMenu.IsOpen && !_rows.Values.Any(r => r.MenuOpen))
                HideRequested?.Invoke(this, EventArgs.Empty);
        }, Threading.DispatcherPriority.Background);
        _window.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == I.Key.Escape) { HideRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
            if (e.Key == I.Key.F5 && !_busy) { e.Handled = true; await RefreshAsync(); }
        };
        Click("RefreshButton", async () => await RefreshAsync());
        Click("SettingsButton", () => ShowSettings(!_settingsVisible));
        Click("AddButton", () => { _addMenu.PlacementTarget = Find<C.Button>("AddButton"); _addMenu.IsOpen = true; });
        Click("FolderButton", () =>
        {
            try { if (_state is not null) _service.OpenFolder(_state.DataFolder); }
            catch (Exception ex) { ShowError(ex.Message); }
        });
        Click("FileModeButton", async () =>
        {
            if (!Confirm("Guardar sesiones en archivo", "Codex guardará sus credenciales localmente para que AICountsManager pueda alternar entre perfiles. Reinicia Codex después de aplicar esta preferencia.", "Configurar")) return;
            await RunAsync(() => { _service.EnsureFileModeConfig(); return Task.FromResult("Configuración guardada. Reinicia Codex para aplicarla."); });
        });
        Find<C.CheckBox>("StrictToggle").Click += async (_, _) =>
        {
            if (!_applying) await RunAsync(async () => { await _service.SetStrictVerificationAsync(Find<C.CheckBox>("StrictToggle").IsChecked == true); return "Preferencia guardada."; });
        };
        Find<C.CheckBox>("StartupToggle").Click += async (_, _) =>
        {
            if (!_applying) await RunAsync(async () =>
            {
                var enabled = Find<C.CheckBox>("StartupToggle").IsChecked == true;
                await Task.Run(() => _service.SetStartupEnabled(enabled)); return "Inicio con Windows actualizado.";
            });
        };
        _clock = new Threading.DispatcherTimer(TimeSpan.FromMinutes(1), Threading.DispatcherPriority.Background,
            (_, _) => { if (Visible && !_busy) RefreshState(); }, _window.Dispatcher);
        _clock.Stop();
        ApplyTheme();
    }
    private void Click(string name, Action action) => Find<C.Button>(name).Click += (_, _) => action();
    public void BeginInvoke(Action action) => _window.Dispatcher.BeginInvoke(() => { if (!IsDisposed) action(); });
    public void Hide() => _window.Hide();
    public void Activate() => _window.Activate();
    private IntPtr WindowMessage(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message is 0x001A or 0x031A) BeginInvoke(() =>
        {
            var next = _fixedTheme ?? UiTheme.Read();
            // Setting the DWM dark-mode attribute can itself emit WM_THEMECHANGED.
            // Reapplying an identical material here would create a message/render loop.
            if (next == _theme) return;
            _theme = next; ApplyTheme();
        });
        return IntPtr.Zero;
    }
    private void ApplyTheme()
    {
        if (_source is not null)
        {
            AcrylicEnabled = WindowMaterial.Apply(Handle, _theme.Dark, _theme.Transparency && !_theme.HighContrast);
            _source.CompositionTarget.BackgroundColor = AcrylicEnabled ? M.Colors.Transparent : _theme.Base;
        }
        _window.Background = AcrylicEnabled ? M.Brushes.Transparent : UiTheme.Brush(_theme.Base);
        _theme.Apply(_window.Resources, AcrylicEnabled);
        foreach (var row in _rows.Values) row.SetTheme(_theme);
        _historyPanel.SetTheme(_theme); _overview.SetTheme(_theme);
        foreach (var card in _externalRows.Values) card.SetTheme(_theme);
        _theme.Apply(_addMenu.Resources, false); _addMenu.Background = UiTheme.Brush(_theme.Base); _addMenu.Foreground = UiTheme.Brush(_theme.Text);
    }
    public void ShowNearTray()
    {
        _theme = _fixedTheme ?? UiTheme.Read(); ApplyTheme();
        RefreshState();
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        // Move the HWND to the target monitor first, then use its actual DPI for layout.
        WindowMaterial.Position(Handle, screen.Right - 382, screen.Bottom - 510);
        var dpi = WindowMaterial.Dpi(Handle) / 96d;
        _window.Width = Math.Min(382, Math.Max(280, screen.Width / dpi - 24));
        _window.Height = Math.Min(_settingsVisible ? 440 : Math.Max(440, Math.Min(710, 352 + _rows.Values.Sum(row => row.EstimatedHeight))), screen.Height / dpi - 24);
        WindowMaterial.Position(Handle, screen.Right - (int)Math.Ceiling(_window.Width * dpi) - 12, screen.Bottom - (int)Math.Ceiling(_window.Height * dpi) - 12);
        _window.Show(); _window.Activate();
        _ = UpdateHistoryAsync();
    }
    public void ApplyState(DashboardState state)
    {
        if (IsDisposed) return;
        _state = state; _applying = true;
        try
        {
            var stack = Find<C.StackPanel>("AccountList");
            var names = state.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _rows.Keys.Where(key => !names.Contains(key)).ToArray())
            { var row = _rows[key]; stack.Children.Remove(row); _rows.Remove(key); row.Dispose(); }
            foreach (var profile in state.Profiles)
            {
                if (!_rows.TryGetValue(profile.Name, out var row))
                {
                    row = new AccountRow(_theme);
                    row.ActivateRequested += SwitchAsync; row.RenameRequested += RenameAsync; row.DeleteRequested += DeleteAsync;
                    _rows.Add(profile.Name, row);
                }
                row.Update(profile, _busy);
            }
            var others = state.OtherAccounts ?? [];
            foreach (var old in _externalRows.Keys.Where(k => !others.Any(a => a.Provider == k)).ToArray())
            { stack.Children.Remove(_externalRows[old]); _externalRows.Remove(old); }
            foreach (var other in others)
            {
                if (!_externalRows.TryGetValue(other.Provider, out var card)) { card = new CurrentProviderCard(_theme); _externalRows[other.Provider] = card; }
                card.Update(other);
            }
            var ordered = state.OrderedAccounts;
            for (var position = 0; position < ordered.Count; position++)
            {
                var account = ordered[position];
                W.UIElement card = account.Provider == ProviderIds.Codex ? _rows[account.Name] : _externalRows[account.Provider];
                if (stack.Children.IndexOf(card) != position) { stack.Children.Remove(card); stack.Children.Insert(position, card); }
            }
            foreach (var provider in _service.CurrentProviders) _providerToggles[provider.Id].IsChecked = provider.Enabled;
            Find<C.CheckBox>("StrictToggle").IsChecked = state.StrictVerification;
            Find<C.CheckBox>("StartupToggle").IsChecked = _service.StartupShortcutExists();
            UpdateFooter(state);
            if (_settingsVisible) UpdateOrderList();
            Find<C.TextBlock>("CountLabel").Text = $"{state.Profiles.Count + (state.OtherAccounts?.Count ?? 0)} cuentas";
            Find<C.TextBlock>("SettingsSummary").Text = state.SettingsSummary;
            UpdateViews();
        }
        finally { _applying = false; }
    }
    internal void ShowSettings(bool show) { _settingsVisible = show; if (show) UpdateOrderList(); UpdateViews(); }
    private void UpdateFooter(DashboardState state)
    {
        var total = state.Profiles.Count + (state.OtherAccounts?.Count ?? 0);
        var fresh = state.Profiles.Count(p => p.Status == "ok") + (state.OtherAccounts?.Count(p => p.Fresh) ?? 0);
        Find<C.TextBlock>("FooterLabel").Text = total == 0 ? "Sin cuentas guardadas" : $"{fresh}/{total} cuentas al día";
        Find<C.TextBlock>("FooterUpdated").Text = state.UpdatedLabel ?? "Sin lecturas recientes";
        var resets = state.Profiles.SelectMany(p => new[] { (p.Name, Window: p.SessionWindow), (p.Name, Window: p.WeeklyWindow) })
            .Concat((state.OtherAccounts ?? []).SelectMany(a => a.Quotas.Select(q => (a.Name, Window: (UsageWindow?)q.Window))))
            .Where(p => p.Window?.ResetsAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && p.Window.ResetsAt < DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            .OrderBy(p => p.Window!.ResetsAt).ToArray();
        var next = Find<C.TextBlock>("FooterReset");
        next.Text = resets.Length > 0 ? UsagePresentation.Duration(resets[0].Window!.ResetsAt!.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : "—";
    }
    private void UpdateOrderList()
    {
        var list = Find<C.StackPanel>("OrderList"); list.Children.Clear();
        if (_state is null) return;
        var accounts = _state.OrderedAccounts;
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var name = account.Provider == ProviderIds.Codex ? account.Name : ProviderIds.Name(account.Provider) + " · " + account.Name;
            var row = new C.Grid { Margin = new W.Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new C.ColumnDefinition()); row.ColumnDefinitions.Add(new C.ColumnDefinition { Width = W.GridLength.Auto }); row.ColumnDefinitions.Add(new C.ColumnDefinition { Width = W.GridLength.Auto });
            row.Children.Add(new C.TextBlock { Text = name, Margin = new W.Thickness(30, 0, 5, 0), VerticalAlignment = W.VerticalAlignment.Center, TextTrimming = W.TextTrimming.CharacterEllipsis });
            for (var direction = -1; direction <= 1; direction += 2)
            {
                var step = direction;
                var button = new C.Button { Content = direction < 0 ? "↑" : "↓", Width = 28, MinHeight = 26, Padding = new W.Thickness(0), IsEnabled = !_busy && index + direction >= 0 && index + direction < accounts.Count };
                W.Automation.AutomationProperties.SetName(button, (direction < 0 ? "Subir " : "Bajar ") + name);
                button.Click += async (_, _) => await MoveAccountAsync(account.Id, step);
                C.Grid.SetColumn(button, direction < 0 ? 1 : 2); row.Children.Add(button);
            }
            var logo = ProviderIcons.Create(account.Provider, _theme); logo.HorizontalAlignment = W.HorizontalAlignment.Left; row.Children.Add(logo);
            list.Children.Add(row);
        }
    }
    private async Task MoveAccountAsync(string name, int direction)
    {
        if (_busy || _state is null) return;
        var names = _state.OrderedAccounts.Select(p => p.Id).ToArray();
        var index = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        var next = index + direction;
        if (index < 0 || next < 0 || next >= names.Length) return;
        (names[index], names[next]) = (names[next], names[index]);
        await RunAsync(async () => { await _service.SetAccountOrderAsync(names); return "Orden guardado."; });
    }
    private void UpdateViews()
    {
        Find<C.ScrollViewer>("AccountsScroll").Visibility = !_settingsVisible ? W.Visibility.Visible : W.Visibility.Collapsed;
        Find<C.StackPanel>("EmptyPanel").Visibility = !_settingsVisible && _rows.Count == 0 && _externalRows.Count == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        Find<C.ScrollViewer>("SettingsScroll").Visibility = _settingsVisible ? W.Visibility.Visible : W.Visibility.Collapsed;
        Find<C.TextBlock>("SectionLabel").Text = _settingsVisible ? "PREFERENCIAS" : "ACTIVIDAD Y CUENTAS";
        Find<C.TextBlock>("CountLabel").Visibility = _settingsVisible ? W.Visibility.Collapsed : W.Visibility.Visible;
        Find<C.Button>("SettingsButton").Content = _settingsVisible ? "\uE72B" : "\uE713";
        W.Automation.AutomationProperties.SetName(Find<C.Button>("SettingsButton"), _settingsVisible ? "Volver" : "Ajustes");
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var name in new[] { "AddButton", "RefreshButton", "FileModeButton", "StartupToggle", "StrictToggle" }) Find<C.Control>(name).IsEnabled = !busy;
        foreach (var toggle in _providerToggles.Values) toggle.IsEnabled = !busy;
        foreach (var row in _rows.Values) row.SetBusy(busy);
        Find<C.StackPanel>("OrderList").IsEnabled = !busy;
    }
    internal void ShowRefreshError()
    {
        if (IsDisposed) return;
        // Reuse the footer; keep the last displayed quotas and allow a manual retry.
        Find<C.TextBlock>("FooterLabel").Text = "No se pudo actualizar";
        Find<C.TextBlock>("FooterUpdated").Text = "Pulsa ↻ para reintentar";
    }
    private void ReportRefreshFailure(string operation, Exception ex)
    {
        _service.Diagnostics.Write(operation, ex);
        try { ShowRefreshError(); }
        catch (Exception displayError) { _service.Diagnostics.Write("widget.error-display", displayError); }
    }
    private void RefreshState()
    {
        if (IsDisposed) return;
        try { ApplyState(_service.BuildDashboardState()); }
        catch (Exception ex) { ReportRefreshFailure("widget.apply-state", ex); }
    }
    private async Task RunAsync(Func<Task<string>> action, bool showDialogOnError = true)
    {
        if (_busy || IsDisposed) return;
        Exception? failure = null;
        try { SetBusy(true); await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            _service.Diagnostics.Write("widget.operation", ex);
            if (showDialogOnError && !IsDisposed)
            {
                try { ShowError(ex.Message); }
                catch (Exception displayError) { ReportRefreshFailure("widget.error-dialog", displayError); }
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                try { SetBusy(false); }
                catch (Exception ex) { ReportRefreshFailure("widget.restore-controls", ex); }
                RefreshState();
                if (failure is not null) ReportRefreshFailure("widget.operation-failed", failure);
            }
        }
    }
    private async Task RefreshAsync() => await RunAsync(async () =>
    {
        var history = UpdateHistoryAsync();
        try { return await _service.RefreshAllUsageAsync(null, _lifetime.Token); }
        finally { await history; }
    }, showDialogOnError: false);
    private void UpdateHistoryPanels(TokenHistorySnapshot snapshot) { _historyPanel.Update(snapshot); _overview.Update(snapshot); }
    internal void SetHistoryPreview(TokenHistorySnapshot snapshot) => UpdateHistoryPanels(snapshot);
    private async Task UpdateHistoryAsync()
    {
        if (_historyReading || IsDisposed) return;
        _historyReading = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _historyCancellation = cancellation;
        try
        {
            var snapshot = await _service.RefreshHistoryAsync(cancellation.Token);
            if (!IsDisposed) { UpdateHistoryPanels(snapshot); if (Visible && snapshot.CatchingUp) _historyTimer.Start(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _service.Diagnostics.Write("widget.history", ex);
            try { if (!IsDisposed) UpdateHistoryPanels(_service.History with { Partial = true, Error = "No se pudo leer el historial local." }); }
            catch (Exception displayError) { ReportRefreshFailure("widget.history-error-display", displayError); }
        }
        finally { _historyReading = false; _historyCancellation = null; if (!IsDisposed && Visible && cancellation.IsCancellationRequested) _historyTimer.Start(); }
    }
    private async Task AddAsync()
    {
        if (_busy) return;
        var name = AskName("Añadir cuenta", "Nombre para la sesión actual", "Nueva cuenta"); if (name is null) return;
        await RunAsync(async () => { await _service.AddCurrentProfileAsync(name); return "Cuenta guardada."; });
    }
    private async Task RenameAsync(string name)
    {
        if (_busy) return;
        var next = AskName("Renombrar cuenta", "Nombre del perfil", name); if (next is null || next == name) return;
        await RunAsync(async () => { await _service.RenameProfileAsync(name, next); return "Nombre actualizado."; });
    }
    private async Task DeleteAsync(string name)
    {
        if (_busy || !Confirm("Eliminar perfil", $"Se eliminará «{name}» de AICountsManager y se conservará una copia local. Tu cuenta de OpenAI no se eliminará.", "Eliminar")) return;
        await RunAsync(async () => { await _service.DeleteProfileAsync(name); return "Perfil eliminado. Copia local conservada."; });
    }
    private async Task SwitchAsync(string name)
    {
        if (_busy) return;
        var detail = _service.IsCodexRunning() ? "Se guardará la sesión actual, Codex se cerrará normalmente y volverá a abrirse con esta cuenta." : "Se guardará la sesión actual y se activará esta cuenta. Codex permanecerá cerrado.";
        if (!Confirm("Cambiar de cuenta", $"Vas a activar «{name}».\n\n{detail}", "Activar")) return;
        await RunAsync(async () =>
        {
            var result = await _service.SwitchToProfileAsync(name);
            if (result.Status == SwitchProfileStatus.RequiresReauthentication && Confirm("Inicia sesión de nuevo", "Se ha restablecido el estado anterior. Puedes abrir Codex con esta cuenta para iniciar sesión de nuevo.", "Iniciar sesión")) result = await _service.PrepareReauthenticationAsync(name);
            if (result.Status != SwitchProfileStatus.Success) throw new InvalidOperationException(result.Message);
            return result.Message;
        });
    }
    private void ShowError(string text)
    {
        if (!IsDisposed) Dialog("No se pudo completar", text, "Cerrar", null, allowCancel: false);
    }
    private string? AskName(string title, string prompt, string initial)
    {
        var input = new C.TextBox { Text = initial, MaxLength = 64, Margin = new W.Thickness(0, 12, 0, 16) };
        W.Automation.AutomationProperties.SetName(input, prompt);
        return Dialog(title, prompt, "Guardar", input) && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }
    private bool Confirm(string title, string text, string action) => Dialog(title, text, action, null);
    private bool Dialog(string title, string text, string action, C.TextBox? input, bool allowCancel = true)
    {
        _dialogOpen = true;
        var dialog = new W.Window { Owner = _window, Title = title, Width = 380, SizeToContent = W.SizeToContent.Height, WindowStartupLocation = W.WindowStartupLocation.CenterOwner,
            WindowStyle = W.WindowStyle.None, ResizeMode = W.ResizeMode.NoResize, ShowInTaskbar = false, Background = M.Brushes.Transparent, Foreground = UiTheme.Brush(_theme.Text), FontFamily = new M.FontFamily("Segoe UI"), FontSize = 12 };
        dialog.Resources.MergedDictionaries.Add(_window.Resources);
        var content = new C.StackPanel { Margin = new W.Thickness(22) };
        content.Children.Add(new C.TextBlock { Text = title, FontSize = 17, FontWeight = W.FontWeights.SemiBold, Margin = new W.Thickness(0, 0, 0, 12) });
        content.Children.Add(new C.TextBlock { Text = text, TextWrapping = W.TextWrapping.Wrap, Foreground = UiTheme.Brush(_theme.Muted), Margin = new W.Thickness(0, 0, 0, 16) });
        if (input is not null) content.Children.Add(input);
        var buttons = new C.StackPanel { Orientation = C.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right };
        var cancel = new C.Button { Content = "Cancelar", IsCancel = true, Margin = new W.Thickness(0, 0, 8, 0) };
        var confirm = new C.Button { Content = action, IsDefault = true, Background = UiTheme.Brush(_theme.Accent, 35), BorderThickness = new W.Thickness(1) };
        cancel.Click += (_, _) => dialog.DialogResult = false; confirm.Click += (_, _) => dialog.DialogResult = true;
        if (allowCancel) buttons.Children.Add(cancel); buttons.Children.Add(confirm); content.Children.Add(buttons);
        var border = new C.Border { CornerRadius = new W.CornerRadius(14), BorderThickness = new W.Thickness(1), BorderBrush = UiTheme.Brush(_theme.Text, 30), Background = UiTheme.Brush(_theme.Base), Child = content };
        dialog.Content = border;
        dialog.SourceInitialized += (_, _) =>
        {
            var handle = new Interop.WindowInteropHelper(dialog).Handle;
            var acrylic = WindowMaterial.Apply(handle, _theme.Dark, _theme.Transparency && !_theme.HighContrast);
            if (acrylic) { Interop.HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = M.Colors.Transparent; border.Background = UiTheme.Brush(_theme.Base, 110); }
        };
        dialog.Loaded += (_, _) => { if (input is not null) { input.Focus(); input.SelectAll(); } };
        try { return dialog.ShowDialog() == true; }
        finally { dialog.Close(); _dialogOpen = false; }
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; _addMenu.IsOpen = false; _addMenu.Items.Clear(); _lifetime.Cancel(); _historyTimer.Stop(); _clock.Stop(); _source?.RemoveHook(WindowMessage);
        foreach (var row in _rows.Values) row.Dispose(); _rows.Clear(); _window.Close(); _lifetime.Dispose();
    }
}

internal sealed class AccountRow : C.Border, IDisposable
{
    private UiTheme _theme;
    private ProfileState? _profile;
    private readonly C.ContentControl _logo = new();
    private readonly C.TextBlock _plan = Text(9.5, true);
    private readonly C.Border _planBadge = new() { CornerRadius = new W.CornerRadius(4), Padding = new W.Thickness(5, 2, 5, 2), Margin = new W.Thickness(6, 0, 0, 0), VerticalAlignment = W.VerticalAlignment.Center };
    private readonly C.StackPanel[] _quotaSections = [new(), new()];
    internal double EstimatedHeight => _profile?.SessionLimitNotApplicable == true ? 122 : 174;
    private readonly C.TextBlock _name = Text(13, true), _email = Text(10), _badge = Text(10, true), _updated = Text(9.5), _notice = Text(10);
    private readonly C.Button _activate = new() { Content = "Activar", FontSize = 10, Padding = new W.Thickness(9, 3, 9, 3), MinHeight = 24 }, _more = new() { Content = "\uE712", FontFamily = new M.FontFamily("Segoe Fluent Icons"), Width = 24, MinHeight = 24, Padding = new W.Thickness(0) };
    private readonly C.ContextMenu _menu = new();
    private readonly C.MenuItem _rename = new() { Header = "Renombrar" }, _delete = new() { Header = "Eliminar perfil" };
    private readonly C.Border[] _tracks = [new(), new()], _fills = [new(), new()];
    private readonly C.Border[] _markers = [new(), new()];
    private readonly C.TextBlock[] _forecast = [Text(9.5), Text(9.5)];
    private readonly C.TextBlock[] _percent = [Text(12, true), Text(12, true)], _reset = [Text(9.5), Text(9.5)];
    public bool IsDisposed { get; private set; }
    public bool MenuOpen => _menu.IsOpen;
    public event Func<string, Task>? ActivateRequested, RenameRequested, DeleteRequested;
    public AccountRow(UiTheme theme)
    {
        _theme = theme; CornerRadius = new W.CornerRadius(11); Padding = new W.Thickness(12, 10, 12, 11); Margin = new W.Thickness(0, 0, 0, 8); BorderThickness = new W.Thickness(1);
        var stack = new C.StackPanel(); Child = stack;
        var header = new C.Grid { Margin = new W.Thickness(0, 0, 0, 9) };
        header.ColumnDefinitions.Add(new C.ColumnDefinition()); header.ColumnDefinitions.Add(new C.ColumnDefinition { Width = W.GridLength.Auto }); header.ColumnDefinitions.Add(new C.ColumnDefinition { Width = W.GridLength.Auto });
        var identity = new C.StackPanel();
        var title = new C.Grid { HorizontalAlignment = W.HorizontalAlignment.Left };
        title.ColumnDefinitions.Add(new C.ColumnDefinition());
        title.ColumnDefinitions.Add(new C.ColumnDefinition { Width = W.GridLength.Auto });
        _name.VerticalAlignment = W.VerticalAlignment.Center; title.Children.Add(_name);
        _planBadge.Child = _plan; C.Grid.SetColumn(_planBadge, 1); title.Children.Add(_planBadge);
        identity.Children.Add(title); identity.Children.Add(_email); _email.Margin = new W.Thickness(0, 2, 0, 0);
        var identityWithLogo = new C.DockPanel(); identityWithLogo.Children.Add(_logo); identityWithLogo.Children.Add(identity);
        header.Children.Add(identityWithLogo);
        var status = new C.StackPanel { Margin = new W.Thickness(6, 0, 9, 0), VerticalAlignment = W.VerticalAlignment.Center };
        _badge.HorizontalAlignment = _updated.HorizontalAlignment = W.HorizontalAlignment.Right; status.Children.Add(_badge); status.Children.Add(_updated); status.Children.Add(_activate);
        C.Grid.SetColumn(status, 1); header.Children.Add(status); C.Grid.SetColumn(_more, 2); header.Children.Add(_more); stack.Children.Add(header);
        _activate.Click += async (_, _) => { if (_profile is { } p && ActivateRequested is { } action) await action(p.Name); };
        _more.Click += (_, _) => { _menu.PlacementTarget = _more; _menu.IsOpen = true; };
        W.Automation.AutomationProperties.SetName(_more, "Opciones de la cuenta");
        _rename.Click += async (_, _) => { if (_profile is { } p && RenameRequested is { } action) await action(p.Name); };
        _delete.Click += async (_, _) => { if (_profile is { } p && DeleteRequested is { } action) await action(p.Name); };
        AccountMenuStyle.Apply(_menu);
        _menu.Items.Add(_rename); _menu.Items.Add(_delete);
        for (var i = 0; i < 2; i++)
        {
            var section = _quotaSections[i]; stack.Children.Add(section);
            var line = new C.DockPanel { Margin = new W.Thickness(0, 0, 0, 6) };
            _forecast[i].HorizontalAlignment = W.HorizontalAlignment.Right; C.DockPanel.SetDock(_forecast[i], C.Dock.Right); line.Children.Add(_forecast[i]);
            var caption = Text(11, true); caption.Text = i == 0 ? "Sesión · 5 h" : "Semanal"; line.Children.Add(caption); section.Children.Add(line);
            _tracks[i].Height = 4; _tracks[i].CornerRadius = new W.CornerRadius(2);
            _fills[i].CornerRadius = new W.CornerRadius(2); _fills[i].HorizontalAlignment = W.HorizontalAlignment.Left;
            var bar = new C.Grid(); bar.Children.Add(_fills[i]);
            _markers[i].Width = 2; _markers[i].Height = 8; _markers[i].VerticalAlignment = W.VerticalAlignment.Center; _markers[i].HorizontalAlignment = W.HorizontalAlignment.Left;
            bar.Children.Add(_markers[i]); _tracks[i].Child = bar;
            var index = i; _tracks[i].SizeChanged += (_, _) => SetFill(index); section.Children.Add(_tracks[i]);
            var footer = new C.DockPanel { Margin = new W.Thickness(0, 5, 0, 0) };
            _reset[i].HorizontalAlignment = W.HorizontalAlignment.Right; C.DockPanel.SetDock(_reset[i], C.Dock.Right); footer.Children.Add(_reset[i]);
            _percent[i].FontSize = 10; _percent[i].FontWeight = W.FontWeights.Normal; footer.Children.Add(_percent[i]); section.Children.Add(footer);
        }
        _notice.Margin = new W.Thickness(0, 7, 0, 0); _notice.Visibility = W.Visibility.Collapsed; stack.Children.Add(_notice);
        SetTheme(theme);
    }
    private static C.TextBlock Text(double size, bool bold = false) => new() { FontSize = size, FontWeight = bold ? W.FontWeights.SemiBold : W.FontWeights.Normal, TextTrimming = W.TextTrimming.CharacterEllipsis };
    public void SetTheme(UiTheme theme)
    {
        _theme = theme; _logo.Content = ProviderIcons.Create(ProviderIds.Codex, theme); _email.Foreground = _updated.Foreground = UiTheme.Brush(theme.Muted); _notice.Foreground = UiTheme.Brush(theme.Warning);
        theme.Apply(_menu.Resources, false);
        _menu.Background = UiTheme.Brush(theme.Base); _menu.Foreground = UiTheme.Brush(theme.Text);
        foreach (var text in _reset) text.Foreground = UiTheme.Brush(theme.Muted);
        foreach (var track in _tracks) track.Background = UiTheme.Brush(theme.Text, theme.Dark ? (byte)26 : (byte)22);
        if (_profile is not null) Update(_profile, !_more.IsEnabled);
    }
    public void Update(ProfileState p, bool busy)
    {
        _profile = p; W.Automation.AutomationProperties.SetName(this, "Cuenta " + p.Name);
        _plan.Text = AccountPlan.Label(p.PlanType) ?? "";
        _planBadge.Visibility = _plan.Text.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        _plan.Foreground = UiTheme.Brush(_theme.Accent);
        _planBadge.Background = UiTheme.Brush(_theme.Accent, _theme.HighContrast ? (byte)0 : (byte)20);
        _quotaSections[0].Visibility = p.SessionLimitNotApplicable ? W.Visibility.Collapsed : W.Visibility.Visible;
        _quotaSections[1].Margin = new W.Thickness(0, p.SessionLimitNotApplicable ? 0 : 10, 0, 0);
        _name.Text = p.Name; _email.Text = p.Email ?? "Perfil local";
        _badge.Text = "● Activa"; _badge.Foreground = UiTheme.Brush(_theme.Accent); _badge.Visibility = p.IsActive ? W.Visibility.Visible : W.Visibility.Collapsed;
        _updated.Text = p.UpdatedLabel; _updated.Visibility = p.IsActive ? W.Visibility.Visible : W.Visibility.Collapsed;
        _activate.Visibility = p.IsActive ? W.Visibility.Collapsed : W.Visibility.Visible;
        W.Automation.AutomationProperties.SetName(_activate, "Activar " + p.Name); _delete.IsEnabled = !p.IsActive;
        Background = UiTheme.Brush(_theme.Dark ? M.Colors.White : M.Colors.Black, p.IsActive ? (byte)17 : (byte)10);
        BorderBrush = UiTheme.Brush(p.IsActive ? _theme.Accent : _theme.Text, p.IsActive ? (byte)35 : (byte)0);
        for (var i = 0; i < 2; i++) SetFill(i);
        _notice.Visibility = p.Status is "error" or "stale" ? W.Visibility.Visible : W.Visibility.Collapsed;
        _notice.Text = p.Status == "error" ? "No se pudo actualizar · " + p.Error : "Datos antiguos · " + p.UpdatedLabel;
        SetBusy(busy);
    }
    private void SetFill(int index)
    {
        var remaining = index == 0 ? _profile?.SessionRemainingPercent : _profile?.WeeklyRemainingPercent;
        var window = index == 0 ? _profile?.SessionWindow : _profile?.WeeklyWindow;
        var display = QuotaDisplay.Create(window, remaining, _profile?.Status == "ok", DateTimeOffset.UtcNow);
        _percent[index].Text = display.RemainingText + (remaining.HasValue ? " restante" : "");
        _reset[index].Text = display.ResetText ?? (index == 0 ? _profile?.SessionResetLabel : _profile?.WeeklyResetLabel) ?? "Sin fecha";
        _fills[index].Width = display.FillWidth(_tracks[index].ActualWidth);
        var color = index == 0 ? _theme.Accent : _theme.WeeklyAccent;
        _fills[index].Background = _percent[index].Foreground = UiTheme.Brush(color);
        _markers[index].Visibility = display.Reference is null ? W.Visibility.Collapsed : W.Visibility.Visible;
        if (display.Reference is not null) _markers[index].Margin = new W.Thickness(display.MarkerOffset(_tracks[index].ActualWidth), -2, 0, -2);
        _markers[index].Background = UiTheme.Brush(_theme.Text, 170);
        _forecast[index].Text = display.ForecastText;
        _forecast[index].Foreground = UiTheme.Brush(display.AtRisk ? _theme.Warning : _theme.Muted);
    }

    public void SetBusy(bool busy) { _activate.IsEnabled = _more.IsEnabled = !busy; if (busy) _menu.IsOpen = false; }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; _menu.IsOpen = false; _menu.Items.Clear(); _menu.PlacementTarget = null;
        ActivateRequested = RenameRequested = DeleteRequested = null; Child = null;
    }
}

internal sealed record UiTheme(bool Dark, bool Transparency, bool HighContrast, M.Color? TaskbarColor = null, M.Color? SystemAccent = null)
{
    public M.Color Base => HighContrast ? W.SystemColors.WindowColor : TaskbarColor ?? (Dark ? M.Color.FromRgb(29, 30, 32) : M.Color.FromRgb(240, 242, 244));
    public M.Color Text => HighContrast ? W.SystemColors.WindowTextColor : Dark ? M.Color.FromRgb(243, 245, 247) : M.Color.FromRgb(28, 32, 37);
    public M.Color Muted => HighContrast ? Text : Dark ? M.Color.FromRgb(173, 182, 190) : M.Color.FromRgb(83, 95, 104);
    private M.Color RawAccent => SystemAccent ?? M.Color.FromRgb(0, 120, 212);
    private M.Color Shade(double mix) => M.Color.FromRgb((byte)(RawAccent.R * (1 - mix) + (Dark ? 255 : 0) * mix), (byte)(RawAccent.G * (1 - mix) + (Dark ? 255 : 0) * mix), (byte)(RawAccent.B * (1 - mix) + (Dark ? 255 : 0) * mix));
    public M.Color Accent => HighContrast ? W.SystemColors.HighlightColor : Shade(Dark ? .48 : .18);
    public M.Color WeeklyAccent => HighContrast ? W.SystemColors.HighlightColor : Shade(Dark ? .68 : .35);
    public M.Color Warning => HighContrast ? Text : Dark ? M.Color.FromRgb(231, 189, 116) : M.Color.FromRgb(144, 88, 18);
    public static M.SolidColorBrush Brush(M.Color color, byte alpha = 255)
    { var brush = new M.SolidColorBrush(M.Color.FromArgb(alpha, color.R, color.G, color.B)); brush.Freeze(); return brush; }
    public void Apply(W.ResourceDictionary resources, bool acrylic)
    {
        resources["Text"] = Brush(Text); resources["Muted"] = Brush(Muted); resources["Accent"] = Brush(Accent);
        resources["ToggleKnob"] = HighContrast ? W.SystemColors.HighlightTextBrush : Brush(Dark ? Base : M.Colors.White);
        resources["ShellTint"] = Brush(Base, acrylic ? (byte)95 : (byte)255);
        resources["Line"] = Brush(Text, HighContrast ? (byte)255 : (byte)30);
        resources["Hover"] = Brush(Text, 18); resources["Selected"] = Brush(Accent, 28); resources["ScrollThumb"] = Brush(Text, 55);
    }
    public static UiTheme Read()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // Taskbar theme can differ from the apps theme: intentionally use SystemUsesLightTheme.
            var dark = settings?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
            var transparent = settings?.GetValue("EnableTransparency") is int enabled && enabled == 1;
            M.Color? tint = null, systemAccent = null;
            using (var colors = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                if (colors?.GetValue("AccentColorMenu") is int color)
                    systemAccent = M.Color.FromRgb((byte)(color & 255), (byte)((color >> 8) & 255), (byte)((color >> 16) & 255));
            if (settings?.GetValue("ColorPrevalence") is int prevalence && prevalence == 1)
            {
                using var accent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (accent?.GetValue("AccentColorMenu") is int raw)
                {
                    // Explorer stores its accent as ABGR. Darken it to the taskbar material base.
                    tint = M.Color.FromRgb((byte)((raw & 255) * 0.55), (byte)(((raw >> 8) & 255) * 0.55), (byte)(((raw >> 16) & 255) * 0.55));
                }
            }
            return new UiTheme(dark, transparent, W.SystemParameters.HighContrast, tint, systemAccent);
        }
        catch { return new UiTheme(true, false, W.SystemParameters.HighContrast); }
    }
}

internal static class WindowMaterial
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins margins);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    public static uint Dpi(IntPtr h) { try { return Math.Max(96, GetDpiForWindow(h)); } catch (EntryPointNotFoundException) { return 96; } }
    public static void Position(IntPtr h, int x, int y) => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    public static bool Apply(IntPtr h, bool dark, bool transparency)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        try
        {
            var d = dark ? 1 : 0; var corners = 2;
            DwmSetWindowAttribute(h, 20, ref d, 4); DwmSetWindowAttribute(h, 33, ref corners, 4);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return false;
            var backdrop = transparency ? 3 : 1;
            var ok = DwmSetWindowAttribute(h, 38, ref backdrop, 4) == 0;
            var margins = ok && transparency ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Margins();
            if (DwmExtendFrameIntoClientArea(h, ref margins) == 0 && ok && transparency) return true;
            backdrop = 1; DwmSetWindowAttribute(h, 38, ref backdrop, 4);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return false;
    }
}
