using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AICountsManager;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private const string DesktopAccount = "11111111-1111-4111-8111-111111111111";
    private const string DesktopOrg = "22222222-2222-4222-8222-222222222222";
    private static string DesktopKey(string account = DesktopAccount, string org = DesktopOrg) =>
        $"acct:{account}|9d1c250a-e61b-44d9-88ed-5944d1962f5e:{org}:https://api.anthropic.com:user:profile user:inference";
    private static object DesktopEntry(string token = "desktop-fixture", int hours = 4) => new
    {
        token, refreshToken = "must-not-be-used", expiresAt = DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeMilliseconds(),
        subscriptionType = "max", rateLimitTier = "max_5x"
    };
    private static Task ClaudeDesktopSelectionAsync()
    {
        var account = Guid.Parse(DesktopAccount); var now = DateTimeOffset.UtcNow;
        JsonElement Cache(Dictionary<string, object?> entries) => Element(JsonSerializer.Serialize(entries));
        var original = Cache(new() { [DesktopKey()] = DesktopEntry() });
        Check(ClaudeDesktopSessionSource.Select(account, original, null, now)?.AccessToken == "desktop-fixture", "Desktop session not selected.");
        Check(ClaudeDesktopSessionSource.Select(account, Cache(new() { [DesktopKey()] = null }), original, now) is null, "V2 logout resurrected V1 token.");
        var legacyKey = DesktopKey().Split('|')[1];
        Check(ClaudeDesktopSessionSource.Select(account, Cache(new() { [legacyKey] = DesktopEntry(), [DesktopKey()] = null }), null, now) is null, "Scoped logout resurrected legacy alias.");
        Check(ClaudeDesktopSessionSource.Select(account, Cache(new() { [DesktopKey("33333333-3333-4333-8333-333333333333")] = DesktopEntry() }), null, now) is null, "Another account supplied a token.");
        Check(ClaudeDesktopSessionSource.Select(account, Cache(new() { [DesktopKey()] = DesktopEntry(hours: -1) }), original, now) is null, "Expired V2 resurrected an older login.");
        Check(ClaudeDesktopSessionSource.Select(account, Cache(new() { [DesktopKey().Replace("user:profile ", "")] = DesktopEntry() }), null, now) is null, "Inference-only token selected for usage.");
        Check(ClaudeDesktopSessionSource.Select(account, Cache(new() { [DesktopKey()] = DesktopEntry(), [DesktopKey(org: "44444444-4444-4444-8444-444444444444")] = DesktopEntry() }), null, now) is null, "Ambiguous organizations were guessed.");
        return Task.CompletedTask;
    }
    private static Task ClaudeDesktopStorageAsync() => InTemporaryStoreAsync(async paths =>
    {
        var desktop = Path.Combine(paths.AppHome, "desktop"); Directory.CreateDirectory(desktop);
        var key = RandomNumberGenerator.GetBytes(32);
        string Encrypt(string json)
        {
            var plain = Encoding.UTF8.GetBytes(json); var bytes = new byte[31 + plain.Length];
            "v10"u8.CopyTo(bytes); RandomNumberGenerator.Fill(bytes.AsSpan(3, 12));
            using var aes = new AesGcm(key, 16); aes.Encrypt(bytes.AsSpan(3, 12), plain, bytes.AsSpan(15, plain.Length), bytes.AsSpan(bytes.Length - 16));
            return Convert.ToBase64String(bytes);
        }
        var wrapped = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        await File.WriteAllTextAsync(Path.Combine(desktop, "Local State"), JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(wrapped).ToArray()) } }));
        var config = Path.Combine(desktop, "config.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["lastKnownAccountUuid"] = DesktopAccount,
            ["oauth:tokenCacheV2"] = Encrypt(JsonSerializer.Serialize(new Dictionary<string, object> { [DesktopKey()] = DesktopEntry() }))
        }));
        var before = File.ReadAllBytes(config); var reader = new ClaudeDesktopSessionSource(desktop);
        var read = reader.Read();
        Check(read?.AccessToken == "desktop-fixture" && read.Plan == "max 5x" && read.AccountId == DesktopAccount && read.OrganizationId == DesktopOrg, "Windows Desktop decrypt/selection failed.");
        Check(before.SequenceEqual(File.ReadAllBytes(config)), "Desktop config was modified.");
        var home = Path.Combine(paths.AppHome, "terminal"); Directory.CreateDirectory(home);
        var combined = new ClaudeSessionSource(home, reader);
        Check(combined.Read()?.AccessToken == "desktop-fixture", "Absent CLI did not fall back to Desktop.");
        var cli = Path.Combine(home, ".credentials.json");
        await File.WriteAllTextAsync(cli, "{\"claudeAiOauth\":{\"accessToken\":\"cli-fixture\"}}");
        Check(combined.Read()?.AccessToken == "cli-fixture", "Existing terminal login lost priority.");
        await File.WriteAllTextAsync(cli, "{\"claudeAiOauth\":{\"accessToken\":\"expired-cli\",\"expiresAt\":1}}");
        Check(combined.Read()?.AccessToken == "desktop-fixture", "Expired terminal did not fall back to Desktop.");
        await File.WriteAllTextAsync(config, "{\"lastKnownAccountUuid\":\"invalid\"}");
        Check(reader.Read() is null, "Invalid Desktop state reused a cached credential.");
        await File.WriteAllTextAsync(config, new string('x', 1024 * 1024 + 1));
        Check(reader.Read() is null, "Oversized cache was accepted.");
        CryptographicOperations.ZeroMemory(key);
    });
    private static async Task ClaudeDesktopIdentityAsync()
    {
        var session = new SessionFixture { Value = new("desktop-fixture", AccountId: DesktopAccount, OrganizationId: DesktopOrg) };
        var handler = new SessionHttp(); using var http = new System.Net.Http.HttpClient(handler);
        var wrong = false;
        handler.Reply = (request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("profile")
            ? JsonSerializer.Serialize(new { account = new { uuid = wrong ? Guid.NewGuid().ToString() : DesktopAccount }, organization = new { uuid = DesktopOrg } }) : ClaudeFixture()));
        var provider = new ClaudeQuotaProvider(session, http);
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "ok", "Matching Desktop identity rejected.");
        wrong = true;
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "disconnected", "Desktop identity mismatch accepted.");
        handler.Reply = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("profile")
            ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) : Response(ClaudeFixture()));
        Check((await provider.ReadAsync(new CurrentQuotaRequest(), default)).Status == "disconnected", "Unverified Desktop session accepted.");
    }
    private static async Task ProbeClaudeDesktopAsync(string desktop)
    {
        var source = new ClaudeDesktopSessionSource(desktop);
        var session = source.Read();
        using var http = ProviderHttp.Create();
        var result = session is null ? ProviderUsage.Unavailable(ProviderIds.Claude, "No session")
            : await new ClaudeQuotaProvider(source, http).ReadAsync(new CurrentQuotaRequest(), default);
        Console.WriteLine(JsonSerializer.Serialize(new { SessionFound = session is not null, result.Status, result.HttpStatus,
            QuotaCount = result.Quotas.Count, Quotas = result.Quotas.Select(q => new { q.Label, q.UsedPercent, q.Minutes }) }));
    }
}
