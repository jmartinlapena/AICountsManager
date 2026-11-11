using System.Text.Json;
using System.Text.RegularExpressions;

namespace AICountsManager;

internal sealed partial class AICountsManagerService : IDisposable
{
    private readonly AppPaths _paths;
    internal AppDiagnostics Diagnostics { get; }
    private readonly ProfileRepository _profiles;
    private readonly ICodexProcessController _processController;
    private readonly CodexAccountSwitcher _codexSwitcher;
    private readonly ProviderComposition _composition;
    private readonly AccountProviderRegistry _accountProviders;
    private readonly WindowsIntegration _windows;
    private readonly CodexQuotaProvider _codexProvider;
    private IReadOnlyList<ProviderUsage> OtherAccounts => _accountProviders.ReadingsExcept(ProviderIds.Codex);
    internal bool CanManageClaude => _accountProviders.CanManage(ProviderIds.Claude);
    public string? PendingClaudeName => _accountProviders.PendingName(ProviderIds.Claude);
    private bool ProviderEnabled(string id) => _accountProviders.IsEnabled(id);
    private readonly Dictionary<string, IProviderTokenHistory> _providerHistories;
    public bool AntigravityEnabled => _accountProviders.IsEnabled(ProviderIds.Antigravity);
    private volatile bool _codexEnabled;
    public bool CodexEnabled => _codexEnabled;
    public IReadOnlyList<CurrentProviderSetting> CurrentProviders => _accountProviders.Settings;
    public Task SetAntigravityEnabledAsync(bool enabled) => SetCurrentProviderEnabledAsync(ProviderIds.Antigravity, enabled);
    public async Task SetCurrentProviderEnabledAsync(string id, bool enabled)
    {
        await InitializeAsync();
        await _accountProviders.SetEnabledAsync(id, enabled, CancellationToken.None);
        NotifyStateChanged();
    }
    public async Task RefreshCurrentProvidersAsync(CancellationToken token = default)
    {
        await InitializeAsync(token);
        try { await RefreshActiveProfileAsync(token); }
        finally { await _accountProviders.RefreshExceptAsync(ProviderIds.Codex, false, token); if (!_disposed) NotifyStateChanged(); }
    }
    private async Task<UsageRecord> ReadCodexUsageAsync(AuthSnapshot snapshot, CancellationToken token) =>
        CodexQuotaProvider.Legacy(await QuotaQuery.ReadAsync(_codexProvider, new CodexQuotaRequest(snapshot, _paths.CodexConfigPath), token));
    private readonly AccountOperationCoordinator _operations = new();
    private SemaphoreSlim _credentialGate => _operations.Credentials;
    private SemaphoreSlim _switchGate => _operations.Mutations;
    private SemaphoreSlim _usageGate => _operations.Quotas;
    private AuthFileWatcher? _authWatcher;
    private bool _disposed;
    private readonly TokenHistory _history;
    private DateTimeOffset _nextBatchAllowed;
    public TokenHistorySnapshot History
    {
        get
        {
            var sources = new Dictionary<string, TokenHistorySnapshot>();
            if (_codexEnabled) sources[ProviderIds.Codex] = _history.Snapshot;
            foreach (var provider in _providerHistories)
                if (ProviderEnabled(provider.Key)) sources[provider.Key] = provider.Value.Snapshot;
            return sources.Count == 1 && _codexEnabled ? _history.Snapshot : ProviderHistory.Merge(sources);
        }
    }
    public async Task<TokenHistorySnapshot> RefreshHistoryAsync(CancellationToken token = default)
    {
        await InitializeAsync(token);
        if (_codexEnabled) await _history.RefreshAsync(token);
        foreach (var provider in _providerHistories)
            if (ProviderEnabled(provider.Key)) await provider.Value.RefreshAsync(token);
        return History;
    }


    public event EventHandler? StateChanged;

    public AICountsManagerService()
        : this(AppPaths.Build(), null, null, null, startWatcher: true)
    {
    }

    internal AICountsManagerService(
        AppPaths paths,
        ICodexProcessController? processController,
        ICodexAppServerClient? appServer,
        IUsageClient? usageClient,
        bool startWatcher, IQuotaProvider? currentProvider = null, string? geminiHome = null, IEnumerable<IQuotaProvider>? additionalProviders = null, ClaudeAccounts? claudeAccounts = null)
    {
        _paths = paths;
        _startWatcher = startWatcher;
        _windows = new WindowsIntegration(paths);
        Diagnostics = new AppDiagnostics(paths.LogsDir);
        _history = new TokenHistory(paths.CodexHome, Path.Combine(paths.AppHome, "cache", "token_history.json"));
        _paths.EnsureDirectories();
        _profiles = new ProfileRepository(paths, Log);
        _codexEnabled = _profiles.LoadAppConfig().CodexEnabled;
        LoadEnrollment();
        _processController = processController ?? new CodexProcessController();
        var codexAppServer = appServer ?? new CodexAppServerClient(paths);
        _codexProvider = new CodexQuotaProvider(usageClient ?? new UsageClient());
        _composition = new ProviderComposition(paths, startWatcher, currentProvider, geminiHome, additionalProviders,
            claudeAccounts, _profiles.SanitizeProfileName, NotifyStateChanged, Log);
        _providerHistories = _composition.Histories;
        _codexSwitcher = new CodexAccountSwitcher(paths, _profiles, _processController, codexAppServer, _operations,
            new(() => _codexEnabled, RequireNoEnrollment, ThrowIfDisposed, NotifyStateChanged,
                token => RefreshActiveProfileAsync(token), Log));
        _initialization = new ServiceInitialization(InitializeCoreAsync);
        _accountProviders = new AccountProviderRegistry(new[] { CreateCodexAccountProvider() }.Concat(_composition.AccountProviders(_operations, NotifyStateChanged, InitializeAsync)));
    }

    public DashboardState BuildDashboardState()
    {
        var profiles = _codexEnabled ? _profiles.ListProfiles() : Array.Empty<ProfileMeta>();
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
        var binary = _codexEnabled ? CodexBinaryLocator.Find() : null;
        var runtime = _codexEnabled ? _processController.CaptureState() : new CodexRuntimeState(false, null, []);
        var settings = !_codexEnabled ? "Codex desactivado · perfiles conservados" : binary is null
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
            UpdatedLabel: FormatRelative(usageCache.Accounts.Values.Select(u => u.CheckedAt).Concat(OtherAccounts.Select(a => a.CheckedAt))
                .Where(value => DateTimeOffset.TryParse(value, out _)).OrderByDescending(value => DateTimeOffset.Parse(value!)).FirstOrDefault()),
            OtherAccounts: OtherAccounts, AccountOrder: config.AccountOrder);
    }

    public string BuildTrayTooltip(DashboardState state)
    {
        if (!_codexEnabled) return "AICountsManager";
        var active = state.ActiveProfile;
        var session = active.SessionRemainingPercent.HasValue
            ? $"{active.SessionRemainingPercent.Value:0}% restante"
            : "sin datos";
        var text = $"AICountsManager - {active.Name} - {session}";
        return text.Length <= 63 ? text : text[..63];
    }

    public bool IsCodexRunning() => _processController.CaptureState().IsRunning;

    public bool StrictVerificationEnabled() => _profiles.LoadAppConfig().StrictVerification;

    public bool IsMutationInProgress => _operations.IsMutating || _initialization.IsRunning;

    public Task SetProfileOrderAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(() =>
        {
            var existing = _profiles.ListProfiles().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count != existing.Count || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count || !existing.SetEquals(names))
                throw new InvalidOperationException("La lista de cuentas ha cambiado. Vuelve a ordenar las cuentas actuales.");
            var config = _profiles.LoadAppConfig();
            _profiles.SaveAppConfig(config with { ProfileOrder = names.ToArray(), AccountOrder = AccountOrdering.ReplaceVisible(config.AccountOrder, names.Select(AccountOrdering.Codex).ToArray()), LastUpdatedAt = UtcNow() });
            return Task.CompletedTask;
        }, cancellationToken);

    public Task SetAccountOrderAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(() =>
        {
            var profiles = _codexEnabled ? _profiles.ListProfiles() : Array.Empty<ProfileMeta>();
            var existing = profiles.Select(p => AccountOrdering.Codex(p.Name)).Concat(OtherAccounts.Select(AccountOrdering.For)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ids.Count != existing.Count || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count || !existing.SetEquals(ids))
                throw new InvalidOperationException("La lista de cuentas ha cambiado. Vuelve a ordenar las cuentas actuales.");
            var config = _profiles.LoadAppConfig();
            _profiles.SaveAppConfig(config with
            {
                AccountOrder = AccountOrdering.ReplaceVisible(config.AccountOrder, ids),
                ProfileOrder = _codexEnabled ? ids.Where(id => id.StartsWith("codex:", StringComparison.OrdinalIgnoreCase)).Select(id => profiles.Single(p => string.Equals(AccountOrdering.Codex(p.Name), id, StringComparison.OrdinalIgnoreCase)).Name).ToArray() : config.ProfileOrder,
                LastUpdatedAt = UtcNow()
            });
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
        await InitializeAsync(cancellationToken);
        if (!_codexEnabled) return null;
        if (!await _usageGate.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        string? requestedProfile = null;
        try
        {
            var config = _profiles.LoadAppConfig();
            if (!_codexEnabled) return null;
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
            var usage = await ReadCodexUsageAsync(live, cancellationToken);

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
    public Task<string> RefreshAllUsageAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        RefreshAllUsageCoreAsync(progress, cancellationToken, includeOtherProviders: true);

    private async Task<string> RefreshAllUsageCoreAsync(IProgress<string>? progress, CancellationToken cancellationToken, bool includeOtherProviders)
    {
        await InitializeAsync(cancellationToken);
        ThrowIfDisposed();
        if (_pendingCodex is not null) await ReconcileActiveAuthAsync(cancellationToken);
        if (!await _usageGate.WaitAsync(0, cancellationToken)) return "Ya hay una actualización en curso.";
        try
        {
            if (DateTimeOffset.UtcNow < _nextBatchAllowed)
                return "Espera " + UsagePresentation.Duration((_nextBatchAllowed - DateTimeOffset.UtcNow).TotalSeconds) + " antes de volver a consultar.";
            var profiles = _codexEnabled ? _profiles.ListProfiles() : Array.Empty<ProfileMeta>();
            Diagnostics.Write("quota.batch.begin count=" + profiles.Count);
            AuthSnapshot? live = null;
            try { if (_codexEnabled && File.Exists(_paths.ActiveAuthPath)) live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken); }
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
                        return await ReadCodexUsageAsync(snapshot, timeout.Token);
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
            if (includeOtherProviders && !deadline.IsCancellationRequested)
            {
                try { await _accountProviders.RefreshExceptAsync(ProviderIds.Codex, true, deadline.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            _nextBatchAllowed = _nextBatchAllowed > DateTimeOffset.UtcNow ? _nextBatchAllowed : DateTimeOffset.UtcNow.AddSeconds(15);
            NotifyStateChanged();
            Diagnostics.Write($"quota.batch.complete completed={completed} failed={failed} limited={limited}");
            if (profiles.Count == 0) return OtherAccounts.Count != 0 ? "Actualización terminada. Consulta el resultado en cada cuenta." : "Historial actualizado. Añade una cuenta para consultar sus cuotas.";
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
            RequireNoEnrollment();
            if (!File.Exists(_paths.ActiveAuthPath))
                throw new InvalidOperationException("No existe una sesión activa de Codex para guardar.");
            var snapshot = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
            if (!snapshot.Identity.IsUsable)
                throw new InvalidOperationException("La sesión no contiene una identidad de cuenta verificable.");
            _profiles.AddOrUpdateFromSnapshot(profileName, snapshot);
            Log($"Perfil '{profileName}' guardado para identidad {AuthHash.Short(snapshot.Identity.Fingerprint)}.", null);
        }, cancellationToken);

    private Task RenameProfileCoreAsync(string currentName, string newName, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(() =>
        {
            RequireNoEnrollment();
            _profiles.Rename(currentName, newName);
            return Task.CompletedTask;
        }, cancellationToken);

    private Task DeleteProfileCoreAsync(string profileName, CancellationToken cancellationToken = default) =>
        MutateProfilesAsync(async () =>
        {
            RequireNoEnrollment();
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
        await InitializeAsync(cancellationToken);
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

    private async Task ReconcileActiveAuthAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !_codexEnabled)
        {
            return;
        }

        await _credentialGate.WaitAsync(cancellationToken);
        try
        {
            using var globalLock = new GlobalAuthSwitchLock(_paths.CodexHome);
            await globalLock.AcquireAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (!_codexEnabled) return;
            if (!File.Exists(_paths.ActiveAuthPath))
            {
                _profiles.SaveAppConfig(_profiles.LoadAppConfig() with { ActiveProfile = null, LastUpdatedAt = UtcNow() });
                NotifyStateChanged();
                return;
            }
            var live = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
            if (await CompleteEnrollmentAsync(live, cancellationToken)) { NotifyStateChanged(); return; }
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

    public bool StartupShortcutExists() => _windows.StartupShortcutExists();
    public void SetStartupEnabled(bool enabled) { _windows.SetStartupEnabled(enabled); NotifyStateChanged(); }
    public void OpenFolder(string path) => _windows.OpenFolder(path);

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
            PlanType: usage.PlanType, SessionLimitNotApplicable: usage.SessionLimitNotApplicable, CheckedAt: usage.CheckedAt);
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
        _initialization.Dispose();
        lock (_watcherSync) _authWatcher?.Dispose();
        _composition.Dispose();
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
