using System.Text.Json;

namespace CodexManager;

internal static class ProviderIds
{
    public const string Codex = "codex", Antigravity = "antigravity";
    public static string Name(string id) => id == Codex ? "Codex" : id == Antigravity ? "Antigravity" : id;
}
internal abstract record QuotaRequest;
internal sealed record CodexQuotaRequest(AuthSnapshot Auth, string ConfigPath) : QuotaRequest;
internal sealed record CurrentQuotaRequest : QuotaRequest;
internal sealed record ProviderQuota(string Id, string Group, string Label, double? UsedPercent, int? Minutes, long? ResetsAt)
{
    public UsageWindow Window => new(UsedPercent, Minutes, ResetsAt);
    public double? CurrentUsed => UsedPercent is double v && double.IsFinite(v) && (ResetsAt is null || ResetsAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ? Math.Clamp(v, 0, 100) : null;
}
internal sealed record ProviderUsage(string Provider, string? Identity, string Name, string? Email, string? Plan,
    string? CheckedAt, string Status, string? Error, IReadOnlyList<ProviderQuota> Quotas,
    int? HttpStatus = null, int? RetryAfterSeconds = null, bool SessionLimitNotApplicable = false)
{
    public static ProviderUsage Unavailable(string provider, string error) => new(provider, null, ProviderIds.Name(provider), null, null, null, "error", error, []);
    public bool Fresh => Status == "ok" && DateTimeOffset.TryParse(CheckedAt, out var t) && DateTimeOffset.UtcNow - t < TimeSpan.FromMinutes(15);
    // Independent account capacities cannot be added. This scalar is explicitly the most-used weekly limit.
    public double? WeeklyUsed => Fresh ? Quotas.Where(q => q.Minutes == 10080).Select(q => q.CurrentUsed).Where(v => v.HasValue).DefaultIfEmpty(null).Max() : null;
}
internal interface IQuotaProvider
{
    string Id { get; }
    bool CanSwitchAccounts { get; }
    Task<ProviderUsage> ReadAsync(QuotaRequest request, CancellationToken token);
}
internal static class QuotaQuery
{
    // Shared deadline and cancellation. Credentials and parsing remain inside each provider.
    public static async Task<ProviderUsage> ReadAsync(IQuotaProvider provider, QuotaRequest request, CancellationToken token)
    {
        if (provider.Id == ProviderIds.Codex ? request is not CodexQuotaRequest : request is not CurrentQuotaRequest || provider.CanSwitchAccounts)
            throw new InvalidOperationException("Solo Codex admite consultas de perfiles guardados y cambio de cuenta.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        return await provider.ReadAsync(request, deadline.Token).ConfigureAwait(false);
    }
}
internal sealed class CodexQuotaProvider(IUsageClient client) : IQuotaProvider
{
    public string Id => ProviderIds.Codex;
    public bool CanSwitchAccounts => true;
    public async Task<ProviderUsage> ReadAsync(QuotaRequest request, CancellationToken token)
    {
        if (request is not CodexQuotaRequest codex) throw new ArgumentException("Solicitud de proveedor incorrecta.");
        var u = await client.ReadAsync(codex.Auth, codex.ConfigPath, token).ConfigureAwait(false);
        return new(Id, codex.Auth.Identity.Fingerprint, "Codex", u.Email, u.PlanType, u.CheckedAt, u.Status, u.Error,
            [new("session", "Codex", "Sesión · 5 h", u.Session.UsedPercent, u.Session.WindowDurationMins, u.Session.ResetsAt),
             new("weekly", "Codex", "Semanal", u.Weekly.UsedPercent, u.Weekly.WindowDurationMins, u.Weekly.ResetsAt)],
            u.HttpStatus, u.RetryAfterSeconds, u.SessionLimitNotApplicable);
    }
    public static UsageRecord Legacy(ProviderUsage reading) => new()
    {
        CheckedAt = reading.CheckedAt, Status = reading.Status, Error = reading.Error, Email = reading.Email,
        PlanType = reading.Plan, HttpStatus = reading.HttpStatus, RetryAfterSeconds = reading.RetryAfterSeconds,
        SessionLimitNotApplicable = reading.SessionLimitNotApplicable,
        Session = reading.Quotas.FirstOrDefault(q => q.Id == "session")?.Window ?? new(null, null, null),
        Weekly = reading.Quotas.FirstOrDefault(q => q.Id == "weekly")?.Window ?? new(null, null, null)
    };
    public static ProviderUsage FromProfile(ProfileState p) => new(ProviderIds.Codex, p.Name, p.Name, p.Email,
        AccountPlan.Label(p.PlanType), p.Status == "ok" ? DateTimeOffset.UtcNow.ToString("O") : null, p.Status, p.Error,
        [new("weekly", "Codex", "Semanal", p.WeeklyUsedPercent, 10080, p.WeeklyWindow?.ResetsAt)]);
}

// Only numeric snapshots and account display metadata are persisted, never authentication material.
internal sealed class CurrentProviderStore
{
    private readonly IQuotaProvider? _provider;
    private readonly string _cache, _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProviderUsage? _snapshot;
    private DateTimeOffset _next;
    private volatile bool _enabled;
    private int _generation;
    public string Id => _provider?.Id ?? ProviderIds.Antigravity;
    public bool Available => _provider is not null;
    public bool Enabled => _enabled;
    public ProviderUsage? Snapshot => _enabled ? Volatile.Read(ref _snapshot) : null;
    public CurrentProviderStore(AppPaths paths, IQuotaProvider? provider)
    {
        _provider = provider; _enabled = provider is not null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(Id, "^[a-z0-9-]{1,40}$")) throw new ArgumentException("Identificador de proveedor no válido.");
        _cache = Path.Combine(paths.AppHome, "cache", "provider-" + Id + ".json");
        _config = Path.Combine(paths.AppHome, "config", "provider-" + Id + ".json");
        try
        {
            if (File.Exists(_config)) _enabled &= JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_config))?.GetValueOrDefault(Id, true) ?? true;
            if (provider is not null && File.Exists(_cache) && new FileInfo(_cache).Length <= 128 * 1024)
            {
                var saved = JsonSerializer.Deserialize<ProviderUsage>(File.ReadAllText(_cache));
                if (saved is { Quotas.Count: <= 24 } && saved.Provider == Id && saved.Quotas.All(q => q is not null))
                    _snapshot = saved with { Status = "stale", Error = "Última lectura · comprobando sesión actual" };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public void SetEnabled(bool enabled)
    {
        AtomicFile.WriteText(_config, JsonSerializer.Serialize(new Dictionary<string, bool> { [Id] = enabled }));
        Interlocked.Increment(ref _generation);
        _enabled = enabled && Available;
        if (!enabled) { _snapshot = null; if (File.Exists(_cache)) File.Delete(_cache); }
        _next = DateTimeOffset.MinValue;
    }
    public async Task RefreshAsync(CancellationToken token)
    {
        if (!_enabled || _provider is null || !await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            if (DateTimeOffset.UtcNow < _next) return;
            var generation = Volatile.Read(ref _generation);
            ProviderUsage result;
            try { result = await QuotaQuery.ReadAsync(_provider, new CurrentQuotaRequest(), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { result = ProviderUsage.Unavailable(_provider.Id, "No se pudo consultar " + ProviderIds.Name(Id) + ". Vuelve a actualizar."); }
            token.ThrowIfCancellationRequested();
            if (!_enabled || generation != Volatile.Read(ref _generation)) return;
            // Keep an unavailable previous observation explicitly stale. Never merge limits from two identities.
            if (result.Status == "error" && result.Identity is null && _snapshot is { } previous)
                result = previous with { Status = "error", Error = "Última cuenta consultada · " + result.Error, HttpStatus = result.HttpStatus, RetryAfterSeconds = result.RetryAfterSeconds };
            _snapshot = result;
            _next = DateTimeOffset.UtcNow.AddSeconds(result.HttpStatus == 429 ? Math.Clamp(result.RetryAfterSeconds ?? 60, 60, 86400) : 15);
            AtomicFile.WriteText(_cache, JsonSerializer.Serialize(result));
        }
        finally { _gate.Release(); }
    }
}


internal sealed record CurrentProviderSetting(string Id, string Name, bool Enabled);
internal sealed class CurrentProviderRegistry
{
    private readonly Dictionary<string, CurrentProviderStore> _stores;
    private readonly AppDiagnostics _diagnostics;
    public CurrentProviderRegistry(AppPaths paths, IEnumerable<IQuotaProvider> providers)
    {
        _diagnostics = new AppDiagnostics(paths.LogsDir);
        var adapters = providers.ToArray();
        if (adapters.Any(p => p.Id == ProviderIds.Codex || p.CanSwitchAccounts))
            throw new ArgumentException("Los proveedores externos solo pueden consultar la sesión actual.");
        _stores = adapters.ToDictionary(p => p.Id, p => new CurrentProviderStore(paths, p));
    }
    public IReadOnlyList<CurrentProviderSetting> Settings => _stores.Values.Select(s => new CurrentProviderSetting(s.Id, ProviderIds.Name(s.Id), s.Enabled)).ToArray();
    public bool IsEnabled(string id) => _stores.TryGetValue(id, out var store) && store.Enabled;
    public IReadOnlyList<ProviderUsage> Accounts => _stores.Values.Where(s => s.Enabled)
        .Select(s => s.Snapshot ?? ProviderUsage.Unavailable(s.Id, "Pendiente de consultar la sesión actual")).ToArray();
    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken token)
    {
        if (!_stores.TryGetValue(id, out var store)) return;
        store.SetEnabled(enabled);
        if (enabled) await RefreshOneAsync(store, token).ConfigureAwait(false);
    }
    public async Task RefreshAsync(CancellationToken token)
    {
        foreach (var batch in _stores.Values.Where(s => s.Enabled).Chunk(2))
            await Task.WhenAll(batch.Select(s => RefreshOneAsync(s, token))).ConfigureAwait(false);
    }
    private async Task RefreshOneAsync(CurrentProviderStore store, CancellationToken token)
    {
        try { await store.RefreshAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { _diagnostics.Write("provider.refresh." + store.Id, ex); }
    }
}
