using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace CodexManager;

internal interface IUsageClient
{
    Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string codexConfigPath, CancellationToken cancellationToken);
}

internal sealed class UsageClient : IUsageClient
{
    public async Task<UsageRecord> ReadAsync(
        AuthSnapshot snapshot,
        string codexConfigPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshot.AccessToken))
        {
            throw new InvalidOperationException("El perfil no contiene access_token para consultar el consumo.");
        }

        var url = $"{ResolveChatGptBaseUrl(codexConfigPath)}/wham/usage";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.AccessToken);
        request.Headers.UserAgent.ParseAdd("CodexManager");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(snapshot.AccountId))
        {
            request.Headers.Add("ChatGPT-Account-Id", snapshot.AccountId);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return UsageRecord.FromError(
                $"El access_token no fue aceptado ({(int)response.StatusCode}). Esto no confirma que el refresh_token este revocado.");
        }
        if (!response.IsSuccessStatusCode)
        {
            return UsageRecord.FromError($"La consulta de limites devolvio {(int)response.StatusCode}.");
        }

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return NormalizeWhamUsage(json);
    }

    internal static UsageRecord NormalizeWhamUsage(JsonNode? root)
    {
        var rateLimit = root?["rate_limit"] as JsonObject;
        var windows = new List<UsageWindow>();
        AddWhamWindow(rateLimit?["primary_window"], windows);
        AddWhamWindow(rateLimit?["secondary_window"], windows);

        var classified = UsageWindowClassifier.Classify(windows);

        return new UsageRecord
        {
            CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
            Status = "ok",
            Email = root?["email"]?.GetValue<string?>(),
            Session = classified.Session,
            Weekly = classified.Weekly,
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
