namespace AICountsManager;

// Legacy entry points keep the UI unchanged; all named-account commands use the same contract.
internal sealed partial class AICountsManagerService
{
    internal AccountProviderRegistry AccountProviders => _accountProviders;

    private IAccountManager CreateCodexAccountProvider() => new ManagedAccountProvider(ProviderIds.Codex,
        AccountCapabilities.Reauthenticate, () => _codexEnabled, () => PendingCodexName, ReadCodexAccounts,
        SetCodexEnabledAsync, async (all, token) => { if (all) await RefreshAllUsageCoreAsync(null, token, includeOtherProviders: false); else await RefreshActiveProfileAsync(token); }, new(
            async (name, token) => { await InitializeAsync(token); return await BeginCodexAccountCoreAsync(name, token); }, CancelCodexAccountCoreAsync,
            async (id, reauthenticate, token) =>
            {
                await InitializeAsync(token);
                return AccountActivationResult.FromCodex(reauthenticate
                    ? await _codexSwitcher.PrepareReauthenticationAsync(id, token) : await _codexSwitcher.SwitchToProfileAsync(id, token));
            },
            RenameProfileCoreAsync, DeleteProfileCoreAsync));

    private IReadOnlyList<ProviderUsage> ReadCodexAccounts()
    {
        var cache = _profiles.LoadUsageCache(); var active = _profiles.LoadAppConfig().ActiveProfile;
        return _profiles.ListProfiles().Select(p =>
        {
            var u = GetUsage(cache, p.Name);
            return new ProviderUsage(ProviderIds.Codex, p.IdentityFingerprint, p.Name, p.Email, u.PlanType, u.CheckedAt,
                BuildStatus(u), u.Error, [new("session", "Codex", "Sesión · 5 h", u.Session.UsedPercent, u.Session.WindowDurationMins, u.Session.ResetsAt),
                    new("weekly", "Codex", "Semanal", u.Weekly.UsedPercent, u.Weekly.WindowDurationMins, u.Weekly.ResetsAt)],
                u.HttpStatus, u.RetryAfterSeconds, u.SessionLimitNotApplicable, p.Name, string.Equals(active, p.Name, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }

    public async Task<string?> BeginProviderAccountAsync(string provider, string name, CancellationToken token = default)
    { await InitializeAsync(token); return await _accountProviders.Manager(provider).BeginAsync(name, token); }
    public async Task CancelProviderAccountAsync(string provider, CancellationToken token = default)
    { await InitializeAsync(token); await _accountProviders.Manager(provider).CancelAsync(token); }
    public async Task<AccountActivationResult> ActivateProviderAccountAsync(string provider, string id, bool reauthenticate = false, CancellationToken token = default)
    { await InitializeAsync(token); return await _accountProviders.Manager(provider).ActivateAsync(id, reauthenticate, token); }
    public async Task RenameProviderAccountAsync(string provider, string id, string name, CancellationToken token = default)
    { await InitializeAsync(token); await _accountProviders.Manager(provider).RenameAsync(id, name, token); }
    public async Task DeleteProviderAccountAsync(string provider, string id, CancellationToken token = default)
    { await InitializeAsync(token); await _accountProviders.Manager(provider).DeleteAsync(id, token); }

    public async Task<string> BeginCodexAccountAsync(string name, CancellationToken token = default) =>
        await BeginProviderAccountAsync(ProviderIds.Codex, name, token) ?? "";
    public Task CancelCodexAccountAsync(CancellationToken token = default) => CancelProviderAccountAsync(ProviderIds.Codex, token);
    public async Task BeginClaudeAccountAsync(string name) => await BeginProviderAccountAsync(ProviderIds.Claude, name);
    public Task CancelClaudeAccountAsync() => CancelProviderAccountAsync(ProviderIds.Claude);
    public async Task SwitchClaudeAccountAsync(string id) => await ActivateProviderAccountAsync(ProviderIds.Claude, id);
    public Task RenameClaudeAccountAsync(string id, string name) => RenameProviderAccountAsync(ProviderIds.Claude, id, name);
    public Task DeleteClaudeAccountAsync(string id) => DeleteProviderAccountAsync(ProviderIds.Claude, id);
    public Task RenameProfileAsync(string currentName, string newName, CancellationToken cancellationToken = default) => RenameProviderAccountAsync(ProviderIds.Codex, currentName, newName, cancellationToken);
    public Task DeleteProfileAsync(string profileName, CancellationToken cancellationToken = default) => DeleteProviderAccountAsync(ProviderIds.Codex, profileName, cancellationToken);
    public async Task<SwitchProfileResult> SwitchToProfileAsync(string profileName, CancellationToken cancellationToken = default) =>
        (await ActivateProviderAccountAsync(ProviderIds.Codex, profileName, token: cancellationToken)).ToCodex();
    public async Task<SwitchProfileResult> PrepareReauthenticationAsync(string profileName, CancellationToken cancellationToken = default) =>
        (await ActivateProviderAccountAsync(ProviderIds.Codex, profileName, true, cancellationToken)).ToCodex();

    private async Task SetCodexEnabledAsync(bool enabled, CancellationToken token)
    {
        await MutateProfilesAsync(() =>
        {
            if (!enabled) RequireNoEnrollment();
            _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { CodexEnabled = enabled, LastUpdatedAt = UtcNow() });
            _codexEnabled = enabled; _authWatcher?.SetEnabled(enabled);
            return Task.CompletedTask;
        }, token);
        if (enabled) { await ReconcileActiveAuthAsync(token); await RefreshActiveProfileAsync(token); }
    }
}
