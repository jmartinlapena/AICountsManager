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

        var url = $"{ResolveChatGptBaseUrl(codexConfigPath)}/wham/usage";
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

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var usage = NormalizeWhamUsage(json);
        // An optional inventory failure must never discard a successful quota reading.
        var budget = TimeSpan.FromSeconds(10) - started.Elapsed;
        if (usage.Resets is { Status: "ok", DetailsComplete: true } || budget <= TimeSpan.FromMilliseconds(100)) return usage;
        try
        {
            using var optional = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            optional.CancelAfter(budget < TimeSpan.FromSeconds(3) ? budget : TimeSpan.FromSeconds(3));
            var details = ProviderHttp.Bearer($"{ResolveChatGptBaseUrl(codexConfigPath)}/wham/rate-limit-reset-credits", snapshot.AccessToken);
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
        var rateLimit = root?["rate_limit"] as JsonObject;
        var windows = new List<UsageWindow>();
        AddWhamWindow(rateLimit?["primary_window"], windows);
        AddWhamWindow(rateLimit?["secondary_window"], windows);

        var classified = UsageWindowClassifier.Classify(windows);
        var plan = AccountPlan.Read(root?["plan_type"]);

        return new UsageRecord
        {
            CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
            Status = "ok",
            Email = root?["email"]?.GetValue<string?>(),
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
        if (node is null)
        {
            return;
        }
        destination.Add(new UsageWindow(
            node["used_percent"]?.GetValue<double?>(),
            node["limit_window_seconds"]?.GetValue<int?>() is int seconds ? seconds / 60 : null,
            node["reset_at"]?.GetValue<long?>()));
    }

    private static string ResolveChatGptBaseUrl(string codexConfigPath)
    {
        var baseUrl = "https://chatgpt.com/backend-api";
        if (!File.Exists(codexConfigPath))
        {
            return baseUrl;
        }

        try
        {
            foreach (var rawLine in File.ReadAllLines(codexConfigPath))
            {
                var line = rawLine.Split('#')[0].Trim();
                var parts = line.Split('=', 2);
                if (parts.Length != 2 || parts[0].Trim() != "chatgpt_base_url")
                {
                    continue;
                }

                var value = parts[1].Trim().Trim('"', '\'').TrimEnd('/');
                if ((value.StartsWith("https://chatgpt.com", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("https://chat.openai.com", StringComparison.OrdinalIgnoreCase))
                    && !value.Contains("/backend-api", StringComparison.OrdinalIgnoreCase))
                {
                    value += "/backend-api";
                }

                if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        catch
        {
        }
        return baseUrl;
    }
}
