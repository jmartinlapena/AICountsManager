using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexManager;

internal sealed class CodexManagerService : IDisposable
{
    private readonly AppPaths _paths;
    internal AppDiagnostics Diagnostics { get; }
    private readonly ProfileRepository _profiles;
    private readonly ICodexProcessController _processController;
    private readonly ICodexAppServerClient _appServer;
    private readonly IUsageClient _usageClient;
    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly SemaphoreSlim _usageGate = new(1, 1);
    private readonly AuthFileWatcher? _authWatcher;
    private bool _disposed;
    private readonly TokenHistory _history;
    private DateTimeOffset _nextBatchAllowed;
    public TokenHistorySnapshot History => _history.Snapshot;
    public Task<TokenHistorySnapshot> RefreshHistoryAsync(CancellationToken token = default) => _history.RefreshAsync(token);


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
        Diagnostics = new AppDiagnostics(paths.LogsDir);
        _history = new TokenHistory(paths.CodexHome, Path.Combine(paths.AppHome, "cache", "token_history.json"));
        _paths.EnsureDirectories();
        _profiles = new ProfileRepository(paths, Log);
        _processController = processController ?? new CodexProcessController();
        _appServer = appServer ?? new CodexAppServerClient(paths);
        _usageClient = usageClient ?? new UsageClient();

        try
        {
            Task.Run(async () =>
            {
                await _profiles.RecoverJournalAsync();
                await ReconcileActiveAuthAsync(CancellationToken.None);
            }).GetAwaiter().GetResult();
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
            CodexRunning: runtime.IsRunning,
            UpdatedLabel: FormatRelative(usageCache.Accounts.Values.Select(u => u.CheckedAt)
                .Where(value => DateTimeOffset.TryParse(value, out _)).OrderByDescending(value => DateTimeOffset.Parse(value!)).FirstOrDefault()));
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

    public bool IsMutationInProgress => _switchGate.CurrentCount == 0;

    public Task SetProfileOrderAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(() =>
        {
            var existing = _profiles.ListProfiles().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count != existing.Count || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count || !existing.SetEquals(names))
                throw new InvalidOperationException("La lista de cuentas ha cambiado. Vuelve a ordenar las cuentas actuales.");
            _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ProfileOrder = names.ToArray(), LastUpdatedAt = UtcNow() });
            return Task.CompletedTask;
        }, cancellationToken);

    public Task SetStrictVerificationAsync(bool enabled, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(() =>
        {
            _profiles.SaveAppConfig(_profiles.LoadAppConfig() with
            {
                StrictVerification = enabled,
                LastUpdatedAt = UtcNow(),
            });
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task<UsageRecord?> RefreshActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!await _usageGate.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        string? requestedProfile = null;
        try
        {
            var config = _profiles.LoadAppConfig();
            requestedProfile = config.ActiveProfile;
            if (string.IsNullOrWhiteSpace(config.ActiveProfile))
            {
                return null;
            }
            if (!File.Exists(_paths.ActiveAuthPath))
            {
                var error = UsageRecord.FromError("No existe auth.json activo.");
                _profiles.SaveUsage(config.ActiveProfile, error);
                NotifyStateChanged();
                return error;
            }

            var live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
            var stored = await _profiles.LoadProfileSnapshotAsync(config.ActiveProfile, cancellationToken);
            if (!live.Identity.Matches(stored.Identity))
            {
                var error = UsageRecord.FromError("La identidad activa ya no coincide con el perfil seleccionado.");
                _profiles.SaveUsage(config.ActiveProfile, error);
                NotifyStateChanged();
                return error;
            }

            // Background polling must stay lightweight and credential read-only.
            // Strict app-server verification is reserved for the account-switch flow.
            var usage = await _usageClient.ReadAsync(live, _paths.CodexConfigPath, cancellationToken);

            _profiles.SaveUsage(config.ActiveProfile, usage);
            NotifyStateChanged();
            return usage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var active = requestedProfile;
            var error = UsageRecord.FromError(ex.Message);
            if (!string.IsNullOrWhiteSpace(active))
            {
                _profiles.SaveUsage(active, error);
            }
            Log("Error actualizando porcentajes. Las credenciales no se han modificado.", ex);
            NotifyStateChanged();
            return error;
        }
        finally
        {
            _usageGate.Release();
        }
    }

    // Read-only queries with bounded concurrency. Never installs or renews an account's credentials.
    public async Task<string> RefreshAllUsageAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _usageGate.WaitAsync(0, cancellationToken)) return "Ya hay una actualización en curso.";
        try
        {
            if (DateTimeOffset.UtcNow < _nextBatchAllowed)
                return "Espera " + UsagePresentation.Duration((_nextBatchAllowed - DateTimeOffset.UtcNow).TotalSeconds) + " antes de volver a consultar.";
            var profiles = _profiles.ListProfiles();
            Diagnostics.Write("quota.batch.begin count=" + profiles.Count);
            AuthSnapshot? live = null;
            try { if (File.Exists(_paths.ActiveAuthPath)) live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException) { }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var completed = 0; var failed = 0; bool limited = false;
            foreach (var group in profiles.Chunk(2))
            {
                if (deadline.IsCancellationRequested || limited) break;
                var results = await Task.WhenAll(group.Select(async profile =>
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(12));
                        var stored = await _profiles.LoadProfileSnapshotAsync(profile.Name, timeout.Token);
                        var snapshot = live is not null && live.Identity.Matches(stored.Identity) && live.CompareGenerationTo(stored) >= 0 ? live : stored;
                        return await _usageClient.ReadAsync(snapshot, _paths.CodexConfigPath, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return UsageRecord.FromError("La consulta agotó el tiempo de espera."); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { return UsageRecord.FromError("No se pudo consultar esta cuenta. Comprueba la conexión y la sesión guardada."); }
                }));
                // One writer: saving the shared JSON cache concurrently would lose other accounts' results.
                for (var i = 0; i < group.Length; i++)
                {
                    var result = results[i];
                    _profiles.SaveUsage(group[i].Name, result); completed++;
                    if (result.Status == "error") failed++;
                    if (result.HttpStatus == 429)
                    { limited = true; _nextBatchAllowed = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, result.RetryAfterSeconds ?? 60)); }
                    progress?.Report($"Actualizando cuentas · {completed}/{profiles.Count}");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            _nextBatchAllowed = _nextBatchAllowed > DateTimeOffset.UtcNow ? _nextBatchAllowed : DateTimeOffset.UtcNow.AddSeconds(15);
            NotifyStateChanged();
            Diagnostics.Write($"quota.batch.complete completed={completed} failed={failed} limited={limited}");
            if (profiles.Count == 0) return "Historial actualizado. Añade una cuenta para consultar sus cuotas.";
            var text = $"{completed - failed}/{profiles.Count} cuentas actualizadas";
            if (failed > 0) text += $" · {failed} sin conexión o sesión válida";
            if (completed < profiles.Count) text += $" · {profiles.Count - completed} pendientes";
            if (limited) text += " · Límite de consultas: espera antes de reintentar";
            return text + ".";
        }
        finally { _usageGate.Release(); }
    }

    public Task AddCurrentProfileAsync(string profileName, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(async () =>
        {
            if (!File.Exists(_paths.ActiveAuthPath))
                throw new InvalidOperationException("No existe una sesión activa de Codex para guardar.");
            var snapshot = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
            if (!snapshot.Identity.IsUsable)
                throw new InvalidOperationException("La sesión no contiene una identidad de cuenta verificable.");
            _profiles.AddOrUpdateFromSnapshot(profileName, snapshot);
            Log($"Perfil '{profileName}' guardado para identidad {AuthHash.Short(snapshot.Identity.Fingerprint)}.", null);
        }, cancellationToken);

    public Task RenameProfileAsync(string currentName, string newName, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(() =>
        {
            _profiles.Rename(currentName, newName);
            return Task.CompletedTask;
        }, cancellationToken);

    public Task DeleteProfileAsync(string profileName, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(async () =>
        {
            var config = _profiles.LoadAppConfig();
            if (string.Equals(config.ActiveProfile, profileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("No se puede eliminar el perfil activo. Cambia primero a otra cuenta.");
            if (File.Exists(_paths.ActiveAuthPath))
            {
                var live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
                var stored = await _profiles.LoadProfileSnapshotAsync(profileName, cancellationToken);
                if (live.Identity.Matches(stored.Identity))
                    throw new InvalidOperationException("No se puede eliminar un perfil de la cuenta que está activa en Codex.");
            }
            _profiles.Delete(profileName);
        }, cancellationToken);

    private async Task MutateProfilesAsync(Func<Task> mutation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            await _usageGate.WaitAsync(cancellationToken);
            try
            {
                await _credentialGate.WaitAsync(cancellationToken);
                try
                {
                    using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
                    await globalLock.AcquireAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    await mutation();
                }
                finally { _credentialGate.Release(); }
            }
            finally { _usageGate.Release(); }
        }
        finally { _switchGate.Release(); }
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

                    targetWasInstalled = true;
                    AtomicFile.WriteBytes(_paths.ActiveAuthPath, target.Bytes);
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
                if (targetWasInstalled)
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
                        "La cuenta se instaló, pero Codex no pudo reiniciarse automáticamente.",
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
                    : "Cuenta activada.",
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
            // Absence is a valid pre-transaction state and must also be restored.
            if (File.Exists(_paths.ActiveAuthPath)) File.Delete(_paths.ActiveAuthPath);
            _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ActiveProfile = null, LastUpdatedAt = UtcNow() });
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
        if (_disposed)
        {
            return;
        }

        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
            await globalLock.AcquireAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (!File.Exists(_paths.ActiveAuthPath))
            {
                _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ActiveProfile = null, LastUpdatedAt = UtcNow() });
                NotifyStateChanged();
                return;
            }
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
        var firstTable = Regex.Match(content, @"^\s*\[", RegexOptions.Multiline);
        var split = firstTable.Success ? firstTable.Index : content.Length;
        var root = content[..split];
        const string setting = "cli_auth_credentials_store = \"file\"";
        var updatedRoot = Regex.IsMatch(root, @"^\s*cli_auth_credentials_store\s*=", RegexOptions.Multiline)
            ? Regex.Replace(root, @"^[ \t]*cli_auth_credentials_store[ \t]*=[^\r\n]*", setting, RegexOptions.Multiline)
            : setting + Environment.NewLine + root;
        var updated = updatedRoot + content[split..];
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
        if (process is null) throw new IOException("No se pudo actualizar el inicio con Windows.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Windows no pudo guardar el acceso directo de inicio.");
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
            Email: profile?.Email,
            SessionWindow: usage.Session, WeeklyWindow: usage.Weekly,
            PlanType: usage.PlanType, SessionLimitNotApplicable: usage.SessionLimitNotApplicable);
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
        return DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromMinutes(15)
            || HasResetElapsed(usage.Session.ResetsAt) || HasResetElapsed(usage.Weekly.ResetsAt) ? "stale" : "ok";
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
        // In-flight callbacks may still release these gates after watcher cancellation.
        // No WaitHandle is allocated; let the gates be collected with the service.
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");
    private static double? Clamp(double? value) => value.HasValue ? Math.Clamp(value.Value, 0d, 100d) : null;
    private static double? Remaining(double? value) => value.HasValue ? Math.Clamp(100d - value.Value, 0d, 100d) : null;
    private static double? DisplayUsedPercent(UsageWindow window) => HasResetElapsed(window.ResetsAt) ? null : Clamp(window.UsedPercent);
    private static double? DisplayRemainingPercent(UsageWindow window) => HasResetElapsed(window.ResetsAt) ? null : Remaining(window.UsedPercent);

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

    private static string FormatReset(long? unixSeconds) => UsagePresentation.Countdown(unixSeconds, DateTimeOffset.UtcNow);

    private static string FormatRelative(string? checkedAt)
    {
        if (!DateTimeOffset.TryParse(checkedAt, out var time))
        {
            return "Sin datos recientes";
        }
        var delta = DateTimeOffset.UtcNow - time;
        if (delta.TotalMinutes < 1)
        {
            return "Actualizado ahora";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"Hace {Math.Max(1, (int)delta.TotalMinutes)} min";
        }
        if (delta.TotalHours < 24)
        {
            return $"Hace {(int)delta.TotalHours} h";
        }
        return $"Hace {(int)delta.TotalDays} días";
    }
}
