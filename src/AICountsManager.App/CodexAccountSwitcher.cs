namespace AICountsManager;

internal sealed record CodexSwitchCallbacks(Func<bool> IsEnabled, Action RequireNoEnrollment, Action RequireActive,
    Action Changed, Func<CancellationToken, Task> Refresh, Action<string, Exception?> Log);

// Owns only the existing Codex credential transaction and its organization adapter.
internal sealed class CodexAccountSwitcher
{
    private readonly AppPaths _paths;
    private readonly ProfileRepository _profiles;
    private readonly ICodexProcessController _processController;
    private readonly ICodexAppServerClient _appServer;
    private readonly LocalOrganizationTransfer _codexOrganization;
    private readonly CodexSwitchCallbacks _callbacks;
    private readonly SemaphoreSlim _switchGate, _usageGate, _credentialGate;
    internal bool HasRecovery => _codexOrganization.HasRecovery;

    internal CodexAccountSwitcher(AppPaths paths, ProfileRepository profiles, ICodexProcessController process,
        ICodexAppServerClient appServer, AccountOperationCoordinator operations, CodexSwitchCallbacks callbacks)
    {
        _paths = paths; _profiles = profiles; _processController = process; _appServer = appServer; _callbacks = callbacks;
        _switchGate = operations.Mutations; _usageGate = operations.Quotas; _credentialGate = operations.Credentials;
        _codexOrganization = new(new CodexOrganizationAdapter(paths.CodexHome), paths.TransactionsDir, paths.BackupsDir,
            () => { if (process.CaptureState().IsRunning) throw new InvalidOperationException("Cierra Codex para trasladar su organización local."); });
    }

    internal async Task RecoverAsync(CancellationToken token)
    {
        if (!_codexOrganization.HasRecovery) return;
        var active = File.Exists(_paths.ActiveAuthPath) ? await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: token) : null;
        _codexOrganization.Recover(active?.Identity.Fingerprint);
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
        _callbacks.RequireActive();
        profileName = _profiles.SanitizeProfileName(profileName);
        _callbacks.RequireNoEnrollment();
        if (!_callbacks.IsEnabled()) throw new InvalidOperationException("Activa Codex en Ajustes antes de cambiar de cuenta.");
        await _switchGate.WaitAsync(cancellationToken);
        var usageGateHeld = false;
        var initialRuntime = new CodexRuntimeState(false, null, []);
        var previousProfile = _profiles.LoadAppConfig().ActiveProfile;
        var shouldRestart = false;
        var codexClosedByUs = false;
        AuthSnapshot? outgoingForRollback = null;
        ProfileAuthMatch? outgoingMatchForRollback = null;
        var targetWasInstalled = false;
        LocalOrganizationTransfer.OrganizationChange? organizationChange = null;

        try
        {
            // A rate-limit query may own a short-lived app-server process. Wait
            // for it and prevent another query from starting during the switch.
            await _usageGate.WaitAsync(cancellationToken);
            usageGateHeld = true;
            _callbacks.RequireNoEnrollment();
            if (!_callbacks.IsEnabled()) throw new InvalidOperationException("Activa Codex en Ajustes antes de cambiar de cuenta.");
            initialRuntime = _processController.CaptureState();
            shouldRestart = false;

            // Validate both sides before closing Codex. A corrupt target or an
            // unsaved live account must never interrupt the current session.
            await _credentialGate.WaitAsync(cancellationToken);
            try
            {
                using var preflightLock = new GlobalAuthSwitchLock(_paths.CodexHome);
                await preflightLock.AcquireAsync(TimeSpan.FromSeconds(10), cancellationToken);
                await RecoverAsync(cancellationToken);
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
                return new SwitchProfileResult(SwitchProfileStatus.CodexStillRunning,
                    "Cierra Codex antes de cambiar su cuenta. Claude puede permanecer abierto.", previousProfile, profileName);

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
                    if (outgoingForRollback is not null)
                        organizationChange = _codexOrganization.Prepare(outgoingForRollback.Identity.Fingerprint, target.Identity.Fingerprint,
                            new(outgoingForRollback.Identity.AccountId ?? "", outgoingForRollback.Identity.UserId ?? ""),
                            new(target.Identity.AccountId ?? "", target.Identity.UserId ?? ""));
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
                    organizationChange?.Apply();

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
                            organizationChange?.Rollback();
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
                            organizationChange?.Rollback();
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

                    organizationChange?.Verify();
                    _profiles.SaveAppConfig(_profiles.LoadAppConfig() with
                    {
                        ActiveProfile = profileName,
                        LastUpdatedAt = UtcNow(),
                    });
                    organizationChange?.Commit();
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
                organizationChange?.Rollback();
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

            _callbacks.Log($"Cambio completado a '{profileName}' ({AuthHash.Short(targetIdentityForRestart.Identity.Fingerprint)}).", null);
            _callbacks.Changed();
            _usageGate.Release();
            usageGateHeld = false;
            _ = _callbacks.Refresh(CancellationToken.None);
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
            _callbacks.Log($"Fallo cambiando al perfil '{profileName}'.", ex);
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

    internal async Task RestartAfterFailedSwitchAsync(CodexRuntimeState initialRuntime, bool codexClosedByUs)
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
            _callbacks.Log("No se pudo reiniciar Codex despues de cancelar o revertir el cambio.", ex);
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

    internal async Task EnsureOutgoingWasPreservedAsync(
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

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");
}
