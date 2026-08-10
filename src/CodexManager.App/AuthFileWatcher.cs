namespace CodexManager;

internal sealed class AuthFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<CancellationToken, Task> _onStableChange;
    private readonly Action<string, Exception?> _log;
    private readonly object _sync = new();
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public AuthFileWatcher(
        string authPath,
        Func<CancellationToken, Task> onStableChange,
        Action<string, Exception?> log)
    {
        _onStableChange = onStableChange;
        _log = log;
        var directory = Path.GetDirectoryName(authPath)
            ?? throw new InvalidOperationException("auth.json no tiene un directorio valido.");
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(authPath))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public void ScheduleInitialReconciliation() => Schedule();

    private void OnChanged(object? sender, FileSystemEventArgs e) => Schedule();

    private void OnRenamed(object? sender, RenamedEventArgs e) => Schedule();

    private void OnError(object? sender, ErrorEventArgs e)
    {
        _log("FileSystemWatcher notifico un error; se programara una reconciliacion.", e.GetException());
        Schedule();
    }

    private void Schedule()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _debounce?.Cancel();
            _debounce?.Dispose();
            debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _debounce = debounce;
        }

        _ = RunDebouncedAsync(debounce);
    }

    private async Task RunDebouncedAsync(CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(850), debounce.Token);
            await _onStableChange(debounce.Token);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log("No se pudo reconciliar auth.json despues de un cambio.", ex);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
            _lifetime.Cancel();
            _debounce?.Cancel();
        }

        _watcher.Dispose();
        _debounce?.Dispose();
        _lifetime.Dispose();
    }
}
