using System.Drawing.Drawing2D;
using System.ComponentModel;
using Microsoft.Win32;

namespace CodexManager;

internal sealed class WidgetForm : Form
{
    private static readonly Color ThemeAccent = TryReadWindowsAccentColor() ?? Color.FromArgb(65, 123, 255);
    private static readonly Color ShellBackground = BuildShellBackground();
    private static readonly Color CardBackground = BuildCardBackground();
    private static readonly Color SurfaceBackground = Color.FromArgb(255, 255, 255);
    private static readonly Color TextColor = Color.FromArgb(36, 30, 53);
    private static readonly Color MutedTextColor = Color.FromArgb(108, 93, 126);
    private static readonly Color AccentColor = ThemeAccent;
    private static readonly Color SecondaryAccentColor = Color.FromArgb(145, 118, 255);
    private static readonly Color SuccessColor = Color.FromArgb(45, 212, 171);

    private readonly CodexManagerService _service;
    private readonly FlowLayoutPanel _accountCards;
    private readonly Panel _mainView;
    private readonly Panel _settingsView;
    private readonly Label _settingsSummary;
    private readonly FlowLayoutPanel _settingsAccountList;
    private readonly CheckBox _strictVerificationToggle;
    private readonly Button _backButton;
    private readonly Label _statusMessage;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private bool _modalDialogOpen;
    private bool _applyingState;
    private bool _switchInProgress;

    public event EventHandler? HideRequested;

    public WidgetForm(CodexManagerService service)
    {
        _service = service;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(430, 620);
        MinimumSize = Size;
        MaximumSize = new Size(430, 720);
        DoubleBuffered = true;
        TopMost = true;
        Padding = new Padding(18);
        BackColor = ShellBackground;

        _mainView = BuildMainView();
        _settingsView = BuildSettingsView();

        _accountCards = new FlowLayoutPanel();
        _settingsSummary = new Label();
        _settingsAccountList = new FlowLayoutPanel();
        _strictVerificationToggle = new CheckBox();
        _backButton = new ChromeButton();
        _statusMessage = new Label();
        _statusTimer = new System.Windows.Forms.Timer();

        BuildMainContent();
        BuildSettingsContent();
        BuildStatusMessage();

        Controls.Add(_mainView);
        Controls.Add(_settingsView);
        _settingsView.Visible = false;

        Deactivate += (_, _) =>
        {
            if (!_modalDialogOpen)
            {
                HideRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;
            return cp;
        }
    }

    public void ShowNearTray()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
        ShowMain();
        Show();
        BringToFront();
    }

    public void ApplyState(DashboardState state)
    {
        _applyingState = true;
        try
        {
            _accountCards.SuspendLayout();
            _accountCards.Controls.Clear();
            foreach (var profile in state.Profiles)
            {
                _accountCards.Controls.Add(BuildAccountCard(profile));
            }
            _accountCards.ResumeLayout();

            _settingsSummary.Text = state.SettingsSummary;
            _strictVerificationToggle.Checked = state.StrictVerification;
            _settingsAccountList.SuspendLayout();
            _settingsAccountList.Controls.Clear();
            foreach (var profile in state.Profiles)
            {
                _settingsAccountList.Controls.Add(BuildSettingsProfileRow(profile));
            }
            _settingsAccountList.ResumeLayout();
        }
        finally
        {
            _applyingState = false;
        }
    }

    private Panel BuildMainView()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ShellBackground,
        };
    }

    private Panel BuildSettingsView()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ShellBackground,
        };
    }

    private void BuildMainContent()
    {
        var card = BuildGlassCard();
        card.Dock = DockStyle.Fill;
        _mainView.Controls.Add(card);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = CardBackground,
            Padding = new Padding(14, 16, 14, 16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(layout);

        _accountCards.FlowDirection = FlowDirection.TopDown;
        _accountCards.WrapContents = false;
        _accountCards.AutoScroll = true;
        _accountCards.Dock = DockStyle.Fill;
        _accountCards.BackColor = CardBackground;
        _accountCards.Margin = new Padding(0);
        _accountCards.Padding = new Padding(0, 0, 4, 0);
        layout.Controls.Add(_accountCards);

        var footer = new TableLayoutPanel
        {
            Height = 46,
            BackColor = CardBackground,
            Dock = DockStyle.Bottom,
            ColumnCount = 3,
            Margin = new Padding(0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var refreshButton = BuildIconButton("\uE72C", async (_, _) => await RefreshNowAsync());
        refreshButton.Margin = new Padding(0, 4, 0, 4);
        footer.Controls.Add(refreshButton, 0, 0);

        _statusMessage.Dock = DockStyle.Fill;
        _statusMessage.Margin = new Padding(8, 10, 8, 8);
        footer.Controls.Add(_statusMessage, 1, 0);

        var settingsButton = BuildIconButton("\uE713", (_, _) => ShowSettings());
        settingsButton.Margin = new Padding(0, 4, 0, 4);
        footer.Controls.Add(settingsButton, 2, 0);
        layout.Controls.Add(footer);
    }

    private void BuildSettingsContent()
    {
        var card = BuildGlassCard();
        card.Dock = DockStyle.Fill;
        _settingsView.Controls.Add(card);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = CardBackground,
            Padding = new Padding(18, 18, 18, 18),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.Controls.Add(layout);

        var title = new Label
        {
            Text = "Settings",
            Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold),
            ForeColor = TextColor,
            BackColor = CardBackground,
            AutoSize = true
        };
        layout.Controls.Add(title);

        _settingsSummary.Font = new Font("Segoe UI", 9.5f);
        _settingsSummary.ForeColor = MutedTextColor;
        _settingsSummary.BackColor = CardBackground;
        _settingsSummary.MaximumSize = new Size(320, 0);
        _settingsSummary.AutoSize = true;
        layout.Controls.Add(_settingsSummary);

        _strictVerificationToggle.Text = "Verificar la renovacion antes de abrir Codex";
        _strictVerificationToggle.Font = new Font("Segoe UI", 9f);
        _strictVerificationToggle.ForeColor = TextColor;
        _strictVerificationToggle.BackColor = CardBackground;
        _strictVerificationToggle.AutoSize = true;
        _strictVerificationToggle.Margin = new Padding(0, 8, 0, 2);
        _strictVerificationToggle.CheckedChanged += (_, _) =>
        {
            if (_applyingState)
            {
                return;
            }
            RunAction(
                () => _service.SetStrictVerification(_strictVerificationToggle.Checked),
                _strictVerificationToggle.Checked
                    ? "Verificacion estricta activada."
                    : "Modo rapido activado.");
        };
        layout.Controls.Add(_strictVerificationToggle);

        var toolbar = new TableLayoutPanel
        {
            Height = 42,
            ColumnCount = 2,
            BackColor = CardBackground,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 8)
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var addCurrentButton = BuildGhostButton("Add current account", (_, _) => AddCurrentAccount());
        addCurrentButton.Width = 182;
        addCurrentButton.Height = 34;
        addCurrentButton.TextAlign = ContentAlignment.MiddleCenter;
        addCurrentButton.Margin = new Padding(0, 0, 8, 0);
        toolbar.Controls.Add(addCurrentButton, 0, 0);

        var dataButton = BuildGhostButton("Open data folder", (_, _) => _service.OpenFolder(_service.BuildDashboardState().DataFolder));
        dataButton.Width = 148;
        dataButton.Height = 34;
        dataButton.TextAlign = ContentAlignment.MiddleCenter;
        dataButton.Margin = new Padding(0);
        toolbar.Controls.Add(dataButton, 1, 0);
        layout.Controls.Add(toolbar);

        _settingsAccountList.FlowDirection = FlowDirection.TopDown;
        _settingsAccountList.WrapContents = false;
        _settingsAccountList.AutoScroll = true;
        _settingsAccountList.Dock = DockStyle.Fill;
        _settingsAccountList.BackColor = CardBackground;
        _settingsAccountList.Margin = new Padding(0, 6, 0, 10);
        _settingsAccountList.Padding = new Padding(2, 0, 2, 0);
        layout.Controls.Add(_settingsAccountList);

        _backButton.Text = "Back";
        _backButton.Width = 120;
        _backButton.Height = 38;
        _backButton.BackColor = AccentColor;
        _backButton.ForeColor = Color.White;
        _backButton.Click += (_, _) => ShowMain();
        layout.Controls.Add(_backButton);
    }

    private void BuildStatusMessage()
    {
        _statusMessage.Visible = false;
        _statusMessage.AutoSize = false;
        _statusMessage.Size = new Size(240, 24);
        _statusMessage.TextAlign = ContentAlignment.MiddleCenter;
        _statusMessage.Font = new Font("Segoe UI", 9f);
        _statusMessage.ForeColor = MutedTextColor;
        _statusMessage.BackColor = CardBackground;

        _statusTimer.Interval = 1800;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            _statusMessage.Visible = false;
        };
    }

    private Control BuildAccountCard(ProfileState profile)
    {
        var card = new RoundedPanel
        {
            Width = 364,
            Height = 108,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = SurfaceBackground,
            Radius = 18
        };

        var nameLabel = new Label
        {
            Text = profile.Name,
            Font = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold),
            ForeColor = TextColor,
            BackColor = SurfaceBackground,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(14, 10),
            Size = new Size(110, 24)
        };
        card.Controls.Add(nameLabel);

        var updatedLabel = new Label
        {
            Text = profile.UpdatedLabel,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = MutedTextColor,
            BackColor = SurfaceBackground,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(124, 12),
            Size = new Size(150, 20)
        };
        card.Controls.Add(updatedLabel);

        if (profile.IsActive)
        {
            var liveBadge = new Label
            {
                Text = "\u25CF In Use",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = SuccessColor,
                BackColor = SurfaceBackground,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(236, 10),
                Size = new Size(108, 24),
                Padding = new Padding(0)
            };
            card.Controls.Add(liveBadge);
        }
        else
        {
            var useButton = BuildMiniActionButton("\uE77B", async (_, _) => await ConfirmAndSwitchProfileAsync(profile.Name));
            useButton.Location = new Point(310, 10);
            useButton.Size = new Size(34, 24);
            card.Controls.Add(useButton);
        }

        card.Controls.Add(BuildUsageRow("Session", profile.SessionRemainingPercent, profile.SessionResetLabel, new Point(14, 42), false));
        card.Controls.Add(BuildUsageRow("Weekly", profile.WeeklyRemainingPercent, profile.WeeklyResetLabel, new Point(14, 68), true));
        return card;
    }

    private Control BuildUsageRow(string title, double? remainingPercent, string resetLabel, Point location, bool secondary)
    {
        var row = new Panel
        {
            Location = location,
            Size = new Size(336, 20),
            BackColor = SurfaceBackground
        };

        var titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = TextColor,
            BackColor = SurfaceBackground,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(0, 0),
            Size = new Size(62, 20)
        };
        row.Controls.Add(titleLabel);

        var bar = new UsageBar
        {
            Value = remainingPercent,
            Accent = secondary ? SecondaryAccentColor : Color.FromArgb(215, 138, 91),
            Location = new Point(66, 3),
            Size = new Size(128, 14)
        };
        row.Controls.Add(bar);

        var percentLabel = new Label
        {
            Text = RemainingPercentText(remainingPercent),
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = MutedTextColor,
            BackColor = SurfaceBackground,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(200, 0),
            Size = new Size(46, 20)
        };
        row.Controls.Add(percentLabel);

        var resetText = ShortResetText(resetLabel);
        var resetLabelControl = new Label
        {
            Text = resetText,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = MutedTextColor,
            BackColor = SurfaceBackground,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(248, 0),
            Size = new Size(88, 20)
        };
        row.Controls.Add(resetLabelControl);

        return row;
    }

    private Control BuildSettingsProfileRow(ProfileState profile)
    {
        var row = new RoundedPanel
        {
            Width = 344,
            Height = 52,
            Radius = 14,
            BackColor = SurfaceBackground,
            Margin = new Padding(5, 0, 0, 8)
        };

        var nameLabel = new Label
        {
            Text = profile.Name,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            ForeColor = TextColor,
            BackColor = SurfaceBackground,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(12, 14),
            Size = new Size(168, 24)
        };
        row.Controls.Add(nameLabel);

        var liveLabel = new Label
        {
            Text = profile.IsActive ? "\u25CF In Use" : string.Empty,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = SuccessColor,
            BackColor = SurfaceBackground,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(184, 16),
            Size = new Size(68, 20)
        };
        row.Controls.Add(liveLabel);

        var editButton = BuildMiniActionButton("\uE70F", (_, _) =>
        {
            var newName = ShowProfileNameDialog("Renombrar perfil", "Nombre del perfil", profile.Name);
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, profile.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RunAction(() => _service.RenameProfile(profile.Name, newName), "Perfil renombrado.");
        });
        editButton.Location = new Point(256, 12);
        row.Controls.Add(editButton);

        var deleteButton = BuildMiniActionButton("\uE74D", (_, _) =>
        {
            if (ShowConfirmationDialog("Eliminar perfil", $"Vas a eliminar el perfil '{profile.Name}'.\n\nEsta accion creara una copia de seguridad del auth.json antes de borrar el perfil.", "Eliminar"))
            {
                RunAction(() => _service.DeleteProfile(profile.Name), "Perfil eliminado.");
            }
        });
        deleteButton.Location = new Point(298, 12);
        row.Controls.Add(deleteButton);

        return row;
    }

    private Panel BuildGlassCard()
    {
        return new RoundedPanel
        {
            BackColor = CardBackground,
            Padding = new Padding(0),
            Radius = 24,
        };
    }

    private Button BuildGhostButton(string text, EventHandler onClick)
    {
        var button = new ChromeButton
        {
            Text = text,
            Width = 144,
            Height = 36,
            BackColor = Mix(ThemeAccent, Color.White, 0.76),
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 10f),
            CornerRadius = 14,
            BorderColor = Mix(ThemeAccent, Color.White, 0.48),
            HoverFillColor = Mix(ThemeAccent, Color.White, 0.68),
            PressedFillColor = Mix(ThemeAccent, Color.White, 0.60),
            Padding = new Padding(0)
        };
        button.Click += onClick;
        return button;
    }

    private Button BuildWideButton(string text, EventHandler onClick)
    {
        var button = BuildGhostButton(text, onClick);
        button.Width = 320;
        button.Height = 40;
        button.Margin = new Padding(0, 0, 0, 8);
        return button;
    }

    private Button BuildMiniActionButton(string text, EventHandler onClick)
    {
        var button = new ChromeButton
        {
            Text = text,
            Width = 34,
            Height = 28,
            BackColor = Color.Transparent,
            ForeColor = TextColor,
            Font = IsGlyphText(text) ? new Font("Segoe MDL2 Assets", 11f) : new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            DrawBackground = false,
            HoverFillColor = Color.FromArgb(18, ThemeAccent),
            PressedFillColor = Color.FromArgb(34, ThemeAccent),
            CornerRadius = 10,
            BorderColor = Color.Transparent,
            ContentOffsetY = IsGlyphText(text) ? 1.5f : 0f,
            Padding = new Padding(0)
        };
        button.Click += onClick;
        return button;
    }

    private Button BuildIconButton(string glyph, EventHandler onClick)
    {
        var button = new ChromeButton
        {
            Text = glyph,
            Width = 38,
            Height = 36,
            BackColor = Color.Transparent,
            ForeColor = TextColor,
            Font = new Font("Segoe MDL2 Assets", 13f),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            DrawBackground = false,
            HoverFillColor = Color.FromArgb(20, ThemeAccent),
            PressedFillColor = Color.FromArgb(38, ThemeAccent),
            CornerRadius = 12,
            BorderColor = Color.Transparent,
            ContentOffsetY = 1.5f,
            Padding = new Padding(0)
        };
        button.Click += onClick;
        return button;
    }

    private async Task RefreshNowAsync()
    {
        try
        {
            await _service.RefreshActiveProfileAsync();
            ApplyState(_service.BuildDashboardState());
            ShowStatus("Datos actualizados.");
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
    }

    private void AddCurrentAccount()
    {
        var name = ShowProfileNameDialog("Nuevo perfil", "Nombre del perfil", "Nuevo Perfil");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        RunAction(() => _service.AddCurrentProfile(name), "Perfil guardado.");
    }

    private void ShowSettings()
    {
        _mainView.Visible = false;
        _settingsView.Visible = true;
    }

    private void ShowMain()
    {
        _settingsView.Visible = false;
        _mainView.Visible = true;
    }

    private async Task ConfirmAndSwitchProfileAsync(string profileName)
    {
        if (_switchInProgress)
        {
            ShowStatus("Ya hay un cambio de cuenta en curso.");
            return;
        }

        var codexRunning = _service.IsCodexRunning();
        var message = codexRunning
            ? $"Vas a cambiar a la cuenta '{profileName}'.\n\nCodex se cerrara normalmente, se preservara la cuenta actual y se volvera a abrir con la nueva."
            : $"Vas a cambiar a la cuenta '{profileName}'.\n\nLa cuenta actual se preservara antes de instalar la nueva.";
        if (!ShowConfirmationDialog("Cambiar cuenta", message, "Cambiar"))
        {
            return;
        }

        _switchInProgress = true;
        ShowStatus("Cambiando cuenta...");
        try
        {
            var result = await _service.SwitchToProfileAsync(profileName);
            ApplyState(_service.BuildDashboardState());

            if (result.Status == SwitchProfileStatus.RequiresReauthentication)
            {
                var reauthenticate = ShowConfirmationDialog(
                    "Sesion caducada",
                    $"No se pudo renovar '{profileName}'. La cuenta anterior se ha restaurado.\n\nPuedes abrir Codex con '{profileName}' para iniciar sesion de nuevo, o cancelar para seguir con la cuenta anterior.",
                    "Reautenticar");
                if (reauthenticate)
                {
                    ShowStatus("Preparando reautenticacion...");
                    var reauthResult = await _service.PrepareReauthenticationAsync(profileName);
                    ApplyState(_service.BuildDashboardState());
                    ShowStatus(reauthResult.Message);
                }
                else
                {
                    ShowStatus("Se mantiene la cuenta anterior.");
                }
                return;
            }

            ShowStatus(result.Message);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Cambio cancelado; se ha conservado la cuenta anterior.");
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
        finally
        {
            _switchInProgress = false;
        }
    }

    private string? ShowProfileNameDialog(string title, string label, string initialValue)
    {
        _modalDialogOpen = true;
        try
        {
            return ProfileNameDialog.Show(this, title, label, initialValue);
        }
        finally
        {
            _modalDialogOpen = false;
        }
    }

    private bool ShowConfirmationDialog(string title, string message, string confirmText)
    {
        _modalDialogOpen = true;
        try
        {
            return AppConfirmationDialog.Show(this, title, message, confirmText);
        }
        finally
        {
            _modalDialogOpen = false;
        }
    }

    private void RunAction(Action action, string successMessage)
    {
        try
        {
            action();
            ApplyState(_service.BuildDashboardState());
            ShowStatus(successMessage);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message);
        }
    }

    private void ShowStatus(string message)
    {
        _statusMessage.Text = message;
        _statusMessage.Visible = true;
        _statusMessage.BringToFront();
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private static string RemainingPercentText(double? value) => value.HasValue ? $"{value.Value:0}%" : "N/D";

    private static string ShortResetText(string? resetLabel)
    {
        if (string.IsNullOrWhiteSpace(resetLabel) || string.Equals(resetLabel, "Sin reset", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (resetLabel.StartsWith("Resets in ", StringComparison.OrdinalIgnoreCase))
        {
            resetLabel = resetLabel["Resets in ".Length..];
        }

        return CompactDurationText(resetLabel);
    }

    private static string CompactDurationText(string value)
    {
        var text = value.Trim()
            .Replace(" days", "d")
            .Replace(" day", "d")
            .Replace(" hours", "h")
            .Replace(" hour", "h")
            .Replace(" minutes", "m")
            .Replace(" minute", "m")
            .Replace(" días", "d")
            .Replace(" día", "d")
            .Replace(" horas", "h")
            .Replace(" hora", "h")
            .Replace(" minutos", "m")
            .Replace(" minuto", "m")
            .Replace(" and ", " ")
            .Replace(" y ", " ");

        while (text.Contains("  "))
        {
            text = text.Replace("  ", " ");
        }

        return text;
    }

    private static Color BuildShellBackground()
    {
        return Mix(ThemeAccent, Color.White, 0.76);
    }

    private static Color BuildCardBackground()
    {
        return Mix(ThemeAccent, Color.White, 0.9);
    }

    private static Color? TryReadWindowsAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var raw = key?.GetValue("ColorizationColor");
            if (raw is int value)
            {
                var a = (value >> 24) & 0xFF;
                var r = (value >> 16) & 0xFF;
                var g = (value >> 8) & 0xFF;
                var b = value & 0xFF;
                return Color.FromArgb(a == 0 ? 255 : a, r, g, b);
            }
        }
        catch
        {
        }

        return null;
    }

    private static Color Mix(Color source, Color target, double targetWeight)
    {
        var sourceWeight = 1d - targetWeight;
        return Color.FromArgb(
            255,
            (int)Math.Round(source.R * sourceWeight + target.R * targetWeight),
            (int)Math.Round(source.G * sourceWeight + target.G * targetWeight),
            (int)Math.Round(source.B * sourceWeight + target.B * targetWeight));
    }

    private static bool IsGlyphText(string text) => text.Length == 1 && text[0] >= 0xE700;
}

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 18;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundPath(ClientRectangle, Radius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        Region = new Region(path);
        using var pen = new Pen(Color.FromArgb(220, 220, 214, 236), 1);
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundPath(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ChromeButton : Button
{
    private bool _hovered;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 12;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DrawBackground { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverFillColor { get; set; } = Color.FromArgb(18, 0, 0, 0);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressedFillColor { get; set; } = Color.FromArgb(28, 0, 0, 0);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float ContentOffsetY { get; set; }

    public ChromeButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Color.Transparent);

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = CreateRoundPath(rect, CornerRadius);

        if (DrawBackground)
        {
            var fillColor = _pressed ? PressedFillColor : _hovered ? HoverFillColor : BackColor;
            using var fill = new SolidBrush(fillColor);
            e.Graphics.FillPath(fill, path);
            if (BorderColor.A > 0)
            {
                using var border = new Pen(BorderColor, 1f);
                e.Graphics.DrawPath(border, path);
            }
        }
        else if (_hovered || _pressed)
        {
            var overlay = _pressed ? PressedFillColor : HoverFillColor;
            using var fill = new SolidBrush(overlay);
            e.Graphics.FillPath(fill, path);
        }

        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var brush = new SolidBrush(Enabled ? ForeColor : SystemColors.GrayText);
        var textRect = new RectangleF(rect.Left + 2f, rect.Top + ContentOffsetY, rect.Width - 4f, rect.Height);
        e.Graphics.DrawString(Text, Font, brush, textRect, format);
    }

    private static GraphicsPath CreateRoundPath(RectangleF bounds, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class UsageBar : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? Value { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; } = Color.FromArgb(215, 138, 91);

    public UsageBar()
    {
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = new SolidBrush(Color.FromArgb(226, 221, 237));
        using var fill = new SolidBrush(Accent);
        var rect = new RectangleF(0, 0, Width, Height);
        e.Graphics.FillRoundedRectangle(track, rect, Height / 2f);
        if (Value.HasValue && Value.Value > 0)
        {
            var fillRect = new RectangleF(0, 0, (float)(Width * (Value.Value / 100d)), Height);
            e.Graphics.FillRoundedRectangle(fill, fillRect, Height / 2f);
        }
    }
}

internal static class ProfileNameDialog
{
    public static string? Show(IWin32Window owner, string title, string label, string initialValue)
    {
        using var form = AppDialog.CreateShell(390, 238);
        var card = AppDialog.CreateCard(form);
        form.Controls.Add(card);

        card.Controls.Add(AppDialog.CreateTitle(title, 22, 20, 320));
        card.Controls.Add(AppDialog.CreateBodyLabel(label, 22, 64, 320, 22));

        var input = new TextBox
        {
            Text = initialValue,
            Font = new Font("Segoe UI", 10.5f),
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(22, 92),
            Size = new Size(326, 30),
        };
        card.Controls.Add(input);

        var cancel = AppDialog.CreateSecondaryButton("Cancelar", 152, 156, DialogResult.Cancel);
        var confirm = AppDialog.CreatePrimaryButton("Guardar", 252, 156, DialogResult.OK);
        cancel.Click += (_, _) => AppDialog.CloseWith(form, DialogResult.Cancel);
        confirm.Click += (_, _) => AppDialog.CloseWith(form, DialogResult.OK);
        card.Controls.AddRange([cancel, confirm]);

        form.AcceptButton = confirm;
        form.CancelButton = cancel;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                AppDialog.CloseWith(form, DialogResult.Cancel);
            }
        };
        form.Shown += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return AppDialog.ShowModal(form, owner) == DialogResult.OK ? input.Text.Trim() : null;
    }
}

internal static class AppConfirmationDialog
{
    public static bool Show(IWin32Window owner, string title, string message, string confirmText)
    {
        using var form = AppDialog.CreateShell(410, 268);
        var card = AppDialog.CreateCard(form);
        form.Controls.Add(card);

        card.Controls.Add(AppDialog.CreateIconBadge("\uE7BA", 22, 22));
        card.Controls.Add(AppDialog.CreateTitle(title, 68, 24, 280));
        card.Controls.Add(AppDialog.CreateBodyLabel(message, 24, 78, 340, 92));

        var cancel = AppDialog.CreateSecondaryButton("Cancelar", 162, 188, DialogResult.Cancel);
        var confirm = AppDialog.CreatePrimaryButton(confirmText, 264, 188, DialogResult.OK);
        cancel.Click += (_, _) => AppDialog.CloseWith(form, DialogResult.Cancel);
        confirm.Click += (_, _) => AppDialog.CloseWith(form, DialogResult.OK);
        card.Controls.AddRange([cancel, confirm]);

        form.AcceptButton = confirm;
        form.CancelButton = cancel;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                AppDialog.CloseWith(form, DialogResult.Cancel);
            }
        };
        return AppDialog.ShowModal(form, owner) == DialogResult.OK;
    }
}

internal static class AppDialog
{
    private static readonly Color AccentColor = WidgetFormAccent();
    private static readonly Color ShellBackground = Mix(AccentColor, Color.White, 0.76);
    private static readonly Color CardBackground = Mix(AccentColor, Color.White, 0.9);
    private static readonly Color SurfaceBackground = Color.FromArgb(255, 255, 255);
    private static readonly Color TextColor = Color.FromArgb(36, 30, 53);
    private static readonly Color MutedTextColor = Color.FromArgb(108, 93, 126);

    public static Form CreateShell(int width, int height)
    {
        return new Form
        {
            Width = width,
            Height = height,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            KeyPreview = true,
            BackColor = ShellBackground,
            Padding = new Padding(10),
            Font = new Font("Segoe UI", 9.5f),
        };
    }

    public static DialogResult ShowModal(Form form, IWin32Window owner)
    {
        CenterOverOwner(form, owner);
        form.Activate();
        form.BringToFront();
        return form.ShowDialog(owner);
    }

    public static void CloseWith(Form form, DialogResult result)
    {
        form.DialogResult = result;
        form.Close();
    }

    public static RoundedPanel CreateCard(Form owner)
    {
        return new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBackground,
            Radius = 22,
        };
    }

    public static Label CreateTitle(string text, int left, int top, int width)
    {
        return new Label
        {
            Text = text,
            Location = new Point(left, top),
            Size = new Size(width, 30),
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = TextColor,
            BackColor = CardBackground,
        };
    }

    public static Label CreateBodyLabel(string text, int left, int top, int width, int height)
    {
        return new Label
        {
            Text = text,
            Location = new Point(left, top),
            Size = new Size(width, height),
            Font = new Font("Segoe UI", 10f),
            ForeColor = MutedTextColor,
            BackColor = CardBackground,
        };
    }

    public static Label CreateIconBadge(string glyph, int left, int top)
    {
        return new Label
        {
            Text = glyph,
            Location = new Point(left, top),
            Size = new Size(36, 36),
            Font = new Font("Segoe MDL2 Assets", 15f),
            ForeColor = AccentColor,
            BackColor = CardBackground,
            TextAlign = ContentAlignment.MiddleCenter,
        };
    }

    public static Button CreateSecondaryButton(string text, int left, int top, DialogResult result)
    {
        return CreateButton(text, left, top, result, Mix(AccentColor, Color.White, 0.78), TextColor);
    }

    public static Button CreatePrimaryButton(string text, int left, int top, DialogResult result)
    {
        return CreateButton(text, left, top, result, AccentColor, Color.White);
    }

    private static Button CreateButton(string text, int left, int top, DialogResult result, Color backColor, Color foreColor)
    {
        return new ChromeButton
        {
            Text = text,
            Location = new Point(left, top),
            Size = new Size(86, 34),
            DialogResult = result,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font("Segoe UI Semibold", 9.5f),
            CornerRadius = 12,
            BorderColor = Color.FromArgb(70, AccentColor),
            HoverFillColor = Mix(backColor, Color.White, 0.18),
            PressedFillColor = Mix(backColor, Color.Black, 0.08),
        };
    }

    private static void CenterOverOwner(Form form, IWin32Window owner)
    {
        if (owner is Control ownerControl)
        {
            var x = ownerControl.Left + (ownerControl.Width - form.Width) / 2;
            var y = ownerControl.Top + (ownerControl.Height - form.Height) / 2;
            form.Location = new Point(Math.Max(0, x), Math.Max(0, y));
            return;
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        form.Location = new Point(
            area.Left + (area.Width - form.Width) / 2,
            area.Top + (area.Height - form.Height) / 2);
    }

    private static Color WidgetFormAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var raw = key?.GetValue("ColorizationColor");
            if (raw is int value)
            {
                var a = (value >> 24) & 0xFF;
                var r = (value >> 16) & 0xFF;
                var g = (value >> 8) & 0xFF;
                var b = value & 0xFF;
                return Color.FromArgb(a == 0 ? 255 : a, r, g, b);
            }
        }
        catch
        {
        }

        return Color.FromArgb(65, 123, 255);
    }

    private static Color Mix(Color source, Color target, double targetWeight)
    {
        var sourceWeight = 1d - targetWeight;
        return Color.FromArgb(
            255,
            (int)Math.Round(source.R * sourceWeight + target.R * targetWeight),
            (int)Math.Round(source.G * sourceWeight + target.G * targetWeight),
            (int)Math.Round(source.B * sourceWeight + target.B * targetWeight));
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
