namespace AICountsManager;

// One explicit asynchronous startup. Cancelling a caller does not cancel other callers.
internal sealed class ServiceInitialization(Func<CancellationToken, Task> initialize) : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _task;
    private bool _disposed;
    internal bool IsRunning { get { lock (_sync) return _task is { IsCompleted: false }; } }

    internal Task RunAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task task;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            task = _task ??= Task.Run(() => initialize(_lifetime.Token));
        }
        return token.CanBeCanceled ? task.WaitAsync(token) : task;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
            if (_task is null || _task.IsCompleted) _lifetime.Dispose();
            else _ = _task.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}

// Preserve the existing lock order: account mutation -> quota activity -> credentials.
internal sealed class AccountOperationCoordinator
{
    internal SemaphoreSlim Mutations { get; } = new(1, 1);
    internal SemaphoreSlim Quotas { get; } = new(1, 1);
    internal SemaphoreSlim Credentials { get; } = new(1, 1);
    internal bool IsMutating => Mutations.CurrentCount == 0;

    internal async Task RunAsync(Func<Task> action, Action changed, CancellationToken token)
    {
        await Mutations.WaitAsync(token).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { Mutations.Release(); changed(); }
    }
}
