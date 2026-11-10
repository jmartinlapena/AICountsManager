using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AICountsManager;

// Reads only Claude Desktop's own OAuth cache. No cookies, refresh tokens or writes.
internal sealed class ClaudeDesktopSessionSource(string directory) : IClientSessionSource
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
    private const int MaxBytes = 1024 * 1024;
    private const string ApiMarker = ":https://api.anthropic.com:";
    private const string ProductionClient = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public ClientSession? Read()
    {
        try
        {
            using var config = ReadJson(Path.Combine(directory, "config.json"));
            using var state = ReadJson(Path.Combine(directory, "Local State"));
            return Decode(config.RootElement, state.RootElement);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or FormatException or CryptographicException) { return null; }
    }
    internal static ClientSession? ReadSnapshot(byte[] config, byte[] state)
    {
        if (config.Length > MaxBytes || state.Length > MaxBytes) return null;
        try { using var c = JsonDocument.Parse(config); using var s = JsonDocument.Parse(state); return Decode(c.RootElement, s.RootElement); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or CryptographicException or InvalidDataException) { return null; }
    }
    private static ClientSession? Decode(JsonElement config, JsonElement state)
    {
        byte[]? key = null;
        try
        {
            if (!Guid.TryParse(ProviderJson.Text(config, "lastKnownAccountUuid"), out var account)) return null;
            var encoded = ProviderJson.Text(ProviderJson.Get(state, "os_crypt"), "encrypted_key");
            if (encoded is null) return null;
            var wrapped = Convert.FromBase64String(encoded);
            if (!wrapped.AsSpan().StartsWith("DPAPI"u8)) return null;
            key = ProtectedData.Unprotect(wrapped[5..], null, DataProtectionScope.CurrentUser);
            if (key.Length != 32) return null;
            using var v2 = DecodeCache(config, "oauth:tokenCacheV2", key);
            using var v1 = DecodeCache(config, "oauth:tokenCache", key);
            return Select(account, v2?.RootElement, v1?.RootElement, DateTimeOffset.UtcNow);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private static JsonDocument ReadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytes) throw new InvalidDataException();
        var bytes = new byte[(int)stream.Length + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count == bytes.Length) throw new InvalidDataException();
        return JsonDocument.Parse(bytes.AsMemory(0, count));
    }

    private static JsonDocument? DecodeCache(JsonElement config, string name, byte[] key)
    {
        var encoded = ProviderJson.Text(config, name);
        if (encoded is null) return null;
        var encrypted = Convert.FromBase64String(encoded);
        if (encrypted.Length < 31 || !encrypted.AsSpan().StartsWith("v10"u8)) throw new InvalidDataException();
        var plain = new byte[encrypted.Length - 31];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, plain.Length), encrypted.AsSpan(encrypted.Length - 16), plain);
            // Parse the string so the JSON document doesn't retain the buffer cleared below.
            return JsonDocument.Parse(Encoding.UTF8.GetString(plain));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private sealed record CacheKey(string Client, string Organization, string Scopes);
    private static Dictionary<CacheKey, JsonElement> Normalize(JsonElement? cache, Guid account)
    {
        var legacy = new Dictionary<CacheKey, JsonElement>();
        var scoped = new Dictionary<CacheKey, JsonElement>();
        if (cache is null) return legacy;
        if (cache.Value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var count = 0;
        foreach (var entry in cache.Value.EnumerateObject())
        {
            if (++count > 256) throw new InvalidDataException();
            var name = entry.Name;
            var owned = name.StartsWith("acct:", StringComparison.Ordinal);
            if (owned)
            {
                var separator = name.IndexOf('|');
                if (separator < 5 || !Guid.TryParse(name[5..separator], out var owner) || owner != account) continue;
                name = name[(separator + 1)..];
            }
            var marker = name.IndexOf(ApiMarker, StringComparison.Ordinal);
            var colon = name.IndexOf(':');
            if (colon < 0 || marker <= colon || !Guid.TryParse(name[..colon], out var client)
                || !Guid.TryParse(name[(colon + 1)..marker], out var org)) continue;
            var scopes = string.Join(' ', name[(marker + ApiMarker.Length)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().Order());
            (owned ? scoped : legacy)[new(client.ToString(), org.ToString(), scopes)] = entry.Value;
        }
        // Account-specific entries (including deletion markers) override legacy aliases.
        foreach (var entry in scoped) legacy[entry.Key] = entry.Value;
        return legacy;
    }

    internal static ClientSession? Select(Guid account, JsonElement? v2, JsonElement? v1, DateTimeOffset now)
    {
        var current = Normalize(v2, account);
        var currentKeys = current.Keys.ToHashSet();
        var older = Normalize(v1, account);
        foreach (var entry in older) current.TryAdd(entry.Key, entry.Value);
        // Include expired entries in the ambiguity check: expiration doesn't switch organizations.
        if (current.Where(e => e.Value.ValueKind == JsonValueKind.Object && e.Key.Scopes.Split(' ').Contains("user:profile"))
            .Select(e => e.Key.Organization).Distinct().Count() != 1) return null;
        var candidates = current.Where(e => e.Value.ValueKind == JsonValueKind.Object && e.Key.Scopes.Split(' ').Contains("user:profile")
            && ProviderJson.ValidToken(ProviderJson.Text(e.Value, "token"))
            && ProviderJson.Timestamp(ProviderJson.Get(e.Value, "expiresAt")) > now.ToUnixTimeSeconds() + 120).ToArray();
        if (candidates.Length == 0) return null;
        var chosen = candidates.OrderByDescending(e => currentKeys.Contains(e.Key))
            .ThenByDescending(e => e.Key.Client == ProductionClient && e.Key.Scopes.Split(' ').Contains("user:inference"))
            .ThenByDescending(e => e.Key.Scopes.Split(' ').Contains("user:inference"))
            .ThenByDescending(e => e.Key.Scopes.Split(' ').Length)
            .ThenByDescending(e => ProviderJson.Timestamp(ProviderJson.Get(e.Value, "expiresAt"))).First();
        var plan = ProviderJson.Text(chosen.Value, "subscriptionType");
        var tier = ProviderJson.Text(chosen.Value, "rateLimitTier");
        if (tier is not null && System.Text.RegularExpressions.Regex.Match(tier, @"\d+x") is { Success: true } match) plan += " " + match.Value;
        return new(ProviderJson.Text(chosen.Value, "token")!, Plan: plan,
            ExpiresAt: ProviderJson.Timestamp(ProviderJson.Get(chosen.Value, "expiresAt")),
            AccountId: account.ToString(), OrganizationId: chosen.Key.Organization);
    }
}
