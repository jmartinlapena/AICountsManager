using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AICountsManager;

internal sealed record AuthIdentity(
    string? UserId,
    string? AccountId,
    string? WorkspaceId,
    string? Email)
{
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(UserId)
        || !string.IsNullOrWhiteSpace(AccountId);

    public string Fingerprint => AuthHash.Text(
        $"{Normalize(UserId)}|{Normalize(AccountId)}|{Normalize(WorkspaceId)}");

    public bool Matches(AuthIdentity other)
    {
        var comparedStrongField = false;

        if (!Compatible(UserId, other.UserId, ref comparedStrongField)
            || !Compatible(AccountId, other.AccountId, ref comparedStrongField)
            || !Compatible(WorkspaceId, other.WorkspaceId, ref comparedStrongField))
        {
            return false;
        }

        if (comparedStrongField)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Email)
            && !string.IsNullOrWhiteSpace(other.Email)
            && string.Equals(Email.Trim(), other.Email.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Compatible(string? left, string? right, ref bool compared)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return true;
        }

        compared = true;
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

internal sealed record AuthSnapshot(
    byte[] Bytes,
    AuthIdentity Identity,
    string ContentFingerprint,
    string CredentialFingerprint,
    DateTimeOffset? LastRefresh,
    DateTimeOffset? TokenIssuedAt,
    DateTimeOffset? AccessTokenExpiresAt,
    string? AccessToken,
    string? AccountId)
{
    public DateTimeOffset? GenerationTime => LastRefresh ?? TokenIssuedAt;

    public int CompareGenerationTo(AuthSnapshot other)
    {
        if (string.Equals(CredentialFingerprint, other.CredentialFingerprint, StringComparison.Ordinal))
        {
            return 0;
        }

        var mine = GenerationTime;
        var theirs = other.GenerationTime;
        if (mine.HasValue && theirs.HasValue)
        {
            return mine.Value.CompareTo(theirs.Value);
        }
        if (mine.HasValue)
        {
            return 1;
        }
        if (theirs.HasValue)
        {
            return -1;
        }
        return 0;
    }

    public static AuthSnapshot Parse(byte[] bytes, string sourceDescription)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"{sourceDescription} esta vacio.");
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(bytes)?.AsObject()
                ?? throw new InvalidDataException($"{sourceDescription} no contiene un objeto JSON.");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"{sourceDescription} no es JSON valido.", ex);
        }

        var authMode = ReadString(root, "auth_mode");
        var apiKey = ReadString(root, "OPENAI_API_KEY");
        var tokens = root["tokens"] as JsonObject;

        if (tokens is null && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidDataException($"{sourceDescription} no contiene credenciales de Codex.");
        }

        var idToken = tokens is null ? null : ReadString(tokens, "id_token");
        var accessToken = tokens is null ? null : ReadString(tokens, "access_token");
        var refreshToken = tokens is null ? null : ReadString(tokens, "refresh_token");
        var accountId = tokens is null ? null : ReadString(tokens, "account_id");

        var idClaims = ParseJwtClaims(idToken);
        var accessClaims = ParseJwtClaims(accessToken);
        var idAuth = idClaims?["https://api.openai.com/auth"] as JsonObject;
        var accessAuth = accessClaims?["https://api.openai.com/auth"] as JsonObject;

        var userId = FirstNonEmpty(
            ReadString(idAuth, "chatgpt_user_id"),
            ReadString(accessAuth, "chatgpt_user_id"),
            ReadString(idAuth, "user_id"),
            ReadString(accessAuth, "user_id"),
            ReadString(idClaims, "sub"),
            ReadString(accessClaims, "sub"));

        var chatGptAccountId = FirstNonEmpty(
            ReadString(idAuth, "chatgpt_account_id"),
            ReadString(accessAuth, "chatgpt_account_id"));

        var workspaceId = FirstNonEmpty(
            ReadDefaultOrganizationId(idAuth),
            ReadDefaultOrganizationId(accessAuth),
            chatGptAccountId);

        var email = FirstNonEmpty(
            ReadString(idClaims, "email"),
            ReadString(idClaims?["https://api.openai.com/profile"] as JsonObject, "email"),
            ReadString(accessClaims?["https://api.openai.com/profile"] as JsonObject, "email"));

        var identity = new AuthIdentity(
            userId,
            FirstNonEmpty(accountId, chatGptAccountId),
            workspaceId,
            email);

        if (string.Equals(authMode, "chatgpt", StringComparison.OrdinalIgnoreCase)
            && (!identity.IsUsable || string.IsNullOrWhiteSpace(accessToken)))
        {
            throw new InvalidDataException($"{sourceDescription} no contiene una identidad ChatGPT utilizable.");
        }

        var lastRefresh = ParseDate(ReadString(root, "last_refresh"));
        var issuedAt = Max(ParseUnixDate(idClaims, "iat"), ParseUnixDate(accessClaims, "iat"));
        var accessExpiresAt = ParseUnixDate(accessClaims, "exp");
        var credentialMaterial = string.Join("\n", [
            authMode ?? string.Empty,
            apiKey ?? string.Empty,
            idToken ?? string.Empty,
            accessToken ?? string.Empty,
            refreshToken ?? string.Empty,
            accountId ?? string.Empty,
        ]);

        return new AuthSnapshot(
            Bytes: bytes,
            Identity: identity,
            ContentFingerprint: AuthHash.Bytes(bytes),
            CredentialFingerprint: AuthHash.Text(credentialMaterial),
            LastRefresh: lastRefresh,
            TokenIssuedAt: issuedAt,
            AccessTokenExpiresAt: accessExpiresAt,
            AccessToken: FirstNonEmpty(apiKey, accessToken),
            AccountId: FirstNonEmpty(accountId, chatGptAccountId));
    }

    private static JsonObject? ParseJwtClaims(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            return JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadDefaultOrganizationId(JsonObject? authClaims)
    {
        if (authClaims?["organizations"] is not JsonArray organizations)
        {
            return null;
        }

        string? first = null;
        foreach (var node in organizations)
        {
            if (node is not JsonObject organization)
            {
                continue;
            }

            var id = ReadString(organization, "id");
            first ??= id;
            if (organization["is_default"]?.GetValue<bool?>() == true)
            {
                return id;
            }
        }
        return first;
    }

    private static string? ReadString(JsonObject? node, string property)
    {
        try
        {
            return node?[property]?.GetValue<string?>()?.Trim() is { Length: > 0 } value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;

    private static DateTimeOffset? ParseUnixDate(JsonObject? claims, string property)
    {
        try
        {
            var seconds = claims?[property]?.GetValue<long?>();
            return seconds.HasValue ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (!left.HasValue)
        {
            return right;
        }
        if (!right.HasValue)
        {
            return left;
        }
        return left.Value >= right.Value ? left : right;
    }
}

internal static class StableAuthFile
{
    public static async Task<AuthSnapshot> ReadAsync(
        string path,
        TimeSpan? quietPeriod = null,
        int maxAttempts = 8,
        CancellationToken cancellationToken = default)
    {
        var quiet = quietPeriod ?? TimeSpan.FromMilliseconds(140);
        Exception? lastError = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var first = await ReadBytesSharedAsync(path, cancellationToken);
                var firstHash = AuthHash.Bytes(first);
                await Task.Delay(quiet, cancellationToken);
                var second = await ReadBytesSharedAsync(path, cancellationToken);
                if (!string.Equals(firstHash, AuthHash.Bytes(second), StringComparison.Ordinal))
                {
                    continue;
                }

                return AuthSnapshot.Parse(second, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                lastError = ex;
                await Task.Delay(quiet, cancellationToken);
            }
        }

        throw new IOException($"No se pudo leer un auth.json estable y valido en '{path}'.", lastError);
    }

    private static async Task<byte[]> ReadBytesSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }
}

internal static class AuthHash
{
    public static string Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Text(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    public static string Short(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}
