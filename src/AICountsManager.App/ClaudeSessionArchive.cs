using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICountsManager;

internal sealed record ClaudeCredentialBundle(byte[]? DesktopAuth, string? DesktopKey,
    Dictionary<string, byte[]?> Cookies, byte[]? CodeAuth, byte[]? CodeAccount)
{
    public override string ToString() => "ClaudeCredentialBundle [redacted]";
}
internal sealed record ClaudeLogin(string Account, string Organization, ClientSession Session)
{
    public string Identity => Account + ":" + Organization;
    public override string ToString() => "ClaudeLogin [redacted]";
}

// One provider, two independently issued logins. Never translates a Desktop token into a CLI token.
internal sealed record ClaudeArchiveRecovery(string Desktop, string Code, string CodeConfig, bool NetworkCookies, Dictionary<string, byte[]?> Files);

internal sealed class ClaudeSessionArchive
{
    internal static readonly string[] AuthKeys = ["lastKnownAccountUuid", "oauth:tokenCache", "oauth:tokenCacheV2"];
    internal static readonly string[] CookieNames = ["Cookies", "Cookies-journal", "Cookies-wal", "Cookies-shm"];
    private readonly string _desktop, _code, _codeConfig, _journal;
    private readonly Func<bool> _running;
    private readonly HttpClient _http;
    internal string DesktopHome => _desktop;
    internal string DesktopConfig => Path.Combine(_desktop, "config.json");
    internal string CodeCredentials => Path.Combine(_code, ".credentials.json");
    internal string CookieDirectory => Directory.Exists(Path.Combine(_desktop, "Network")) ? Path.Combine(_desktop, "Network") : _desktop;
    internal bool Running => _running();
    internal IClientSessionSource LiveSource => new ClaudeSessionSource(_code, new ClaudeDesktopSessionSource(_desktop));
    internal bool HasRecovery => File.Exists(_journal);

    public ClaudeSessionArchive(string desktop, string code, string codeConfig, string journal, HttpClient http, Func<bool>? running = null)
    { _desktop = desktop; _code = code; _codeConfig = codeConfig; _journal = journal; _http = http; _running = running ?? IsClaudeRunning; }

    internal static string CodeConfigPath => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom
        ? Path.Combine(custom, ".claude.json") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    internal static bool IsClaudeRunning() => ClaudeProcessGuard.IsRunning();

    internal void RequireClosed()
    {
        if (Running) throw new InvalidOperationException("Cierra Claude Desktop y las sesiones de Claude Code antes de cambiar su cuenta. Codex puede permanecer abierto.");
    }

    internal static byte[]? ReadFile(string path, int maximum = 16 * 1024 * 1024)
    {
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("No se pueden sustituir enlaces de sesión.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maximum) throw new InvalidDataException("El archivo de sesión supera el tamaño admitido.");
        var data = new byte[(int)file.Length]; file.ReadExactly(data);
        if (file.ReadByte() != -1) throw new IOException("La sesión cambió mientras se leía. Vuelve a intentarlo.");
        return data;
    }

    private static JsonObject Object(byte[]? bytes) => bytes is null ? new() : JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException();
    private static byte[]? Select(byte[]? bytes, params string[] keys)
    {
        var source = Object(bytes); var result = new JsonObject();
        foreach (var key in keys) if (source.TryGetPropertyValue(key, out var value) && value is not null) result[key] = value.DeepClone();
        return result.Count == 0 ? null : JsonSerializer.SerializeToUtf8Bytes(result);
    }
    private static byte[] Merge(byte[]? current, byte[]? selected, params string[] keys)
    {
        var root = Object(current); var incoming = Object(selected);
        foreach (var key in keys) { root.Remove(key); if (incoming.TryGetPropertyValue(key, out var value)) root[key] = value?.DeepClone(); }
        return JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
    }
    private string? KeyFingerprint()
    {
        var state = Object(ReadFile(Path.Combine(_desktop, "Local State"), 1024 * 1024));
        return state["os_crypt"]?["encrypted_key"]?.GetValue<string>() is { } value ? AuthHash.Text(value) : null;
    }

    internal ClaudeCredentialBundle Capture()
    {
        RequireClosed();
        var desktop = Select(ReadFile(DesktopConfig, 1024 * 1024), AuthKeys);
        var code = Select(ReadFile(CodeCredentials, 128 * 1024), "claudeAiOauth");
        var codeAccount = Select(ReadFile(_codeConfig, 4 * 1024 * 1024), "oauthAccount");
        var cookies = CookieNames.ToDictionary(n => n, n => ReadFile(Path.Combine(CookieDirectory, n)));
        RequireClosed();
        return new(desktop, KeyFingerprint(), cookies, code, codeAccount);
    }

    internal async Task<ClaudeLogin?> IdentifyAsync(ClaudeCredentialBundle? saved, CancellationToken token)
    {
        var desktop = saved is null ? new ClaudeDesktopSessionSource(_desktop).Read()
            : saved.DesktopAuth is null ? null : ClaudeDesktopSessionSource.ReadSnapshot(saved.DesktopAuth, ReadFile(Path.Combine(_desktop, "Local State"), 1024 * 1024) ?? []);
        var cliBytes = saved?.CodeAuth ?? (saved is null ? ReadFile(CodeCredentials, 128 * 1024) : null);
        var cli = cliBytes is null ? null : ClaudeSessionSource.ParseCredential(cliBytes);
        var desktopLogin = desktop is null ? null : await IdentifyOneAsync(desktop, token).ConfigureAwait(false);
        var codeLogin = cli is null ? null : await IdentifyOneAsync(cli, token).ConfigureAwait(false);
        if (desktopLogin is not null && codeLogin is not null && desktopLogin.Identity != codeLogin.Identity)
            throw new InvalidOperationException("Claude Desktop y Claude Code tienen cuentas diferentes. Inicia la misma cuenta en ambos antes de guardar un perfil conjunto.");
        return desktopLogin ?? codeLogin;
    }

    private async Task<ClaudeLogin> IdentifyOneAsync(ClientSession session, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var request = ProviderHttp.Bearer("https://api.anthropic.com/api/oauth/profile", session.AccessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        var response = await ProviderHttp.SendAsync(_http, request, timeout.Token).ConfigureAwait(false);
        var account = ProviderJson.Get(response.Body, "account"); var org = ProviderJson.Get(response.Body, "organization");
        if (response.Status != 200 || !Guid.TryParse(ProviderJson.Text(account, "uuid"), out var id) || !Guid.TryParse(ProviderJson.Text(org, "uuid"), out var organization))
            throw new InvalidOperationException("No se pudo verificar la cuenta de Claude. Actualiza su sesión y vuelve a intentarlo.");
        if (session.AccountId is not null && (session.AccountId != id.ToString() || session.OrganizationId != organization.ToString()))
            throw new InvalidOperationException("La sesión de Claude no corresponde a su cuenta local.");
        var identified = session with { AccountId = id.ToString(), OrganizationId = organization.ToString(), Email = ProviderJson.Text(account, "email") };
        return new(id.ToString(), organization.ToString(), identified);
    }

    internal ClientSession? Session(ClaudeCredentialBundle bundle, string account, string organization)
    {
        if (bundle.DesktopAuth is not null && bundle.DesktopKey == KeyFingerprint())
        {
            var session = ClaudeDesktopSessionSource.ReadSnapshot(bundle.DesktopAuth, ReadFile(Path.Combine(_desktop, "Local State"), 1024 * 1024) ?? []);
            if (session is not null && session.AccountId == account && session.OrganizationId == organization) return session;
        }
        var cli = bundle.CodeAuth is null ? null : ClaudeSessionSource.ParseCredential(bundle.CodeAuth);
        return cli is null ? null : cli with { AccountId = account, OrganizationId = organization };
    }

    internal void Validate(ClaudeCredentialBundle bundle)
    {
        if (bundle.Cookies.Keys.Any(k => !CookieNames.Contains(k)) || bundle.Cookies.Values.Sum(b => (long)(b?.Length ?? 0)) > 64 * 1024 * 1024)
            throw new InvalidDataException("El perfil de Claude contiene archivos no admitidos.");
        if (bundle.DesktopAuth is not null && (bundle.DesktopKey is null || bundle.DesktopKey != KeyFingerprint()))
            throw new InvalidOperationException("La sesión cifrada de Claude pertenece a otra instalación. Vuelve a iniciar sesión en este equipo.");
        if (bundle.DesktopAuth is not null) _ = Object(bundle.DesktopAuth);
        if (bundle.CodeAuth is not null) _ = Object(bundle.CodeAuth);
    }

    internal static ClaudeCredentialBundle Empty => new(null, null, CookieNames.ToDictionary(n => n, _ => (byte[]?)null), null, null);

    // All paths are fixed by the adapter; an archive cannot select a destination.
    private Dictionary<string, string> Paths(string? cookies = null) => new()
    {
        ["desktop"] = DesktopConfig, ["code"] = CodeCredentials, ["code-account"] = _codeConfig,
        ["Cookies"] = Path.Combine(cookies ?? CookieDirectory, "Cookies"), ["Cookies-journal"] = Path.Combine(cookies ?? CookieDirectory, "Cookies-journal"),
        ["Cookies-wal"] = Path.Combine(cookies ?? CookieDirectory, "Cookies-wal"), ["Cookies-shm"] = Path.Combine(cookies ?? CookieDirectory, "Cookies-shm")
    };
    internal Action<int>? AfterWriteForTesting { get; set; }
    internal void Apply(ClaudeCredentialBundle bundle)
    {
        RequireClosed(); Recover(); Validate(bundle);
        var paths = Paths();
        var before = paths.ToDictionary(p => p.Key, p => ReadFile(p.Value));
        var after = new Dictionary<string, byte[]?>
        {
            ["desktop"] = before["desktop"] is null && bundle.DesktopAuth is null ? null : Merge(before["desktop"], bundle.DesktopAuth, AuthKeys),
            ["code"] = before["code"] is null && bundle.CodeAuth is null ? null : Merge(before["code"], bundle.CodeAuth, "claudeAiOauth"),
            ["code-account"] = before["code-account"] is null && bundle.CodeAccount is null ? null : Merge(before["code-account"], bundle.CodeAccount, "oauthAccount")
        };
        foreach (var name in CookieNames) after[name] = bundle.Cookies.GetValueOrDefault(name);
        // Preflight exclusive access to every existing destination, including the SQLite sidecars.
        foreach (var path in paths.Values.Where(File.Exists)) { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        SaveProtected(_journal, new ClaudeArchiveRecovery(Path.GetFullPath(_desktop), Path.GetFullPath(_code), Path.GetFullPath(_codeConfig), CookieDirectory != _desktop, before));
        try
        {
            var index = 0;
            foreach (var item in after)
            {
                RequireClosed(); WriteOrRemove(paths[item.Key], item.Value); AfterWriteForTesting?.Invoke(++index);
            }
            foreach (var item in after) if (!Equal(ReadFile(paths[item.Key]), item.Value)) throw new IOException("La verificación de Claude no coincide.");
            File.Delete(_journal);
        }
        catch
        {
            if (!Running) Recover(); // Never overwrite a client which raced us and opened mid-transaction.
            throw;
        }
    }
    internal void Recover()
    {
        if (!HasRecovery) return;
        RequireClosed();
        var recovery = LoadProtected<ClaudeArchiveRecovery>(_journal);
        if (recovery.Desktop != Path.GetFullPath(_desktop) || recovery.Code != Path.GetFullPath(_code) || recovery.CodeConfig != Path.GetFullPath(_codeConfig)) throw new InvalidDataException("La copia de recuperación pertenece a otra instalación.");
        var before = recovery.Files;
        var paths = Paths(recovery.NetworkCookies ? Path.Combine(_desktop, "Network") : _desktop);
        if (before.Count != paths.Count || before.Keys.Any(k => !paths.ContainsKey(k))) throw new InvalidDataException("Copia de recuperación de Claude inválida.");
        foreach (var pair in before) { RequireClosed(); WriteOrRemove(paths[pair.Key], pair.Value); }
        foreach (var pair in before) if (!Equal(ReadFile(paths[pair.Key]), pair.Value)) throw new IOException("No se pudo verificar la recuperación de Claude.");
        File.Delete(_journal);
    }
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static void WriteOrRemove(string path, byte[]? bytes)
    {
        // Reject links at every existing component before any write/delete.
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ruta de sesión enlazada no admitida.");
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); } else AtomicFile.WriteBytes(path, bytes);
    }
    internal static void SaveProtected<T>(string path, T data)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(data);
        try { AtomicFile.WriteBytes(path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static T LoadProtected<T>(string path)
    {
        var encrypted = ReadFile(path, 96 * 1024 * 1024) ?? throw new FileNotFoundException();
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
