using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AICountsManager;

// These sessions belong to their clients. Never rotate tokens, start a client or write its store.
internal sealed record ClientSession(string AccessToken, string? Email = null, string? Plan = null, long? ExpiresAt = null)
{
    public string Fingerprint => AuthHash.Bytes(Encoding.UTF8.GetBytes(AccessToken));
    public override string ToString() => "ClientSession [redacted]";
}
internal interface IClientSessionSource { ClientSession? Read(); }
internal sealed class ClaudeSessionSource(string home) : IClientSessionSource
{
    public static string Home => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } path
        ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public ClientSession? Read()
    {
        try
        {
            var path = Path.Combine(home, ".credentials.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(file);
            var oauth = ProviderJson.Get(doc.RootElement, "claudeAiOauth");
            var token = ProviderJson.Text(oauth, "accessToken");
            if (!ProviderJson.ValidToken(token)) return null;
            var plan = ProviderJson.Text(oauth, "subscriptionType");
            var tier = ProviderJson.Text(oauth, "rateLimitTier");
            if (tier is not null && System.Text.RegularExpressions.Regex.Match(tier, @"\d+x") is { Success: true } match) plan += " " + match.Value;
            return new(token!, Plan: plan, ExpiresAt: ProviderJson.Timestamp(ProviderJson.Get(oauth, "expiresAt")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
}
internal sealed class CursorSessionSource(string database) : IClientSessionSource
{
    public static string Database => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
    public ClientSession? Read()
    {
        if (!File.Exists(database)) return null;
        try
        {
            using var db = new ReadOnlySqlite(database);
            var values = new Dictionary<string, string>();
            db.Query("SELECT CAST(key AS BLOB), CAST(value AS BLOB) FROM ItemTable WHERE key IN ('cursorAuth/accessToken','cursorAuth/cachedEmail','cursorAuth/stripeMembershipType') AND length(value)<=32768", row =>
            {
                var k = ReadOnlySqlite.Blob(row, 0); var v = ReadOnlySqlite.Blob(row, 1);
                if (k is not null && v is not null) values[Encoding.UTF8.GetString(k)] = Encoding.UTF8.GetString(v).Trim('"');
            });
            var token = values.GetValueOrDefault("cursorAuth/accessToken");
            return ProviderJson.ValidToken(token) ? new(token!, values.GetValueOrDefault("cursorAuth/cachedEmail"), values.GetValueOrDefault("cursorAuth/stripeMembershipType")) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
internal static class ProviderJson
{
    public static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string? Text(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static double? Number(JsonElement e, string key) => Number(Get(e, key));
    public static double? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static bool ValidToken(string? token) => token is { Length: > 0 and <= 32768 } && !token.Any(char.IsWhiteSpace) && !token.Any(char.IsControl);
    public static long? Timestamp(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), out var date)) return date.ToUnixTimeSeconds();
        var value = Number(e);
        if (value is null && e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed)) value = parsed;
        if (value is not >= 0 || !double.IsFinite(value.Value)) return null;
        var seconds = value > 100000000000 ? value / 1000 : value;
        return seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? (long)seconds : null;
    }
    public static string? CursorUser(string token)
    {
        try
        {
            var parts = token.Split('.'); if (parts.Length != 3 || parts[1].Length > 16000) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/'); payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return Text(doc.RootElement, "sub")?.Split('|').LastOrDefault();
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
internal sealed record ProviderReply(int Status, JsonElement Body, int? RetryAfter);
internal static class ProviderHttp
{
    public static HttpClient Create() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public static async Task<ProviderReply> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken token)
    {
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            int? retry = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Clamp(delta.TotalSeconds, 0, 86400)
                : response.Headers.RetryAfter?.Date is { } date ? (int)Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, 86400) : null;
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, default, retry);
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Respuesta demasiado grande.");
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[8192];
            for (int n; (n = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
            { if (bytes.Length + n > 1024 * 1024) throw new InvalidDataException("Respuesta demasiado grande."); bytes.Write(buffer, 0, n); }
            using var doc = JsonDocument.Parse(bytes.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            return new((int)response.StatusCode, doc.RootElement.Clone(), retry);
        }
    }
    public static HttpRequestMessage Bearer(string uri, string access, bool post = false)
    {
        var r = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, uri);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access); r.Headers.Accept.ParseAdd("application/json");
        if (post) { r.Content = new StringContent("{}", Encoding.UTF8, "application/json"); r.Headers.Add("Connect-Protocol-Version", "1"); }
        return r;
    }
    public static HttpRequestMessage CursorWeb(string uri, ClientSession session, string? body = null)
    {
        var user = ProviderJson.CursorUser(session.AccessToken) ?? throw new InvalidDataException("Sesión no compatible.");
        var r = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        r.Headers.Add("Cookie", "WorkosCursorSessionToken=" + Uri.EscapeDataString(user + "::" + session.AccessToken));
        r.Headers.Add("Origin", "https://cursor.com");
        if (body is not null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }
    public static ProviderUsage Failure(string provider, ClientSession? session, int? status = null, int? retry = null) =>
        ProviderUsage.Unavailable(provider, status == 429 ? "Consulta limitada por el proveedor. Espera antes de actualizar."
            : session is null || status is 401 or 403 ? "Inicia sesión en " + (provider == ProviderIds.Claude ? "Claude Code" : "Cursor") + " y vuelve a actualizar."
            : "No se pudo consultar el uso. Vuelve a actualizar.") with { Identity = session?.Fingerprint, HttpStatus = status, RetryAfterSeconds = retry,
                Status = session is null ? "disconnected" : "error" };
}
internal sealed class ClaudeQuotaProvider(IClientSessionSource sessions, HttpClient http) : IQuotaProvider
{
    public string Id => ProviderIds.Claude;
    public bool CanSwitchAccounts => false;
    public bool DefaultEnabled => false;
    public async Task<ProviderUsage> ReadAsync(QuotaRequest request, CancellationToken token)
    {
        var session = sessions.Read(); if (session is null) return ProviderHttp.Failure(Id, null);
        if (session.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return ProviderHttp.Failure(Id, session, 401);
        var query = ProviderHttp.Bearer("https://api.anthropic.com/api/oauth/usage", session.AccessToken);
        query.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        var response = await ProviderHttp.SendAsync(http, query, token).ConfigureAwait(false);
        if (response.Status != 200) return ProviderHttp.Failure(Id, session, response.Status, response.RetryAfter);
        var quotas = Parse(response.Body);
        if (quotas.Count == 0) return ProviderHttp.Failure(Id, session);
        string? email = session.Email, plan = session.Plan; string name = "Claude";
        try
        {
            using var optional = CancellationTokenSource.CreateLinkedTokenSource(token); optional.CancelAfter(TimeSpan.FromSeconds(2));
            var queryProfile = ProviderHttp.Bearer("https://api.anthropic.com/api/oauth/profile", session.AccessToken);
            queryProfile.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            var profile = await ProviderHttp.SendAsync(http, queryProfile, optional.Token).ConfigureAwait(false);
            if (profile.Status == 200)
            {
                var account = ProviderJson.Get(profile.Body, "account"); var org = ProviderJson.Get(profile.Body, "organization");
                email = ProviderJson.Text(account, "email"); name = ProviderJson.Text(account, "display_name") ?? "Claude";
                plan = ProviderJson.Text(org, "organization_type")?.Replace("claude_", "") ?? plan;
                if (ProviderJson.Text(org, "rate_limit_tier") is { } tier && System.Text.RegularExpressions.Regex.Match(tier, @"\d+x") is { Success: true } m) plan += " " + m.Value;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        if (sessions.Read()?.Fingerprint != session.Fingerprint) return ProviderHttp.Failure(Id, null);
        return new(Id, session.Fingerprint, name, email, plan, DateTimeOffset.UtcNow.ToString("O"), "ok", null, quotas);
    }
    internal static IReadOnlyList<ProviderQuota> Parse(JsonElement root)
    {
        var result = new List<ProviderQuota>();
        foreach (var field in root.EnumerateObject())
        {
            var minutes = field.Name == "five_hour" ? 300 : field.Name.StartsWith("seven_day", StringComparison.Ordinal) ? 10080 : 0;
            if (minutes == 0 || ProviderJson.Number(field.Value, "utilization") is not >= 0) continue;
            var group = field.Name.StartsWith("seven_day_", StringComparison.Ordinal) ? field.Name[10..].Replace('_', ' ') : "Claude";
            result.Add(new(field.Name, group, minutes == 300 ? "Sesión · 5 h" : "Semanal", ProviderJson.Number(field.Value, "utilization"), minutes, ProviderJson.Timestamp(ProviderJson.Get(field.Value, "resets_at"))));
        }
        if (ProviderJson.Get(root, "limits") is { ValueKind: JsonValueKind.Array } limits)
            foreach (var limit in limits.EnumerateArray().Take(20))
            {
                var model = ProviderJson.Text(ProviderJson.Get(ProviderJson.Get(limit, "scope"), "model"), "display_name");
                if (ProviderJson.Text(limit, "kind") != "weekly_scoped" || model is not { Length: > 0 and <= 80 } || ProviderJson.Number(limit, "percent") is not >= 0) continue;
                result.RemoveAll(q => q.Group.Equals(model, StringComparison.OrdinalIgnoreCase));
                result.Add(new("weekly-" + model, model, "Semanal", ProviderJson.Number(limit, "percent"), 10080, ProviderJson.Timestamp(ProviderJson.Get(limit, "resets_at"))));
            }
        var extra = ProviderJson.Get(root, "extra_usage");
        if (ProviderJson.Get(extra, "is_enabled").ValueKind == JsonValueKind.True && ProviderJson.Number(extra, "monthly_limit") is > 0 and var cap && ProviderJson.Number(extra, "used_credits") is >= 0 and var used)
            result.Add(new("extra", "Claude", "Uso extra", used / cap * 100, null, null));
        return result.Take(24).ToArray();
    }
}
internal sealed class CursorQuotaProvider(IClientSessionSource sessions, HttpClient http) : IQuotaProvider
{
    public string Id => ProviderIds.Cursor;
    public bool CanSwitchAccounts => false;
    public bool DefaultEnabled => false;
    public async Task<ProviderUsage> ReadAsync(QuotaRequest request, CancellationToken token)
    {
        var session = sessions.Read(); if (session is null) return ProviderHttp.Failure(Id, null);
        var response = await ProviderHttp.SendAsync(http, ProviderHttp.Bearer("https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage", session.AccessToken, true), token).ConfigureAwait(false);
        if (response.Status is 401 or 403 or 429) return ProviderHttp.Failure(Id, session, response.Status, response.RetryAfter);
        var quotas = response.Status == 200 ? Parse(response.Body) : [];
        if (quotas.Count == 0 && ProviderJson.CursorUser(session.AccessToken) is not null)
        {
            response = await ProviderHttp.SendAsync(http, ProviderHttp.CursorWeb("https://cursor.com/api/usage-summary", session), token).ConfigureAwait(false);
            if (response.Status != 200) return ProviderHttp.Failure(Id, session, response.Status, response.RetryAfter);
            quotas = Parse(response.Body, summary: true);
        }
        if (quotas.Count == 0) return ProviderHttp.Failure(Id, session);
        if (sessions.Read()?.Fingerprint != session.Fingerprint) return ProviderHttp.Failure(Id, null);
        return new(Id, session.Fingerprint, "Cursor", session.Email, ProviderJson.Text(response.Body, "membershipType") ?? session.Plan, DateTimeOffset.UtcNow.ToString("O"), "ok", null, quotas);
    }
    internal static IReadOnlyList<ProviderQuota> Parse(JsonElement root, bool summary = false)
    {
        if (ProviderJson.Get(root, "enabled").ValueKind == JsonValueKind.False) return [];
        var start = ProviderJson.Timestamp(ProviderJson.Get(root, "billingCycleStart")); var end = ProviderJson.Timestamp(ProviderJson.Get(root, "billingCycleEnd"));
        int? minutes = start.HasValue && end > start && end - start <= 366L * 86400 ? (int)((end.Value - start.Value) / 60) : null;
        var plan = summary ? ProviderJson.Get(ProviderJson.Get(root, "individualUsage"), "plan") : ProviderJson.Get(root, "planUsage");
        var result = new List<ProviderQuota>();
        void Add(string id, string label, double? percent) { if (percent is >= 0) result.Add(new(id, "Cursor", label, percent, minutes, end)); }
        var auto = ProviderJson.Number(plan, "autoPercentUsed"); var api = ProviderJson.Number(plan, "apiPercentUsed");
        var spent = ProviderJson.Number(plan, "totalSpend"); var cap = ProviderJson.Number(plan, "limit");
        if (spent is null && cap is > 0 && ProviderJson.Number(plan, "remaining") is double remaining) spent = cap - remaining;
        var pools = auto is >= 0 && api is >= 0 && (auto > 0 || api > 0 || spent == 0);
        var isTeam = string.Equals(ProviderJson.Text(ProviderJson.Get(root, "spendLimitUsage"), "limitType"), "team", StringComparison.OrdinalIgnoreCase) || ProviderJson.Number(ProviderJson.Get(root, "spendLimitUsage"), "pooledLimit") is > 0 || ProviderJson.Text(root, "membershipType") == "team";
        var total = ProviderJson.Number(plan, "totalPercentUsed");
        if (total is null && !(isTeam && pools) && cap is > 0 && spent is >= 0) total = spent / cap * 100;
        Add("cycle", "Ciclo de facturación", total);
        if (pools || summary) { Add("auto", "Modelos Cursor", auto); Add("api", "Otros modelos", api); }
        var extra = summary ? ProviderJson.Get(ProviderJson.Get(root, "individualUsage"), "onDemand") : ProviderJson.Get(root, "spendLimitUsage");
        var extraCap = ProviderJson.Number(extra, summary ? "limit" : "individualLimit");
        var extraUsed = ProviderJson.Number(extra, summary ? "used" : "individualUsed");
        if (extraUsed is null && extraCap is > 0 && ProviderJson.Number(extra, "individualRemaining") is double left) extraUsed = extraCap - left;
        if (extraCap is > 0 && extraUsed is >= 0) Add("extra", "Uso extra", extraUsed / extraCap * 100);
        return result;
    }
}
