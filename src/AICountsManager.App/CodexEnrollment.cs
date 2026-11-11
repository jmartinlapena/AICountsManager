using System.Text.Json;

namespace AICountsManager;

// A durable reservation, not a duplicate profile. Authentication remains owned by Codex.
internal sealed record PendingCodexAccount(string Name, string? PreviousProfile, string? PreviousIdentity, string Stage, AuthIdentity? PreviousAccount = null);

internal sealed partial class AICountsManagerService
{
    private PendingCodexAccount? _pendingCodex;
    private string EnrollmentPath => Path.Combine(_paths.TransactionsDir, "codex-enrollment.json");
    public string? PendingCodexName => _pendingCodex?.Name;

    private void LoadEnrollment()
    {
        if (!File.Exists(EnrollmentPath)) return;
        if (new FileInfo(EnrollmentPath).Length > 8192) throw new InvalidDataException("El alta pendiente no es válida.");
        var pending = JsonSerializer.Deserialize<PendingCodexAccount>(File.ReadAllBytes(EnrollmentPath))
            ?? throw new InvalidDataException("El alta pendiente no es válida.");
        _profiles.SanitizeProfileName(pending.Name);
        if (pending.PreviousProfile is not null) _profiles.SanitizeProfileName(pending.PreviousProfile);
        if (pending.Stage is not ("prepared" or "waiting")) throw new InvalidDataException("Estado de alta desconocido.");
        _pendingCodex = pending;
    }

    private void SaveEnrollment(PendingCodexAccount pending)
    {
        AtomicFile.WriteText(EnrollmentPath, JsonSerializer.Serialize(pending));
        _pendingCodex = pending;
    }

    private void ClearEnrollment()
    {
        File.Delete(EnrollmentPath);
        _pendingCodex = null;
    }

    private void RequireNoEnrollment()
    {
        if (_pendingCodex is not null)
            throw new InvalidOperationException("Completa o cancela primero el alta pendiente de Codex.");
    }

    private async Task<string> BeginCodexAccountCoreAsync(string name, CancellationToken token = default)
    {
        name = _profiles.SanitizeProfileName(name);
        string message = "";
        await MutateProfilesAsync(async () =>
        {
            RequireNoEnrollment();
            if (!_codexEnabled) throw new InvalidOperationException("Activa Codex en Ajustes antes de añadir una cuenta.");
            if (_profiles.ListProfiles().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Ya existe una cuenta con ese nombre.");
            // Validate before interrupting a client. The same check is repeated after it exits.
            if (File.Exists(_paths.ActiveAuthPath) && !(await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: token)).Identity.IsUsable)
                throw new InvalidOperationException("La sesión actual no tiene una identidad verificable.");
            var runtime = _processController.CaptureState();
            var closed = false;
            var cleared = false;
            AuthSnapshot? outgoing = null;
            string? source = null;
            try
            {
                if (runtime.IsRunning) throw new InvalidOperationException("Cierra Codex antes de añadir una cuenta. Claude puede permanecer abierto.");
                if (_processController.CaptureState().IsRunning) throw new InvalidOperationException("Codex se ha vuelto a abrir. No se ha cambiado su sesión.");
                if (File.Exists(_paths.ActiveAuthPath))
                {
                    outgoing = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: token);
                    if (!outgoing.Identity.IsUsable) throw new InvalidOperationException("No se puede preservar la sesión actual.");
                    var match = await _profiles.FindByIdentityAsync(outgoing, token);
                    source = match?.Name;
                    if (source is null)
                    {
                        var names = _profiles.ListProfiles().Select(p => p.Name).Append(name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        source = "Codex anterior";
                        for (var i = 2; names.Contains(source); i++) source = "Codex anterior " + i;
                        _profiles.AddOrUpdateFromSnapshot(source, outgoing);
                    }
                    else await _profiles.SaveIfNewerAsync(source, outgoing, token);
                    await _codexSwitcher.EnsureOutgoingWasPreservedAsync(source, outgoing, token);
                }
                // File mode prevents a keychain fallback from silently restoring the old login.
                EnsureFileModeConfig();
                var pending = new PendingCodexAccount(name, source, outgoing?.Identity.Fingerprint, "prepared", outgoing?.Identity);
                SaveEnrollment(pending);
                token.ThrowIfCancellationRequested();
                if (_processController.CaptureState().IsRunning) throw new InvalidOperationException("Codex se ha vuelto a abrir. No se ha cambiado su sesión.");
                File.Delete(_paths.ActiveAuthPath);
                cleared = true;
                _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ActiveProfile = null, LastUpdatedAt = UtcNow() });
                SaveEnrollment(pending with { Stage = "waiting" });
            }
            catch
            {
                if (cleared && outgoing is not null && !File.Exists(_paths.ActiveAuthPath))
                {
                    AtomicFile.WriteBytes(_paths.ActiveAuthPath, outgoing.Bytes);
                    _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ActiveProfile = source, LastUpdatedAt = UtcNow() });
                }
                if (!cleared || outgoing is null || File.Exists(_paths.ActiveAuthPath)) ClearEnrollment();
                await _codexSwitcher.RestartAfterFailedSwitchAsync(runtime, closed);
                throw;
            }
            message = "Alta preparada. Abre Codex e inicia sesión; se guardará automáticamente como «" + name + "».";
        }, token);
        return message;
    }

    private Task CancelCodexAccountCoreAsync(CancellationToken token = default) => MutateProfilesAsync(async () =>
    {
        var pending = _pendingCodex;
        if (pending is null) return;
        var runtime = _processController.CaptureState();
        var closed = false;
        try
        {
            if (runtime.IsRunning) throw new InvalidOperationException("Cierra Codex antes de cancelar el alta.");
            if (_processController.CaptureState().IsRunning) throw new InvalidOperationException("Codex sigue abierto.");
            if (File.Exists(_paths.ActiveAuthPath))
            {
                var live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: token);
                if (!IsPreviousAccount(pending, live))
                {
                    await CompleteEnrollmentAsync(live, token);
                    if (_pendingCodex is not null) throw new InvalidOperationException("La sesión ha cambiado. Actualiza antes de cancelar.");
                    return; // A completed login is preserved; cancellation never discards it.
                }
                if (pending.PreviousProfile is not null) await _profiles.SaveIfNewerAsync(pending.PreviousProfile, live, token);
            }
            if (pending.PreviousProfile is not null)
            {
                var previous = await _profiles.LoadProfileSnapshotAsync(pending.PreviousProfile, token);
                if (!IsPreviousAccount(pending, previous)) throw new InvalidOperationException("La cuenta anterior ha cambiado. No se ha sobrescrito ninguna sesión.");
                AtomicFile.WriteBytes(_paths.ActiveAuthPath, previous.Bytes);
                if ((await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: token)).ContentFingerprint != previous.ContentFingerprint)
                    throw new IOException("No se pudo verificar la restauración de la cuenta anterior.");
            }
            else File.Delete(_paths.ActiveAuthPath);
            _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ActiveProfile = pending.PreviousProfile, LastUpdatedAt = UtcNow() });
            ClearEnrollment();
        }
        finally { await _codexSwitcher.RestartAfterFailedSwitchAsync(runtime, closed); }
    }, token);

    // Runs only on auth-file events, at startup, or during an explicit refresh. No polling timer.
    private static bool IsPreviousAccount(PendingCodexAccount pending, AuthSnapshot live) =>
        pending.PreviousAccount?.Matches(live.Identity) ?? (pending.PreviousIdentity == live.Identity.Fingerprint);

    private async Task<bool> CompleteEnrollmentAsync(AuthSnapshot live, CancellationToken token)
    {
        var pending = _pendingCodex;
        if (pending is null) return false;
        if (pending.Stage == "prepared" && IsPreviousAccount(pending, live))
        { ClearEnrollment(); return false; } // Interrupted before the login was cleared.
        if (!live.Identity.IsUsable || IsPreviousAccount(pending, live)) return true;
        var existing = await _profiles.FindByIdentityAsync(live, token);
        if (existing is not null)
        {
            await _profiles.SaveIfNewerAsync(existing.Name, live, token);
            _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ActiveProfile = existing.Name, LastUpdatedAt = UtcNow() });
        }
        else _profiles.AddOrUpdateFromSnapshot(pending.Name, live);
        ClearEnrollment();
        NotifyStateChanged();
        return true;
    }
}
