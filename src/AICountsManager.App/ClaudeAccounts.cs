using System.Diagnostics;
using System.Text.Json;

namespace AICountsManager;

internal sealed record ClaudeAccount(string Id, string Name, string Account, string Organization, string? Email, string? Plan)
{
    public string Identity => Account + ":" + Organization;
}
internal sealed record ClaudeEnrollment(string Name, string? PreviousId, string? PreviousIdentity);
internal sealed record ClaudeAccountState(IReadOnlyList<ClaudeAccount> Accounts, string? ActiveId = null, ClaudeEnrollment? Pending = null);
internal sealed class FixedClientSession(ClientSession? session) : IClientSessionSource { public ClientSession? Read() => session; }

internal sealed class ClaudeAccounts : IDisposable
{
    private readonly ClaudeSessionArchive _archive;
    private readonly LocalOrganizationTransfer _organization;
    private readonly string _root, _index, _cache, _config;
    private readonly HttpClient _http;
    private readonly Func<string, string> _name;
    private readonly Action _changed;
    private readonly Action<string, Exception?> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<AuthFileWatcher> _watchers = [];
    private readonly Dictionary<int, Process> _waitingProcesses = [];
    private readonly Dictionary<string, DateTimeOffset> _next = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ClaudeAccountState _state = new([]);
    private Dictionary<string, ProviderUsage> _readings = [];
    private ProviderUsage? _current;
    private const string CurrentCacheKey = "@current:";
    private const int MaxAccounts = 100;
    private const string IndexUnavailable = "No se pudo leer el índice de Claude. Los perfiles guardados se conservan.";
    private readonly bool _indexUnavailable;
    private DateTimeOffset _providerNext;
    private volatile bool _enabled;
    private bool _disposed;
    public bool Enabled => _enabled;
    public string? PendingName => _state.Pending?.Name;
    internal ClaudeAccountState State => _state;

    public ClaudeAccounts(string appHome, ClaudeSessionArchive archive, HttpClient http,
        Func<string, string> name, Action changed, Action<string, Exception?> log)
    {
        _archive = archive; _http = http; _name = name; _changed = changed; _log = log;
        _organization = new LocalOrganizationTransfer(new ClaudeCodeOrganizationAdapter(archive.DesktopHome),
            Path.Combine(appHome, "transactions"), Path.Combine(appHome, "backups"), archive.RequireClosed);
        _root = Path.Combine(appHome, "provider-accounts", "claude");
        _index = Path.Combine(_root, "accounts.json"); _cache = Path.Combine(appHome, "cache", "claude-accounts.json");
        _config = Path.Combine(appHome, "config", "provider-claude.json");
        try
        {
            if (Directory.Exists(_index)) throw new InvalidDataException("El índice de Claude debe ser un archivo.");
            if (ClaudeSessionArchive.ReadFile(_index, 256 * 1024) is { } stored)
            {
                var state = JsonSerializer.Deserialize<ClaudeAccountState>(stored);
                if (state?.Accounts is not { Count: <= MaxAccounts } accounts || accounts.Any(a => a is null
                    || !Guid.TryParseExact(a.Id, "N", out _) || !Guid.TryParse(a.Account, out _) || !Guid.TryParse(a.Organization, out _))
                    || accounts.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != accounts.Count
                    || accounts.Select(a => a.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != accounts.Count)
                    throw new InvalidDataException("Índice de Claude inválido.");
                foreach (var account in accounts) _name(account.Name);
                if (state.Pending is { } pending) _name(pending.Name);
                _state = state;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { _indexUnavailable = true; _log(IndexUnavailable, ex); }
        try
        {
            if (File.Exists(_config)) _enabled = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllBytes(_config))?.GetValueOrDefault(ProviderIds.Claude) == true;
            if (!_indexUnavailable && ClaudeSessionArchive.ReadFile(_cache, 1024 * 1024) is { } cached)
            {
                using var cache = JsonDocument.Parse(cached);
                _readings = cache.RootElement.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, ProviderUsage.ReadCache(p.Value)))
                .Where(p => p.Value is { Provider: ProviderIds.Claude }
                    && (p.Key.StartsWith(CurrentCacheKey, StringComparison.Ordinal)
                        || _state.Accounts.Any(a => a.Id == p.Key && a.Identity == p.Value.Identity)))
                .ToDictionary(p => p.Key, p => p.Value! with { Status = "stale", Error = "Última lectura · pendiente de actualizar", Resets = p.Value!.Resets?.Stale() });
            }
            if (_enabled && _archive.LiveSource.Read() is { } session
                && _readings.GetValueOrDefault(CurrentCacheKey + session.Fingerprint) is { } current) _current = current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException) { _log("No se pudo leer la caché de Claude.", ex); }
        if (_enabled && !_indexUnavailable) StartWatchers();
    }

    public IReadOnlyList<ProviderUsage> Accounts
    {
        get
        {
            if (!_enabled) return [];
            if (_indexUnavailable) return [ProviderUsage.Unavailable(ProviderIds.Claude, IndexUnavailable)];
            var state = _state; var readings = _readings;
            var rows = state.Accounts.Select(a => (readings.GetValueOrDefault(a.Id) ?? ProviderUsage.Unavailable(ProviderIds.Claude, "Pendiente de consultar"))
                with { Name = a.Name, Email = a.Email, Plan = readings.GetValueOrDefault(a.Id)?.Plan ?? a.Plan, Identity = a.Identity, ProfileId = a.Id, IsActive = state.Pending is null && state.ActiveId == a.Id }).ToList();
            if (_current is { } current && !state.Accounts.Any(a => a.Identity == current.Identity) && current.Status is "ok" or "stale") rows.Add(current);
            if (rows.Count == 0 && state.Pending is null) rows.Add(_current ?? ProviderUsage.Unavailable(ProviderIds.Claude, "Inicia sesión en Claude Desktop o Claude Code y actualiza."));
            return rows;
        }
    }
    private string CredentialPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Identificador de cuenta inválido.");
        return Path.Combine(_root, id + ".bin");
    }
    private void SaveState(ClaudeAccountState state)
    {
        RequireIndex();
        Directory.CreateDirectory(_root); AtomicFile.TryRestrictDirectory(_root);
        AtomicFile.WriteText(_index, JsonSerializer.Serialize(state)); _state = state;
    }
    private void SaveUsage(string id, ProviderUsage usage)
    {
        var account = _state.Accounts.Single(a => a.Id == id);
        if (usage.Identity != account.Identity)
        {
            // Error readings can be tied to a credential fingerprint. Their quota data is not attributable.
            usage = ProviderUsage.Unavailable(ProviderIds.Claude, usage.Error ?? "No se pudo verificar esta cuenta de Claude.")
                with { HttpStatus = usage.HttpStatus, RetryAfterSeconds = usage.RetryAfterSeconds };
        }
        if (_readings.TryGetValue(id, out var previous))
            usage = usage.Status != "ok"
                ? previous with { Error = usage.Error, HttpStatus = usage.HttpStatus, RetryAfterSeconds = usage.RetryAfterSeconds,
                    Status = "stale", Resets = previous.Resets?.Stale() }
                : usage with { Resets = ResetInventory.Merge(usage.Resets, previous.Resets) };
        var next = new Dictionary<string, ProviderUsage>(_readings) { [id] = usage with { Identity = account.Identity } };
        AtomicFile.WriteText(_cache, JsonSerializer.Serialize(next)); _readings = next;
    }
    private ClaudeAccount SaveAccount(string name, ClaudeLogin login, ClaudeCredentialBundle bundle)
    {
        var existing = _state.Accounts.FirstOrDefault(a => a.Identity == login.Identity);
        if (existing is null) RequireCapacity();
        var account = existing is null ? new ClaudeAccount(Guid.NewGuid().ToString("N"), _name(name), login.Account, login.Organization, login.Session.Email, login.Session.Plan)
            : existing with { Email = login.Session.Email ?? existing.Email, Plan = login.Session.Plan ?? existing.Plan };
        Directory.CreateDirectory(_root); AtomicFile.TryRestrictDirectory(_root);
        ClaudeSessionArchive.SaveProtected(CredentialPath(account.Id), bundle);
        var verified = ClaudeSessionArchive.LoadProtected<ClaudeCredentialBundle>(CredentialPath(account.Id));
        _archive.Validate(verified);
        SaveState(_state with { Accounts = _state.Accounts.Where(a => a.Id != account.Id).Append(account).ToArray(), ActiveId = account.Id });
        if (_current?.Identity == account.Identity) SaveUsage(account.Id, _current);
        return account;
    }
    private string PreviousName(string? reserved = null)
    {
        var candidate = "Claude anterior";
        for (var i = 2; _state.Accounts.Any(a => a.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) || _state.Pending?.Name == candidate || reserved == candidate; i++) candidate = "Claude anterior " + i;
        return candidate;
    }
    private void RequireIndex()
    {
        if (_indexUnavailable) throw new InvalidOperationException(IndexUnavailable);
    }
    private void RequireCapacity(int slots = 1)
    {
        if (_state.Accounts.Count > MaxAccounts - slots) throw new InvalidOperationException($"Claude admite hasta {MaxAccounts} cuentas guardadas. Libera espacio antes de continuar.");
    }
    private async Task<ClaudeAccount?> PreserveAsync(CancellationToken token, string? reserved = null)
    {
        var bundle = _archive.Capture();
        var login = await _archive.IdentifyAsync(bundle, token).ConfigureAwait(false);
        _archive.RequireClosed();
        if (login is null)
        {
            if (bundle.DesktopAuth is not null || bundle.CodeAuth is not null) throw new InvalidOperationException("No se puede verificar la sesión saliente de Claude. Inicia sesión de nuevo antes de cambiarla.");
            return null;
        }
        if (reserved is not null && !_state.Accounts.Any(a => a.Identity == login.Identity)) RequireCapacity(2);
        return SaveAccount(PreviousName(reserved), login, bundle);
    }
    private async Task MutateAsync(Func<Task> mutation, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_enabled) throw new InvalidOperationException("Activa Claude en Ajustes antes de gestionar sus cuentas.");
            RequireIndex();
            _archive.RequireClosed();
            using var global = new GlobalAuthSwitchLock(_archive.DesktopConfig);
            await global.AcquireAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            _archive.Recover();
            if (_organization.HasRecovery)
            {
                var current = await _archive.IdentifyAsync(null, token).ConfigureAwait(false);
                _organization.Recover(current?.Identity);
            }
            await mutation().ConfigureAwait(false);
        }
        finally { _gate.Release(); _changed(); }
    }
    public Task BeginAsync(string name, CancellationToken token = default) => MutateAsync(async () =>
    {
        name = _name(name);
        RequireCapacity();
        if (_state.Pending is not null) throw new InvalidOperationException("Completa o cancela el alta pendiente de Claude.");
        if (_state.Accounts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Ya existe una cuenta de Claude con ese nombre.");
        var previous = await PreserveAsync(token, name).ConfigureAwait(false);
        var before = _state;
        SaveState(_state with { Pending = new(name, previous?.Id, previous?.Identity) });
        try { _archive.Apply(ClaudeSessionArchive.Empty); _current = null; _next.Remove("current"); SaveState(_state with { ActiveId = null }); }
        catch { if (!_archive.HasRecovery) SaveState(before); throw; }
    }, token);

    public Task SwitchAsync(string id, CancellationToken token = default) => MutateAsync(async () =>
    {
        if (_state.Pending is not null) throw new InvalidOperationException("Completa o cancela el alta pendiente de Claude.");
        var account = _state.Accounts.SingleOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("La cuenta ya no existe.");
        var target = ClaudeSessionArchive.LoadProtected<ClaudeCredentialBundle>(CredentialPath(id)); _archive.Validate(target);
        var previous = await PreserveAsync(token).ConfigureAwait(false);
        // If it is already active, preservation may have saved a newer token generation.
        target = ClaudeSessionArchive.LoadProtected<ClaudeCredentialBundle>(CredentialPath(id));
        var beforeState = _state;
        var outgoing = _archive.Capture();
        var organization = previous is null || previous.Id == account.Id ? null : _organization.Prepare(previous.Identity, account.Identity,
            new(previous.Account, previous.Organization), new(account.Account, account.Organization));
        var installed = false;
        try
        {
            organization?.Apply(); _archive.Apply(target); installed = true; organization?.Verify();
            SaveState(_state with { ActiveId = account.Id }); organization?.Commit();
            _current = null; _next.Remove("current");
        }
        catch
        {
            if (installed) { _archive.Apply(outgoing); SaveState(beforeState); }
            organization?.Rollback(); throw;
        }
    }, token);

    public Task CancelAsync(CancellationToken token = default) => MutateAsync(async () =>
    {
        var pending = _state.Pending; if (pending is null) return;
        if (await CompleteAsync(token).ConfigureAwait(false)) return;
        var restore = pending.PreviousId is null ? ClaudeSessionArchive.Empty : ClaudeSessionArchive.LoadProtected<ClaudeCredentialBundle>(CredentialPath(pending.PreviousId));
        _archive.Apply(restore); SaveState(_state with { Pending = null, ActiveId = pending.PreviousId }); _current = null; _next.Remove("current"); ReleaseExitObservers();
    }, token);

    public async Task RenameAsync(string id, string name, CancellationToken token = default)
    {
        name = _name(name); await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireIndex();
            if (_state.Accounts.Any(a => a.Id != id && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Ese nombre ya existe.");
            if (!_state.Accounts.Any(a => a.Id == id)) throw new InvalidOperationException("La cuenta ya no existe.");
            SaveState(_state with { Accounts = _state.Accounts.Select(a => a.Id == id ? a with { Name = name } : a).ToArray() });
        }
        finally { _gate.Release(); _changed(); }
    }
    public Task DeleteAsync(string id, CancellationToken token = default) => MutateAsync(async () =>
    {
        if (_state.Pending is not null) throw new InvalidOperationException("Completa o cancela primero el alta pendiente.");
        var current = await _archive.IdentifyAsync(null, token).ConfigureAwait(false);
        var account = _state.Accounts.Single(a => a.Id == id);
        if (current?.Identity == account.Identity) throw new InvalidOperationException("No se puede eliminar la cuenta activa de Claude.");
        // The protected credential file remains as a recoverable local backup.
        SaveState(_state with { Accounts = _state.Accounts.Where(a => a.Id != id).ToArray() });
    }, token);

    public async Task SetEnabledAsync(bool enabled, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!enabled && _state.Pending is not null) throw new InvalidOperationException("Completa o cancela el alta de Claude antes de desactivarlo.");
            AtomicFile.WriteText(_config, JsonSerializer.Serialize(new Dictionary<string, bool> { [ProviderIds.Claude] = enabled }));
            _enabled = enabled;
            if (enabled && !_indexUnavailable) StartWatchers(); else { StopWatchers(); _current = null; }
        }
        finally { _gate.Release(); _changed(); }
        if (enabled) await RefreshAsync(false, token).ConfigureAwait(false);
    }

    internal async Task<bool> CompleteAsync(CancellationToken token)
    {
        if (_state.Pending is not { } pending || _archive.Running) return false;
        var bundle = _archive.Capture(); var login = await _archive.IdentifyAsync(bundle, token).ConfigureAwait(false);
        if (login is null || login.Identity == pending.PreviousIdentity) return false;
        _archive.RequireClosed();
        SaveAccount(pending.Name, login, bundle); SaveState(_state with { Pending = null }); _current = null; _next.Remove("current"); ReleaseExitObservers(); return true;
    }

    public async Task RefreshAsync(bool all, CancellationToken token = default)
    {
        if (!_enabled || _indexUnavailable || !await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            if (!_enabled) return;
            if ((_state.Pending is not null || _archive.HasRecovery) && !_archive.Running)
            {
                using var global = new GlobalAuthSwitchLock(_archive.DesktopConfig);
                await global.AcquireAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                _archive.Recover();
                if (_state.Pending is not null) await CompleteAsync(token).ConfigureAwait(false);
            }
            else if (_state.Pending is not null) ObserveExit();
            if (DateTimeOffset.UtcNow < _providerNext) return;
            var live = _archive.LiveSource;
            if (DateTimeOffset.UtcNow >= _next.GetValueOrDefault("current"))
            {
                var sessionKey = live.Read()?.Fingerprint;
                var reading = await QueryAsync(live, token).ConfigureAwait(false);
                var sameSession = sessionKey is not null && sessionKey == live.Read()?.Fingerprint;
                if (reading.Status == "error" && sameSession && _readings.GetValueOrDefault(CurrentCacheKey + sessionKey) is { } previous)
                    reading = previous with { Status = "stale", Error = reading.Error, HttpStatus = reading.HttpStatus,
                        RetryAfterSeconds = reading.RetryAfterSeconds, Resets = previous.Resets?.Stale() };
                if (reading.Identity is not null && reading.Identity == _current?.Identity)
                    reading = reading with { Resets = ResetInventory.Merge(reading.Resets, _current.Resets) };
                _current = reading;
                // Persist the unregistered live account too, without introducing another cache or credentials.
                var nextReadings = new Dictionary<string, ProviderUsage>(_readings);
                foreach (var key in nextReadings.Keys.Where(k => k.StartsWith(CurrentCacheKey, StringComparison.Ordinal)).ToArray()) nextReadings.Remove(key);
                if (reading.Status is "ok" or "stale" && sameSession) nextReadings[CurrentCacheKey + sessionKey] = reading;
                AtomicFile.WriteText(_cache, JsonSerializer.Serialize(nextReadings)); _readings = nextReadings;
                _next["current"] = DateTimeOffset.UtcNow.AddSeconds(_current.HttpStatus == 429 ? Math.Max(60, _current.RetryAfterSeconds ?? 60) : 15);
                if (_state.Accounts.FirstOrDefault(a => a.Identity == _current.Identity) is { } active)
                {
                    SaveUsage(active.Id, _current);
                    if (_state.ActiveId != active.Id) SaveState(_state with { ActiveId = active.Id });
                }
                else if (_current.Status is "ok" or "disconnected" && _state.ActiveId is not null) SaveState(_state with { ActiveId = null });
                if (_current.HttpStatus == 429)
                {
                    _providerNext = _next["current"];
                    return;
                }
            }
            if (all)
            {
                // Sequential requests keep total provider concurrency bounded by the existing batch.
                foreach (var account in _state.Accounts)
                {
                    token.ThrowIfCancellationRequested();
                    if (account.Identity == _current?.Identity && _current.Status == "ok" || DateTimeOffset.UtcNow < _next.GetValueOrDefault(account.Id)) continue;
                    ProviderUsage reading;
                    try
                    {
                        var bundle = ClaudeSessionArchive.LoadProtected<ClaudeCredentialBundle>(CredentialPath(account.Id));
                        reading = await QueryAsync(new FixedClientSession(_archive.Session(bundle, account.Account, account.Organization)), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { reading = ProviderUsage.Unavailable(ProviderIds.Claude, QuotaFailure.FromException(ex)); }
                    SaveUsage(account.Id, reading);
                    _next[account.Id] = DateTimeOffset.UtcNow.AddSeconds(reading.HttpStatus == 429 ? Math.Max(60, reading.RetryAfterSeconds ?? 60) : 15);
                    if (reading.HttpStatus == 429) { _providerNext = _next[account.Id]; break; }
                }
            }
        }
        finally { _gate.Release(); _changed(); }
    }
    private async Task<ProviderUsage> QueryAsync(IClientSessionSource source, CancellationToken token)
    {
        try { return await QuotaQuery.ReadAsync(new ClaudeQuotaProvider(source, _http), new CurrentQuotaRequest(), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ProviderUsage.Unavailable(ProviderIds.Claude, QuotaFailure.FromException(ex)); }
    }
    private void StartWatchers()
    {
        lock (_watchers)
        {
        if (_watchers.Count != 0) return;
        foreach (var path in new[] { _archive.DesktopConfig, _archive.CodeCredentials })
            if (Directory.Exists(Path.GetDirectoryName(path))) _watchers.Add(new AuthFileWatcher(path, ChangedAsync, _log));
        if (_state.Pending is not null) foreach (var watcher in _watchers) watcher.ScheduleInitialReconciliation();
        }
    }
    private async Task ChangedAsync(CancellationToken token)
    {
        if (!_enabled || _disposed || _state.Pending is null) return;
        if (_archive.Running) { ObserveExit(); return; }
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_enabled || _disposed) return;
            using var global = new GlobalAuthSwitchLock(_archive.DesktopConfig);
            await global.AcquireAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            _archive.Recover(); await CompleteAsync(token).ConfigureAwait(false);
        }
        finally { _gate.Release(); _changed(); }
    }
    private void ObserveExit()
    {
        lock (_waitingProcesses)
        {
            if (_disposed) return;
            void Watch(Process process)
            {
                var id = process.Id;
                try
                {
                    if (_waitingProcesses.ContainsKey(id)) { process.Dispose(); return; }
                    _waitingProcesses[id] = process;
                    process.Exited += (_, _) => ScheduleCapture();
                    process.EnableRaisingEvents = true;
                    if (process.HasExited) ScheduleCapture();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { _waitingProcesses.Remove(id); process.Dispose(); }
            }
            foreach (var process in Process.GetProcessesByName("claude")) Watch(process);
            foreach (var id in ClaudeProcessGuard.LegacyProcessIds())
                try { Watch(Process.GetProcessById(id)); } catch (ArgumentException) { }
        }
    }
    private void ScheduleCapture()
    {
        lock (_watchers) if (!_disposed) foreach (var watcher in _watchers) watcher.ScheduleInitialReconciliation();
    }
    private void ReleaseExitObservers()
    {
        lock (_waitingProcesses) { foreach (var process in _waitingProcesses.Values) process.Dispose(); _waitingProcesses.Clear(); }
    }
    private void StopWatchers()
    {
        lock (_watchers) { foreach (var watcher in _watchers) watcher.Dispose(); _watchers.Clear(); }
        ReleaseExitObservers();
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); StopWatchers(); _lifetime.Dispose(); }
}
