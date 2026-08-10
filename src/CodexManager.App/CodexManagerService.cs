using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexManager;

internal sealed class CodexManagerService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ProfileRepository _profiles;
    private readonly ICodexProcessController _processController;
    private readonly ICodexAppServerClient _appServer;
    private readonly IUsageClient _usageClient;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly SemaphoreSlim _usageGate = new(1, 1);
    private readonly AuthFileWatcher? _authWatcher;
    private bool _disposed;

    public event EventHandler? StateChanged;

    public CodexManagerService()
        : this(AppPaths.Build(), null, null, null, startWatcher: true)
    {
    }

    internal CodexManagerService(
        AppPaths paths,
        ICodexProcessController? processController,
        ICodexAppServerClient? appServer,
        IUsageClient? usageClient,
        bool startWatcher)
    {
        _paths = paths;
        _paths.EnsureDirectories();
        _profiles = new ProfileRepository(paths, Log);
        _processController = processController ?? new CodexProcessController();
        _appServer = appServer ?? new CodexAppServerClient(paths);
        _usageClient = usageClient ?? new UsageClient();

        try
        {
            _profiles.RecoverJournalAsync().GetAwaiter().GetResult();
            ReconcileActiveAuthAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log("La reconciliacion inicial no se pudo completar; no se modificaron credenciales.", ex);
        }

        if (startWatcher)
        {
            _authWatcher = new AuthFileWatcher(_paths.ActiveAuthPath, ReconcileActiveAuthAsync, Log);
            _authWatcher.ScheduleInitialReconciliation();
        }
        Log("CodexManager iniciado con sincronizacion por eventos.", null);
    }

    public DashboardState BuildDashboardState()
    {
        var profiles = _profiles.ListProfiles();
        var config = _profiles.LoadAppConfig();
        var activeMeta = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, config.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        var usageCache = _profiles.LoadUsageCache();
        var activeUsage = activeMeta is null
            ? UsageRecord.Empty()
            : GetUsage(usageCache, activeMeta.Name);
        var profileViews = profiles
            .Select(profile => BuildProfileState(
                profile,
                GetUsage(usageCache, profile.Name),
                string.Equals(profile.Name, activeMeta?.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var binary = CodexBinaryLocator.Find();
        var runtime = _processController.CaptureState();
        var settings = binary is null
            ? "Codex no encontrado. Define CODEX_BINARY."
            : $"Watcher activo · Verificacion estricta: {(config.StrictVerification ? "si" : "no")}";

        return new DashboardState(
            ActiveProfile: BuildProfileState(activeMeta, activeUsage, activeMeta is not null),
            Profiles: profileViews,
            SettingsSummary: settings,
            DataFolder: _paths.AppHome,
            LogsFolder: _paths.LogsDir,
            StrictVerification: config.StrictVerification,
            CodexRunning: runtime.IsRunning);
    }

    public string BuildTrayTooltip(DashboardState state)
    {
        var active = state.ActiveProfile;
        var session = active.SessionRemainingPercent.HasValue
            ? $"{active.SessionRemainingPercent.Value:0}% restante"
            : "sin datos";
        var text = $"CodexManager - {active.Name} - {session}";
        return text.Length <= 63 ? text : text[..63];
    }

    public bool IsCodexRunning() => _processController.CaptureState().IsRunning;

    public bool StrictVerificationEnabled() => _profiles.LoadAppConfig().StrictVerification;

    public void SetStrictVerification(bool enabled)
    {
        var config = _profiles.LoadAppConfig();
        _profiles.SaveAppConfig(config with
        {
            StrictVerification = enabled,
            LastUpdatedAt = UtcNow(),
        });
        NotifyStateChanged();
    }

    public async Task RefreshActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!await _usageGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var config = _profiles.LoadAppConfig();
            if (string.IsNullOrWhiteSpace(config.ActiveProfile))
            {
                return;
            }
            if (!File.Exists(_paths.ActiveAuthPath))
            {
                _profiles.SaveUsage(config.ActiveProfile, UsageRecord.FromError("No existe auth.json activo."));
                NotifyStateChanged();
                return;
            }

            var live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
            var stored = await _profiles.LoadProfileSnapshotAsync(config.ActiveProfile, cancellationToken);
            if (!live.Identity.Matches(stored.Identity))
            {
                _profiles.SaveUsage(
                    config.ActiveProfile,
                    UsageRecord.FromError("La identidad activa ya no coincide con el perfil seleccionado."));
                NotifyStateChanged();
                return;
            }

            UsageRecord usage;
            if (config.StrictVerification && !_processController.CaptureState().IsRunning)
            {
                try
                {
                    usage = await _appServer.ReadRateLimitsAsync(cancellationToken);
                }
                catch
                {
                    usage = await _usageClient.ReadAsync(live, _paths.CodexConfigPath, cancellationToken);
                }
            }
            else
            {
                usage = await _usageClient.ReadAsync(live, _paths.CodexConfigPath, cancellationToken);
            }

            _profiles.SaveUsage(config.ActiveProfile, usage);
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var active = _profiles.LoadAppConfig().ActiveProfile;
            if (!string.IsNullOrWhiteSpace(active))
            {
                _profiles.SaveUsage(active, UsageRecord.FromError(ex.Message));
            }
            Log("Error actualizando porcentajes. Las credenciales no se han modificado.", ex);
            NotifyStateChanged();
        }
        finally
        {
            _usageGate.Release();
        }
    }

    public void AddCurrentProfile(string profileName)
    {
        ThrowIfDisposed();
        _credentialGate.Wait();
        try
        {
            using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
            globalLock.AcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            if (!File.Exists(_paths.ActiveAuthPath))
            {
                throw new InvalidOperationException("No existe auth.json activo en CODEX_HOME.");
            }

            var snapshot = StableAuthFile.ReadAsync(_paths.ActiveAuthPath).GetAwaiter().GetResult();
            _profiles.AddOrUpdateFromSnapshot(profileName, snapshot);
            Log($"Perfil '{profileName}' guardado para identidad {AuthHash.Short(snapshot.Identity.Fingerprint)}.", null);
        }
        finally
        {
            _credentialGate.Release();
        }
        NotifyStateChanged();
    }

    public void RenameProfile(string currentName, string newName)
    {
        ThrowIfDisposed();
        _credentialGate.Wait();
        try
        {
            using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
            globalLock.AcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            _profiles.Rename(currentName, newName);
        }
        finally
        {
            _credentialGate.Release();
        }
        NotifyStateChanged();
    }

    public void DeleteProfile(string profileName)
    {
        ThrowIfDisposed();
        var config = _profiles.LoadAppConfig();
        if (string.Equals(config.ActiveProfile, profileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No se puede eliminar el perfil activo. Cambia primero a otra cuenta.");
        }

        _credentialGate.Wait();
        try
        {
            using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
            globalLock.AcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            _profiles.Delete(profileName);
        }
        finally
        {
            _credentialGate.Release();
        }
        NotifyStateChanged();
    }

    public Task<SwitchProfileResult> SwitchToProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default) =>
        SwitchToProfileCoreAsync(profileName, skipStrictVerification: false, cancellationToken);

    public Task<SwitchProfileResult> PrepareReauthenticationAsync(
        string profileName,
        CancellationToken cancellationToken = default) =>
        SwitchToProfileCoreAsync(profileName, skipStrictVerification: true, cancellationToken);

    private async Task<SwitchProfileResult> SwitchToProfileCoreAsync(
        string profileName,
        bool skipStrictVerification,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        profileName = _profiles.SanitizeProfileName(profileName);
        await _switchGate.WaitAsync(cancellationToken);
        var usageGateHeld = false;
        var initialRuntime = new CodexRuntimeState(false, null, []);
        var previousProfile = _profiles.LoadAppConfig().ActiveProfile;
        var shouldRestart = skipStrictVerification;
        var codexClosedByUs = false;
        AuthSnapshot? outgoingForRollback = null;
        ProfileAuthMatch? outgoingMatchForRollback = null;
        var targetWasInstalled = false;

        try
        {
            // A rate-limit query may own a short-lived app-server process. Wait
            // for it and prevent another query from starting during the switch.
            await _usageGate.WaitAsync(cancellationToken);
            usageGateHeld = true;
            initialRuntime = _processController.CaptureState();
            shouldRestart = initialRuntime.IsRunning || skipStrictVerification;

            // Validate both sides before closing Codex. A corrupt target or an
            // unsaved live account must never interrupt the current session.
            await _credentialGate.WaitAsync(cancellationToken);
            try
            {
                using var preflightLock = new GlobalAuthSwitchLock(_paths.CodexHome);
                await preflightLock.AcquireAsync(TimeSpan.FromSeconds(10), cancellationToken);
                var preflightTarget = await _profiles.LoadProfileSnapshotAsync(profileName, cancellationToken);
                if (!preflightTarget.Identity.IsUsable)
                {
                    return new SwitchProfileResult(
                        SwitchProfileStatus.Failed,
                        $"El perfil '{profileName}' no contiene una identidad verificable.",
                        previousProfile,
                        profileName);
                }

                if (File.Exists(_paths.ActiveAuthPath))
                {
                    var preflightOutgoing = await StableAuthFile.ReadAsync(
                        _paths.ActiveAuthPath,
                        cancellationToken: cancellationToken);
                    var preflightMatch = await _profiles.FindByIdentityAsync(preflightOutgoing, cancellationToken);
                    if (preflightMatch is null)
                    {
                        return new SwitchProfileResult(
                            SwitchProfileStatus.Failed,
                            "La cuenta activa no esta guardada como perfil. Guardala antes de cambiar para no perder el inicio de sesion.",
                            previousProfile,
                            profileName);
                    }
                }
            }
            finally
            {
                _credentialGate.Release();
            }

            if (initialRuntime.IsRunning)
            {
                var closed = await _processController.CloseNormallyAsync(TimeSpan.FromSeconds(45), cancellationToken);
                if (!closed)
                {
                    return new SwitchProfileResult(
                        SwitchProfileStatus.CodexStillRunning,
                        "Codex sigue abierto. Termina o cancela la operacion activa, cierralo y vuelve a intentarlo.",
                        previousProfile,
                        profileName);
                }
                codexClosedByUs = true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(850), cancellationToken);
            AuthSnapshot targetIdentityForRestart;

            await _credentialGate.WaitAsync(cancellationToken);
            try
            {
                using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
                await globalLock.AcquireAsync(TimeSpan.FromSeconds(10), cancellationToken);

                var target = await _profiles.LoadProfileSnapshotAsync(profileName, cancellationToken);
                if (!target.Identity.IsUsable)
                {
                    throw new InvalidOperationException($"El perfil '{profileName}' no contiene una identidad verificable.");
                }

                if (File.Exists(_paths.ActiveAuthPath))
                {
                    outgoingForRollback = await StableAuthFile.ReadAsync(
                        _paths.ActiveAuthPath,
                        cancellationToken: cancellationToken);
                    outgoingMatchForRollback = await _profiles.FindByIdentityAsync(
                        outgoingForRollback,
                        cancellationToken);
                    if (outgoingMatchForRollback is null)
                    {
                        if (codexClosedByUs)
                        {
                            await _processController.RestartAsync(
                                initialRuntime.DesktopExecutablePath,
                                TimeSpan.FromSeconds(15),
                                CancellationToken.None);
                            codexClosedByUs = false;
                        }
                        return new SwitchProfileResult(
                            SwitchProfileStatus.Failed,
                            "La cuenta activa no esta guardada como perfil. Guardala antes de cambiar para no perder el inicio de sesion.",
                            previousProfile,
                            profileName);
                    }
                    await _profiles.SaveIfNewerAsync(
                        outgoingMatchForRollback.Name,
                        outgoingForRollback,
                        cancellationToken);
                    await EnsureOutgoingWasPreservedAsync(
                        outgoingMatchForRollback.Name,
                        outgoingForRollback,
                        cancellationToken);
                    previousProfile = outgoingMatchForRollback.Name;
                }

                if (outgoingForRollback is not null && outgoingForRollback.Identity.Matches(target.Identity))
                {
                    _profiles.SaveAppConfig(_profiles.LoadAppConfig() with
                    {
                        ActiveProfile = profileName,
                        LastUpdatedAt = UtcNow(),
                    });
                    targetIdentityForRestart = outgoingForRollback;
                }
                else
                {
                    var transaction = new CredentialTransaction(
                        Id: Guid.NewGuid().ToString("N"),
                        Operation: skipStrictVerification ? "reauthenticate" : "switch",
                        Stage: "prepared",
                        SourceProfile: outgoingMatchForRollback?.Name,
                        TargetProfile: profileName,
                        SourceAuthFingerprint: outgoingForRollback?.ContentFingerprint,
                        TargetAuthFingerprint: target.ContentFingerprint,
                        TargetIdentityFingerprint: target.Identity.Fingerprint,
                        StartedAt: UtcNow(),
                        UpdatedAt: UtcNow());
                    _profiles.WriteJournal(transaction);

                    AtomicFile.WriteBytes(_paths.ActiveAuthPath, target.Bytes);
                    targetWasInstalled = true;
                    var installed = await StableAuthFile.ReadAsync(
                        _paths.ActiveAuthPath,
                        cancellationToken: cancellationToken);
                    EnsureInstalledTarget(profileName, target, installed);
                    transaction = transaction with { Stage = "installed", UpdatedAt = UtcNow() };
                    _profiles.WriteJournal(transaction);

                    var strict = _profiles.LoadAppConfig().StrictVerification && !skipStrictVerification;
                    if (strict)
                    {
                        var verification = await _appServer.VerifyAsync(forceRefresh: true, cancellationToken);
                        if (verification.Status != AuthVerificationStatus.Success)
                        {
                            await RestoreOutgoingAsync(
                                outgoingForRollback,
                                outgoingMatchForRollback,
                                CancellationToken.None);
                            targetWasInstalled = false;
                            _profiles.ClearJournal();
                            if (shouldRestart)
                            {
                                await _processController.RestartAsync(
                                    initialRuntime.DesktopExecutablePath,
                                    TimeSpan.FromSeconds(15),
                                    cancellationToken);
                            }

                            var status = verification.Status == AuthVerificationStatus.RefreshTokenInvalidated
                                ? SwitchProfileStatus.RequiresReauthentication
                                : SwitchProfileStatus.Failed;
                            return new SwitchProfileResult(
                                status,
                                verification.Error ?? "La verificacion estricta no se pudo completar; se restauro la cuenta anterior.",
                                previousProfile,
                                profileName,
                                shouldRestart);
                        }

                        var refreshed = await StableAuthFile.ReadAsync(
                            _paths.ActiveAuthPath,
                            cancellationToken: cancellationToken);
                        if (!refreshed.Identity.Matches(target.Identity))
                        {
                            await RestoreOutgoingAsync(
                                outgoingForRollback,
                                outgoingMatchForRollback,
                                CancellationToken.None);
                            targetWasInstalled = false;
                            _profiles.ClearJournal();
                            throw new InvalidOperationException("La verificacion devolvio una identidad distinta; se restauro la cuenta anterior.");
                        }
                        await _profiles.SaveIfNewerAsync(profileName, refreshed, cancellationToken);
                        target = refreshed;
                        transaction = transaction with
                        {
                            Stage = "verified",
                            TargetAuthFingerprint = refreshed.ContentFingerprint,
                            UpdatedAt = UtcNow(),
                        };
                        _profiles.WriteJournal(transaction);
                    }

                    _profiles.SaveAppConfig(_profiles.LoadAppConfig() with
                    {
                        ActiveProfile = profileName,
                        LastUpdatedAt = UtcNow(),
                    });
                    _profiles.ClearJournal();
                    targetWasInstalled = false;
                    targetIdentityForRestart = target;
                }
            }
            catch
            {
                if (targetWasInstalled && outgoingForRollback is not null)
                {
                    await RestoreOutgoingAsync(
                        outgoingForRollback,
                        outgoingMatchForRollback,
                        CancellationToken.None);
                    targetWasInstalled = false;
                    _profiles.ClearJournal();
                }
                throw;
            }
            finally
            {
                _credentialGate.Release();
            }

            var restarted = false;
            if (shouldRestart)
            {
                restarted = await _processController.RestartAsync(
                    initialRuntime.DesktopExecutablePath,
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                if (!restarted)
                {
                    return new SwitchProfileResult(
                        SwitchProfileStatus.RestartFailed,
                        "La cuenta se instalo y verifico, pero Codex no pudo reiniciarse automaticamente.",
                        previousProfile,
                        profileName);
                }
                codexClosedByUs = false;

                await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
                var afterRestart = await StableAuthFile.ReadAsync(
                    _paths.ActiveAuthPath,
                    cancellationToken: cancellationToken);
                if (!afterRestart.Identity.Matches(targetIdentityForRestart.Identity))
                {
                    return new SwitchProfileResult(
                        SwitchProfileStatus.Failed,
                        "Codex se reinicio, pero la identidad activa no coincide con el perfil solicitado.",
                        previousProfile,
                        profileName,
                        true);
                }
            }

            Log($"Cambio completado a '{profileName}' ({AuthHash.Short(targetIdentityForRestart.Identity.Fingerprint)}).", null);
            NotifyStateChanged();
            _usageGate.Release();
            usageGateHeld = false;
            _ = RefreshActiveProfileAsync(CancellationToken.None);
            return new SwitchProfileResult(
                SwitchProfileStatus.Success,
                skipStrictVerification
                    ? "Cuenta preparada. Completa el inicio de sesion en Codex; el watcher guardara las credenciales nuevas."
                    : "Cuenta activada y verificada.",
                previousProfile,
                profileName,
                restarted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RestartAfterFailedSwitchAsync(initialRuntime, codexClosedByUs);
            throw;
        }
        catch (Exception ex)
        {
            Log($"Fallo cambiando al perfil '{profileName}'.", ex);
            try
            {
                await _profiles.RecoverJournalAsync(cancellationToken);
            }
            catch
            {
            }
            await RestartAfterFailedSwitchAsync(initialRuntime, codexClosedByUs);
            return new SwitchProfileResult(
                SwitchProfileStatus.Failed,
                ex.Message,
                previousProfile,
                profileName);
        }
        finally
        {
            if (usageGateHeld)
            {
                _usageGate.Release();
            }
            _switchGate.Release();
        }
    }

    private async Task RestartAfterFailedSwitchAsync(CodexRuntimeState initialRuntime, bool codexClosedByUs)
    {
        if (!codexClosedByUs || !initialRuntime.IsRunning || _processController.CaptureState().IsRunning)
        {
            return;
        }

        try
        {
            await _processController.RestartAsync(
                initialRuntime.DesktopExecutablePath,
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log("No se pudo reiniciar Codex despues de cancelar o revertir el cambio.", ex);
        }
    }

    private async Task RestoreOutgoingAsync(
        AuthSnapshot? outgoing,
        ProfileAuthMatch? outgoingMatch,
        CancellationToken cancellationToken)
    {
        if (outgoing is null)
        {
            return;
        }

        var restore = outgoing;
        if (outgoingMatch is not null)
        {
            var stored = await _profiles.LoadProfileSnapshotAsync(outgoingMatch.Name, cancellationToken);
            if (stored.Identity.Matches(outgoing.Identity) && stored.CompareGenerationTo(outgoing) > 0)
            {
                restore = stored;
            }
        }

        AtomicFile.WriteBytes(_paths.ActiveAuthPath, restore.Bytes);
        var verified = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
        if (!verified.Identity.Matches(restore.Identity))
        {
            throw new IOException("No se pudo verificar la restauracion de la cuenta anterior.");
        }
        if (outgoingMatch is not null)
        {
            _profiles.SaveAppConfig(_profiles.LoadAppConfig() with
            {
                ActiveProfile = outgoingMatch.Name,
                LastUpdatedAt = UtcNow(),
            });
        }
    }

    private async Task EnsureOutgoingWasPreservedAsync(
        string profileName,
        AuthSnapshot outgoing,
        CancellationToken cancellationToken)
    {
        var stored = await _profiles.LoadProfileSnapshotAsync(profileName, cancellationToken);
        if (!stored.Identity.Matches(outgoing.Identity))
        {
            throw new InvalidOperationException(
                $"No se pudo preservar la identidad saliente en el perfil '{profileName}'.");
        }
        if (string.Equals(stored.CredentialFingerprint, outgoing.CredentialFingerprint, StringComparison.Ordinal)
            || stored.CompareGenerationTo(outgoing) > 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"La generacion saliente de '{profileName}' no pudo guardarse con seguridad. No se ha cambiado de cuenta.");
    }

    private static void EnsureInstalledTarget(string profileName, AuthSnapshot expected, AuthSnapshot installed)
    {
        if (!expected.Identity.Matches(installed.Identity)
            || !string.Equals(expected.ContentFingerprint, installed.ContentFingerprint, StringComparison.Ordinal))
        {
            throw new IOException($"La sustitucion atomica de '{profileName}' no supero la verificacion.");
        }
    }

    private async Task ReconcileActiveAuthAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !File.Exists(_paths.ActiveAuthPath))
        {
            return;
        }

        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
            await globalLock.AcquireAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
            var match = await _profiles.FindByIdentityAsync(live, cancellationToken);
            var config = _profiles.LoadAppConfig();

            if (match is null)
            {
                if (!string.IsNullOrWhiteSpace(config.ActiveProfile))
                {
                    _profiles.SaveAppConfig(config with { ActiveProfile = null, LastUpdatedAt = UtcNow() });
                }
                Log(
                    $"auth.json pertenece a una identidad no guardada ({AuthHash.Short(live.Identity.Fingerprint)}).",
                    null);
                NotifyStateChanged();
                return;
            }

            var updated = await _profiles.SaveIfNewerAsync(match.Name, live, cancellationToken);
            if (!string.Equals(config.ActiveProfile, match.Name, StringComparison.OrdinalIgnoreCase))
            {
                _profiles.SaveAppConfig(config with
                {
                    ActiveProfile = match.Name,
                    LastUpdatedAt = UtcNow(),
                });
            }
            if (updated)
            {
                Log(
                    $"Watcher guardo una nueva generacion para '{match.Name}' ({AuthHash.Short(live.ContentFingerprint)}).",
                    null);
            }
            NotifyStateChanged();
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    public void EnsureFileModeConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.CodexConfigPath)!);
        var content = File.Exists(_paths.CodexConfigPath)
            ? File.ReadAllText(_paths.CodexConfigPath)
            : string.Empty;
        string updated;
        if (Regex.IsMatch(content, @"^\s*cli_auth_credentials_store\s*=", RegexOptions.Multiline))
        {
            updated = Regex.Replace(
                content,
                @"^\s*cli_auth_credentials_store\s*=.*$",
                "cli_auth_credentials_store = \"file\"",
                RegexOptions.Multiline);
        }
        else
        {
            updated = content.TrimEnd() + Environment.NewLine
                + "cli_auth_credentials_store = \"file\""
                + Environment.NewLine;
        }
        if (!string.Equals(content, updated, StringComparison.Ordinal))
        {
            AtomicFile.WriteText(_paths.CodexConfigPath, updated);
        }
        NotifyStateChanged();
    }

    public bool StartupShortcutExists() => File.Exists(_paths.StartupShortcutPath);

    public void SetStartupEnabled(bool enabled)
    {
        if (enabled)
        {
            CreateStartupShortcut();
        }
        else if (File.Exists(_paths.StartupShortcutPath))
        {
            File.Delete(_paths.StartupShortcutPath);
        }
        NotifyStateChanged();
    }

    public void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void CreateStartupShortcut()
    {
        Directory.CreateDirectory(_paths.StartupFolder);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("No se pudo resolver el ejecutable.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        var hostedByDotnet = string.Equals(
            Path.GetFileNameWithoutExtension(executable),
            "dotnet",
            StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssembly);
        var arguments = hostedByDotnet ? $"\"{entryAssembly}\"" : string.Empty;
        var command = $"""
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut('{_paths.StartupShortcutPath.Replace("'", "''")}')
            $shortcut.TargetPath = '{executable.Replace("'", "''")}'
            $shortcut.Arguments = '{arguments.Replace("'", "''")}'
            $shortcut.WorkingDirectory = '{AppContext.BaseDirectory.Replace("'", "''")}'
            $shortcut.IconLocation = '{Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico").Replace("'", "''")},0'
            $shortcut.Description = 'CodexManager'
            $shortcut.WindowStyle = 7
            $shortcut.Save()
            """;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                command,
            },
        });
        process?.WaitForExit();
    }

    private static UsageRecord GetUsage(UsageCacheFile cache, string profileName) =>
        cache.Accounts.TryGetValue(profileName, out var usage) ? usage : UsageRecord.Empty();

    private static ProfileState BuildProfileState(ProfileMeta? profile, UsageRecord usage, bool isActive)
    {
        return new ProfileState(
            Name: profile?.Name ?? "Sin perfil activo",
            IsActive: isActive,
            SessionUsedPercent: DisplayUsedPercent(usage.Session),
            SessionRemainingPercent: DisplayRemainingPercent(usage.Session),
            SessionResetLabel: FormatReset(usage.Session.ResetsAt),
            WeeklyUsedPercent: DisplayUsedPercent(usage.Weekly),
            WeeklyRemainingPercent: DisplayRemainingPercent(usage.Weekly),
            WeeklyResetLabel: FormatReset(usage.Weekly.ResetsAt),
            UpdatedLabel: FormatRelative(usage.CheckedAt),
            Status: BuildStatus(usage),
            Error: usage.Error,
            Email: profile?.Email);
    }

    private static string BuildStatus(UsageRecord usage)
    {
        if (string.Equals(usage.Status, "error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }
        if (!DateTimeOffset.TryParse(usage.CheckedAt, out var checkedAt))
        {
            return "never_checked";
        }
        return DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromMinutes(15) ? "stale" : "ok";
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void Log(string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDir);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (exception is not null)
            {
                line += $" {exception.GetType().Name}: {exception.Message}";
            }
            File.AppendAllLines(_paths.LogFilePath, [line]);
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _authWatcher?.Dispose();
        _credentialGate.Dispose();
        _switchGate.Dispose();
        _usageGate.Dispose();
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");
    private static double? Clamp(double? value) => value.HasValue ? Math.Clamp(value.Value, 0d, 100d) : null;
    private static double? Remaining(double? value) => value.HasValue ? Math.Clamp(100d - value.Value, 0d, 100d) : null;
    private static double? DisplayUsedPercent(UsageWindow window) => HasResetElapsed(window.ResetsAt) ? 0d : Clamp(window.UsedPercent);
    private static double? DisplayRemainingPercent(UsageWindow window) => HasResetElapsed(window.ResetsAt) ? 100d : Remaining(window.UsedPercent);

    private static bool HasResetElapsed(long? unixSeconds)
    {
        if (!unixSeconds.HasValue)
        {
            return false;
        }
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) <= DateTimeOffset.Now;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatReset(long? unixSeconds)
    {
        if (!unixSeconds.HasValue)
        {
            return "Sin reset";
        }
        try
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).ToLocalTime();
            var delta = reset - DateTimeOffset.Now;
            if (delta <= TimeSpan.Zero)
            {
                return "Updated";
            }
            return delta.TotalDays >= 1 ? reset.ToString("dd/MM") : reset.ToString("HH:mm");
        }
        catch
        {
            return "Sin reset";
        }
    }

    private static string FormatRelative(string? checkedAt)
    {
        if (!DateTimeOffset.TryParse(checkedAt, out var time))
        {
            return "Sin datos recientes";
        }
        var delta = DateTimeOffset.UtcNow - time;
        if (delta.TotalMinutes < 1)
        {
            return "Updated just now";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"Updated {Math.Max(1, (int)delta.TotalMinutes)}m ago";
        }
        if (delta.TotalHours < 24)
        {
            return $"Updated {(int)delta.TotalHours}h ago";
        }
        return $"Updated {(int)delta.TotalDays}d ago";
    }
}
