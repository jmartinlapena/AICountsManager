namespace AICountsManager;

internal sealed partial class AICountsManagerService
{
    private readonly ServiceInitialization _initialization;
    private readonly bool _startWatcher;
    private readonly object _watcherSync = new();
    public Task InitializeAsync(CancellationToken token = default) => _initialization.RunAsync(token);

    private async Task InitializeCoreAsync(CancellationToken token)
    {
        try
        {
            await _profiles.RecoverJournalAsync(token).ConfigureAwait(false);
            if (_codexSwitcher.HasRecovery && !_processController.CaptureState().IsRunning)
            {
                using var recoveryLock = new GlobalAuthSwitchLock(_paths.CodexHome);
                await recoveryLock.AcquireAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                await _codexSwitcher.RecoverAsync(token).ConfigureAwait(false);
            }
            await ReconcileActiveAuthAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log("La reconciliacion inicial no se pudo completar; no se modificaron credenciales.", ex); }

        token.ThrowIfCancellationRequested();
        lock (_watcherSync)
        {
            if (_disposed) return;
            if (_startWatcher)
            {
                _authWatcher = new AuthFileWatcher(_paths.ActiveAuthPath, ReconcileActiveAuthAsync, Log);
                _authWatcher.SetEnabled(_codexEnabled);
                if (_codexEnabled) _authWatcher.ScheduleInitialReconciliation();
            }
        }
        Log("AICountsManager iniciado con sincronizacion por eventos.", null);
        if (!_disposed) NotifyStateChanged();
    }
}
