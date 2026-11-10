using AICountsManager;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
namespace AICountsManager.Tests;
internal static partial class Program
{
    private sealed class CloudCredentials : IAntigravityCredentials
    {
        public AntigravityCredential? Value = new("one", "access-one", "refresh-one", DateTimeOffset.UtcNow.AddHours(1), "a@example.test", "A");
        public AntigravityCredential? Read() => Value;
    }
    private sealed class CloudHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => JsonResponse(QuotaPayload());
        public int Refreshes, Quotas;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com") Refreshes++;
            if (request.RequestUri.AbsolutePath.EndsWith("retrieveUserQuotaSummary")) Quotas++;
            return Task.FromResult(Reply(request));
        }
    }
    private static HttpResponseMessage JsonResponse(JsonNode json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json.ToJsonString()) };
    private static async Task CloudQuotaAsync()
    {
        var credentials = new CloudCredentials(); var handler = new CloudHandler(); using var http = new HttpClient(handler);
        var current = new JsonObject
        {
            ["token"] = new JsonObject { ["access_token"] = "access-one", ["refresh_token"] = "refresh-one", ["expiry"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") },
            ["auth_method"] = "oauth", ["id_token_email"] = "a@example.test"
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(current.ToJsonString());
        var unchanged = bytes.ToArray();
        credentials.Value = WindowsAntigravityCredentials.Parse(bytes);
        Check(credentials.Value is { Email: "a@example.test", Access: "access-one", Refresh: "refresh-one", Name: "Antigravity" }
            && credentials.Value.Expiry > DateTimeOffset.UtcNow && bytes.SequenceEqual(unchanged), "Current email-only credential was rejected or modified.");
        var fallback = new FakeQuotaProvider { Result = SampleAntigravity() };
        var currentProvider = new AntigravityCloudProvider(credentials, http, fallback, () => false);
        var direct = await currentProvider.ReadAsync(new CurrentQuotaRequest(), default);
        Check(direct.Status == "ok" && direct.Quotas.Count == 5 && fallback.Calls == 0 && handler.Refreshes == 0,
            "Current credential format required the running application or a token renewal.");
        var wrapped = System.Text.Encoding.UTF8.GetBytes("go-keyring-base64:" + Convert.ToBase64String(bytes) + "\0");
        Check(WindowsAntigravityCredentials.Parse(wrapped)?.Email == "a@example.test", "Wrapped current credentials were rejected.");
        var claims = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"email\":\"a@example.test\",\"name\":\"A\"}")).TrimEnd('=').Replace('+','-').Replace('/','_');
        current.Remove("id_token_email"); current["id_token"] = "fixture." + claims + ".fixture";
        var legacy = WindowsAntigravityCredentials.Parse(System.Text.Encoding.UTF8.GetBytes(current.ToJsonString()));
        Check(legacy is { Email: "a@example.test", Name: "A", Access: "access-one" }, "Legacy ID-token credential compatibility was lost.");
        current["id_token_email"] = "different@example.test";
        Check(WindowsAntigravityCredentials.Parse(System.Text.Encoding.UTF8.GetBytes(current.ToJsonString())) is null, "Conflicting credential identities were accepted.");
        current.Remove("id_token"); current.Remove("id_token_email");
        Check(WindowsAntigravityCredentials.Parse(System.Text.Encoding.UTF8.GetBytes(current.ToJsonString())) is null
            && WindowsAntigravityCredentials.Parse("{broken"u8.ToArray()) is null
            && WindowsAntigravityCredentials.Parse(new byte[65537]) is null, "Missing identity, malformed or oversized credentials were accepted.");
        credentials.Value = legacy;
        var provider = new AntigravityCloudProvider(credentials, http);
        var read = await provider.ReadAsync(new CurrentQuotaRequest(), default);
        Check(read.Status == "ok" && read.Quotas.Count == 5 && read.Email == "a@example.test" && handler.Refreshes == 0, "Direct query required app or refreshed valid token.");
        var local = new FakeQuotaProvider { Result = SampleAntigravity() with { Email = "a@example.test" } };
        handler.Reply = _ => { var payload = QuotaPayload(); ((JsonArray)payload["response"]!["groups"]!).RemoveAt(2); return JsonResponse(payload); };
        var directOnly = new AntigravityCloudProvider(credentials, http, local, () => false);
        Check((await directOnly.ReadAsync(new CurrentQuotaRequest(), default)).Quotas.Count == 4 && local.Calls == 0, "Direct query started unnecessary local discovery.");
        var enriched = new AntigravityCloudProvider(credentials, http, local, () => true);
        Check((await enriched.ReadAsync(new CurrentQuotaRequest(), default)).Quotas.Count == 5, "Confirmed same-session extra quota lost.");
        local.Result = local.Result with { Email = "different@example.test" };
        Check((await enriched.ReadAsync(new CurrentQuotaRequest(), default)).Quotas.Count == 4, "Local enrichment mixed accounts.");
        handler.Reply = _ => JsonResponse(QuotaPayload());
        credentials.Value = credentials.Value! with { Fingerprint = "expired", Expiry = DateTimeOffset.UtcNow.AddHours(-1) };
        handler.Reply = request => request.RequestUri!.Host == "oauth2.googleapis.com" ? JsonResponse(new JsonObject { ["access_token"] = "refreshed", ["expires_in"] = 3600 }) : JsonResponse(QuotaPayload());
        await provider.ReadAsync(new CurrentQuotaRequest(), default); await provider.ReadAsync(new CurrentQuotaRequest(), default);
        Check(handler.Refreshes == 1, "Refreshed credential not reused in memory.");
        handler.Reply = request => { if (request.RequestUri!.AbsolutePath.EndsWith("retrieveUserQuotaSummary")) credentials.Value = credentials.Value! with { Fingerprint = "changed", Email = "b@example.test" }; return JsonResponse(QuotaPayload()); };
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "error", "Changed session received previous account quotas.");
        credentials.Value = null; var calls = handler.Quotas;
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "error" && handler.Quotas == calls, "Logout used cached access token.");
    }
    private static async Task CloudFailuresAsync()
    {
        var credentials = new CloudCredentials(); var handler = new CloudHandler(); using var http = new HttpClient(handler);
        var provider = new AntigravityCloudProvider(credentials, http);
        handler.Reply = _ => new(HttpStatusCode.ServiceUnavailable);
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "error" && handler.Refreshes == 0, "Outage triggered OAuth refresh.");
        handler.Reply = _ => new(HttpStatusCode.TooManyRequests);
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).HttpStatus == 429 && handler.Refreshes == 0, "Rate limit ignored.");
        var rejected = 0;
        handler.Reply = request =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com") return JsonResponse(new JsonObject { ["access_token"] = "renewed", ["expires_in"] = 3600 });
            if (++rejected <= 2) return new(HttpStatusCode.Unauthorized);
            return JsonResponse(QuotaPayload());
        };
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "ok" && handler.Refreshes == 1, "401 did not refresh once.");
        handler.Reply = _ => JsonResponse(new JsonObject { ["groups"] = new JsonArray() });
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Quotas.All(q => q.UsedPercent is null), "Missing quotas became zero usage.");
    }
    private static Task ProviderModelIsolationAsync()
    {
        var date = DateTime.Today.ToString("yyyy-MM-dd");
        var codex = new TokenHistorySnapshot([new(date, new Dictionary<string, long> { ["shared-model"] = 900000 })], false, "now", null);
        var agy = new TokenHistorySnapshot([new(date, new Dictionary<string, long> { ["shared-model"] = 300 })], false, "now", null);
        var merged = ProviderHistory.Merge(codex, agy);
        Check(merged.Days.Single().Total == 900300 && merged.Days.Single().Models.Count == 2, "Same-named models combined across providers.");
        Check(merged.Sources![ProviderIds.Antigravity].Days.Single().Total == 300 && merged.Sources[ProviderIds.Codex].Days.Single().Total == 900000, "Cross-provider tokens copied.");
        merged = ProviderHistory.Merge(codex, TokenHistorySnapshot.Empty);
        Check(merged.Sources![ProviderIds.Antigravity].UpdatedAt is null && merged.Sources[ProviderIds.Antigravity].Days.Count == 0 && merged.Days.Single().Total == 900000, "Missing AG history borrowed Codex tokens.");
        return Task.CompletedTask;
    }
}
