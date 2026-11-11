namespace AICountsManager;

[Flags]
internal enum AccountCapabilities { Query = 1, NamedAccounts = 2, Reauthenticate = 4 }
internal enum AccountActivationStatus { Success, ClientRunning, ReauthenticationRequired, RestartFailed, Failed }
internal sealed record AccountActivationResult(AccountActivationStatus Status, string Message,
    string? PreviousAccount, string TargetAccount, bool ClientRestarted = false)
{
    internal static AccountActivationResult FromCodex(SwitchProfileResult result) => new(result.Status switch
    {
        SwitchProfileStatus.Success => AccountActivationStatus.Success,
        SwitchProfileStatus.CodexStillRunning => AccountActivationStatus.ClientRunning,
        SwitchProfileStatus.RequiresReauthentication => AccountActivationStatus.ReauthenticationRequired,
        SwitchProfileStatus.RestartFailed => AccountActivationStatus.RestartFailed,
        _ => AccountActivationStatus.Failed
    }, result.Message, result.PreviousProfile, result.TargetProfile, result.CodexWasRestarted);
    internal SwitchProfileResult ToCodex() => new(Status switch
    {
        AccountActivationStatus.Success => SwitchProfileStatus.Success,
        AccountActivationStatus.ClientRunning => SwitchProfileStatus.CodexStillRunning,
        AccountActivationStatus.ReauthenticationRequired => SwitchProfileStatus.RequiresReauthentication,
        AccountActivationStatus.RestartFailed => SwitchProfileStatus.RestartFailed,
        _ => SwitchProfileStatus.Failed
    }, Message, PreviousAccount, TargetAccount, ClientRestarted);
}

internal interface IAccountProvider
{
    string Id { get; }
    bool Enabled { get; }
    AccountCapabilities Capabilities { get; }
    IReadOnlyList<ProviderUsage> Readings { get; }
    Task SetEnabledAsync(bool enabled, CancellationToken token);
    Task RefreshAsync(bool all, CancellationToken token);
}

internal interface IAccountManager : IAccountProvider
{
    string? PendingName { get; }
    Task<string?> BeginAsync(string name, CancellationToken token);
    Task CancelAsync(CancellationToken token);
    Task<AccountActivationResult> ActivateAsync(string id, bool reauthenticate, CancellationToken token);
    Task RenameAsync(string id, string name, CancellationToken token);
    Task DeleteAsync(string id, CancellationToken token);
}

internal sealed class AccountProviderRegistry
{
    private readonly IReadOnlyList<IAccountProvider> _ordered;
    private readonly Dictionary<string, IAccountProvider> _providers;
    internal AccountProviderRegistry(IEnumerable<IAccountProvider> providers)
    {
        _ordered = providers.ToArray();
        _providers = _ordered.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (_ordered.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Id, "^[a-z0-9-]{1,40}$")
            || p.Capabilities.HasFlag(AccountCapabilities.NamedAccounts) != (p is IAccountManager)))
            throw new ArgumentException("Contrato de cuentas de proveedor no válido.");
    }
    internal IReadOnlyList<CurrentProviderSetting> Settings => _ordered.Select(p => new CurrentProviderSetting(p.Id, ProviderIds.Name(p.Id), p.Enabled)).ToArray();
    internal bool IsEnabled(string id) => _providers.TryGetValue(id, out var p) && p.Enabled;
    internal bool CanManage(string id) => _providers.GetValueOrDefault(id) is IAccountManager;
    internal string? PendingName(string id) => (_providers.GetValueOrDefault(id) as IAccountManager)?.PendingName;
    internal IAccountManager Manager(string id) => _providers.GetValueOrDefault(id) as IAccountManager
        ?? throw new InvalidOperationException(ProviderIds.Name(id) + " no está disponible.");
    internal IReadOnlyList<ProviderUsage> ReadingsExcept(string id) => _ordered.Where(p => p.Id != id && p.Enabled).SelectMany(p => p.Readings).ToArray();
    internal Task SetEnabledAsync(string id, bool enabled, CancellationToken token) => _providers.TryGetValue(id, out var p) ? p.SetEnabledAsync(enabled, token) : Task.CompletedTask;
    internal async Task RefreshExceptAsync(string id, bool all, CancellationToken token)
    {
        // Keep the existing read-only batch of two, followed by the saved-account managers.
        var active = _ordered.Where(p => p.Id != id && p.Enabled).ToArray();
        foreach (var batch in active.Where(p => p is not IAccountManager).Chunk(2))
            await Task.WhenAll(batch.Select(p => p.RefreshAsync(all, token))).ConfigureAwait(false);
        foreach (var provider in active.OfType<IAccountManager>()) await provider.RefreshAsync(all, token).ConfigureAwait(false);
    }
}

internal sealed class CurrentAccountProvider(CurrentProviderRegistry registry, CurrentProviderStore store) : IAccountProvider
{
    public string Id => store.Id;
    public bool Enabled => store.Enabled;
    public AccountCapabilities Capabilities => AccountCapabilities.Query;
    public IReadOnlyList<ProviderUsage> Readings => Enabled ? [store.Snapshot ?? ProviderUsage.Unavailable(Id, "Pendiente de consultar la sesión actual")] : [];
    public Task SetEnabledAsync(bool enabled, CancellationToken token) => registry.SetEnabledAsync(Id, enabled, token);
    public Task RefreshAsync(bool all, CancellationToken token) => registry.RefreshOneAsync(store, token);
}

internal sealed record AccountManagerActions(Func<string, CancellationToken, Task<string?>> Begin,
    Func<CancellationToken, Task> Cancel, Func<string, bool, CancellationToken, Task<AccountActivationResult>> Activate,
    Func<string, string, CancellationToken, Task> Rename, Func<string, CancellationToken, Task> Delete);

internal sealed class ManagedAccountProvider(string id, AccountCapabilities capabilities, Func<bool> enabled, Func<string?> pending,
    Func<IReadOnlyList<ProviderUsage>> readings, Func<bool, CancellationToken, Task> setEnabled,
    Func<bool, CancellationToken, Task> refresh, AccountManagerActions actions) : IAccountManager
{
    public string Id => id;
    public bool Enabled => enabled();
    public AccountCapabilities Capabilities => capabilities | AccountCapabilities.Query | AccountCapabilities.NamedAccounts;
    public string? PendingName => pending();
    public IReadOnlyList<ProviderUsage> Readings => Enabled ? readings() : [];
    public Task SetEnabledAsync(bool value, CancellationToken token) => setEnabled(value, token);
    public Task RefreshAsync(bool all, CancellationToken token) => refresh(all, token);
    public Task<string?> BeginAsync(string name, CancellationToken token) => actions.Begin(name, token);
    public Task CancelAsync(CancellationToken token) => actions.Cancel(token);
    public Task<AccountActivationResult> ActivateAsync(string account, bool reauthenticate, CancellationToken token)
    {
        if (reauthenticate && !Capabilities.HasFlag(AccountCapabilities.Reauthenticate)) throw new InvalidOperationException("Este proveedor renueva la sesión desde su cliente.");
        return actions.Activate(account, reauthenticate, token);
    }
    public Task RenameAsync(string account, string name, CancellationToken token) => actions.Rename(account, name, token);
    public Task DeleteAsync(string account, CancellationToken token) => actions.Delete(account, token);
}
