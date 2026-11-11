namespace AICountsManager;

// Composition owns the HTTP client it creates. Externally supplied clients remain externally owned.
internal sealed class ProviderComposition : IDisposable
{
    private readonly HttpClient _http = ProviderHttp.Create();
    internal CurrentProviderRegistry Current { get; }
    internal ClaudeAccounts? Claude { get; }
    internal Dictionary<string, IProviderTokenHistory> Histories { get; }

    internal ProviderComposition(AppPaths paths, bool defaults, IQuotaProvider? current, string? geminiHome,
        IEnumerable<IQuotaProvider>? additional, ClaudeAccounts? claude, Func<string, string> name,
        Action changed, Action<string, Exception?> log)
    {
        var cursor = new CursorSessionSource(CursorSessionSource.Database);
        var primary = current ?? (defaults ? AntigravityCloudProvider.Create() : null);
        var extras = additional ?? (defaults ? new IQuotaProvider[] { new CursorQuotaProvider(cursor, _http) } : []);
        Current = new CurrentProviderRegistry(paths, (primary is null ? Array.Empty<IQuotaProvider>() : [primary]).Concat(extras));
        Claude = claude ?? (defaults && additional is null ? new ClaudeAccounts(paths.AppHome,
            new ClaudeSessionArchive(ClaudeDesktopSessionSource.DirectoryPath, ClaudeSessionSource.Home, ClaudeSessionArchive.CodeConfigPath,
                Path.Combine(paths.TransactionsDir, "claude-rollback.bin"), _http), _http, name, changed, log) : null);
        Histories = new()
        {
            [ProviderIds.Antigravity] = new AntigravityTokenHistory(geminiHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini"), Path.Combine(paths.AppHome, "cache", "antigravity_history.json")),
            [ProviderIds.Claude] = new ClaudeTokenHistory(ClaudeSessionSource.Home, Path.Combine(paths.AppHome, "cache", "claude_history.json")),
            [ProviderIds.Cursor] = new CursorTokenHistory(cursor, _http, Path.Combine(paths.AppHome, "cache", "cursor_history.json"))
        };
    }

    internal IEnumerable<IAccountProvider> AccountProviders(AccountOperationCoordinator operations, Action changed, Func<CancellationToken, Task> initialize)
    {
        foreach (var store in Current.Stores) yield return new CurrentAccountProvider(Current, store);
        if (Claude is not { } accounts) yield break;
        async Task Mutate(Func<Task> action, CancellationToken token) { await initialize(token); await operations.RunAsync(action, changed, token); }
        yield return new ManagedAccountProvider(ProviderIds.Claude, 0, () => accounts.Enabled, () => accounts.PendingName,
            () => accounts.Accounts,
            async (enabled, token) => { await initialize(token); await accounts.SetEnabledAsync(enabled, token); },
            async (all, token) => { await initialize(token); await accounts.RefreshAsync(all, token); }, new(
                async (name, token) => { await Mutate(() => accounts.BeginAsync(name, token), token); return null; },
                token => Mutate(() => accounts.CancelAsync(token), token),
                async (id, _, token) => { await Mutate(() => accounts.SwitchAsync(id, token), token); return new(AccountActivationStatus.Success, "Cuenta de Claude activada. La aplicación permanece cerrada.", null, id); },
                (id, name, token) => Mutate(() => accounts.RenameAsync(id, name, token), token),
                (id, token) => Mutate(() => accounts.DeleteAsync(id, token), token)));
    }

    public void Dispose() { Claude?.Dispose(); _http.Dispose(); }
}
