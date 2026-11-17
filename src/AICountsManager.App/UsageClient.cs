using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace AICountsManager;

internal interface IUsageClient
{
    Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string codexConfigPath, CancellationToken cancellationToken);
}

internal sealed class UsageClient(HttpClient? http = null) : IUsageClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _http = http ?? Http;
    public async Task<UsageRecord> ReadAsync(
        AuthSnapshot snapshot,
        string codexConfigPath,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(snapshot.AccessToken))
        {
            throw new InvalidOperationException("El perfil no contiene access_token para consultar el consumo.");
        }

        var baseUrl = ResolveChatGptBaseUrl(codexConfigPath);
        if (baseUrl is null)
            return UsageRecord.FromError("Destino de Codex no autorizado. Revisa chatgpt_base_url.");

        var url = new Uri(baseUrl, "wham/usage");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.AccessToken);
        request.Headers.UserAgent.ParseAdd("AICountsManager");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(snapshot.AccountId))
        {
            request.Headers.Add("ChatGPT-Account-Id", snapshot.AccountId);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return UsageRecord.FromError(QuotaFailure.Authentication) with { HttpStatus = (int)response.StatusCode };
        }
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter;
            var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 60;
            return UsageRecord.FromError(QuotaFailure.FromStatus((int)response.StatusCode)) with
            { HttpStatus = (int)response.StatusCode, RetryAfterSeconds = (int)Math.Clamp(seconds, 1, 86400) };
        }

        JsonNode? json;
        try { json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
        catch (System.Text.Json.JsonException) { return UsageRecord.FromError(QuotaFailure.InvalidResponse); }
        var usage = NormalizeWhamUsage(json);
        if (usage.Status != "ok") return usage;
        // An optional inventory failure must never discard a successful quota reading.
        var budget = TimeSpan.FromSeconds(10) - started.Elapsed;
        if (usage.Resets is { Status: "ok", DetailsComplete: true } || budget <= TimeSpan.FromMilliseconds(100)) return usage;
        try
        {
            using var optional = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            optional.CancelAfter(budget < TimeSpan.FromSeconds(3) ? budget : TimeSpan.FromSeconds(3));
            var details = ProviderHttp.Bearer(new Uri(baseUrl, "wham/rate-limit-reset-credits").AbsoluteUri, snapshot.AccessToken);
            details.Headers.UserAgent.ParseAdd("AICountsManager");
            details.Headers.CacheControl = new() { NoStore = true };
            if (!string.IsNullOrWhiteSpace(snapshot.AccountId)) details.Headers.Add("ChatGPT-Account-Id", snapshot.AccountId);
            var inventory = await ProviderHttp.SendAsync(_http, details, optional.Token).ConfigureAwait(false);
            var reading = inventory.Status == 200
                ? ResetInventoryParser.Codex(inventory.Body, usage.SessionLimitNotApplicable, DateTimeOffset.UtcNow)
                : ResetInventory.Unknown(DateTimeOffset.UtcNow, inventory.Status, inventory.RetryAfter);
            usage = usage with { Resets = reading.AvailableCount is null && usage.Resets?.AvailableCount is not null
                ? usage.Resets with { HttpStatus = reading.HttpStatus, RetryAfterSeconds = reading.RetryAfterSeconds } : reading };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        { cancellationToken.ThrowIfCancellationRequested(); }
        return usage;
    }

    internal static UsageRecord NormalizeWhamUsage(JsonNode? root)
    {
        if (root is not JsonObject payload) return UsageRecord.FromError(QuotaFailure.InvalidResponse);
        var rateLimit = payload["rate_limit"] as JsonObject;
        var windows = new List<UsageWindow>();
        AddWhamWindow(rateLimit?["primary_window"], windows);
        AddWhamWindow(rateLimit?["secondary_window"], windows);

        var classified = UsageWindowClassifier.Classify(windows);
        if (classified.Session.UsedPercent is null && classified.Weekly.UsedPercent is null)
            return UsageRecord.FromError(QuotaFailure.InvalidResponse);

        var plan = AccountPlan.Read(payload["plan_type"]);

        return new UsageRecord
        {
            CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
            Status = "ok",
            Email = payload["email"] is JsonValue email && email.TryGetValue<string>(out var address) ? address : null,
            PlanType = plan,
            SessionLimitNotApplicable = AccountPlan.IsWeeklyOnlyPlan(plan, windows),
            Session = classified.Session,
            Weekly = classified.Weekly,
            Resets = ResetInventoryParser.Codex(System.Text.Json.JsonSerializer.SerializeToElement(root),
                AccountPlan.IsWeeklyOnlyPlan(plan, windows), DateTimeOffset.UtcNow, summary: true),
        };
    }

    private static void AddWhamWindow(JsonNode? node, ICollection<UsageWindow> destination)
    {
        if (node is not JsonObject window) return;
        var used = window["used_percent"] is JsonValue percent && percent.TryGetValue<double>(out var value)
            && double.IsFinite(value) && value is >= 0 and <= 100 ? value : (double?)null;
        var duration = window["limit_window_seconds"] is JsonValue length && length.TryGetValue<int>(out var seconds)
            ? seconds % 60 == 0 ? seconds / 60 : -1 : (int?)null;
        var reset = window["reset_at"] is JsonValue time && time.TryGetValue<long>(out var timestamp)
            && timestamp > 0 && timestamp <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? timestamp : (long?)null;
        destination.Add(new UsageWindow(used, duration, reset));
    }

    private static Uri? ResolveChatGptBaseUrl(string codexConfigPath)
    {
        var defaultUrl = new Uri("https://chatgpt.com/backend-api/");
        Uri? configured = null;
        try
        {
            foreach (var rawLine in File.ReadLines(codexConfigPath))
            {
                var line = rawLine.Split('#')[0].Trim();
                if (line.StartsWith('[')) break; // Only root TOML settings apply.
                var parts = line.Split('=', 2);
                if (parts[0].Trim().Trim('"', '\'') != "chatgpt_base_url") continue;
                if (parts.Length != 2 || configured is not null) return null;
                var value = parts[1].Trim();
                if (value.Length < 2 || value[0] is not ('"' or '\'') || value[^1] != value[0]) return null;
                if (!Uri.TryCreate(value[1..^1], UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
                    || uri.IdnHost is not ("chatgpt.com" or "chat.openai.com")
                    || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                    || uri.AbsolutePath is not ("/" or "/backend-api" or "/backend-api/")) return null;
                configured = new Uri($"https://{uri.IdnHost}/backend-api/");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return defaultUrl; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException)
        {
            return null;
        }
        return configured ?? defaultUrl;
    }
}
