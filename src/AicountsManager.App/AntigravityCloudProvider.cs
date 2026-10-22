using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace AicountsManager;

internal sealed record AntigravityCredential(string Fingerprint, string Access, string Refresh, DateTimeOffset Expiry, string Email, string Name);
internal interface IAntigravityCredentials { AntigravityCredential? Read(); }
internal sealed class WindowsAntigravityCredentials : IAntigravityCredentials
{
    public AntigravityCredential? Read()
    {
        if (!CredRead("gemini:antigravity", 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.Size is 0 or > 65536) return null;
            var bytes = new byte[credential.Size]; Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            if (text.StartsWith("go-keyring-base64:")) text = Encoding.UTF8.GetString(Convert.FromBase64String(text[18..]));
            var root = JsonNode.Parse(text); var token = root?["token"];
            var id = root?["id_token"]?.GetValue<string>()?.Split('.');
            if (id is not { Length: 3 }) return null;
            var encoded = id[1].Replace('-', '+').Replace('_', '/');
            var claims = JsonNode.Parse(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            var email = claims?["email"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(email)) return null;
            DateTimeOffset.TryParse(token?["expiry"]?.GetValue<string>(), out var expiry);
            return new(AuthHash.Bytes(bytes), token?["access_token"]?.GetValue<string>() ?? "", token?["refresh_token"]?.GetValue<string>() ?? "", expiry, email, claims?["name"]?.GetValue<string>() ?? "Antigravity");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException) { return null; }
        finally { CredFree(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type; public IntPtr Target, Comment; public long LastWritten; public uint Size;
        public IntPtr Blob; public uint Persist, Count; public IntPtr Attributes, Alias, User;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}

// The installed-app OAuth client is public and ships with Antigravity. User tokens stay
// in memory; this adapter never writes Windows credentials or changes the active account.
internal sealed class AntigravityCloudProvider(IAntigravityCredentials credentials, HttpClient http, IQuotaProvider? localFallback = null, Func<bool>? hasRichLocalClient = null) : IQuotaProvider
{
    public string Id => ProviderIds.Antigravity;
    public bool CanSwitchAccounts => false;
    private string? _fingerprint, _access, _plan;
    private DateTimeOffset _expiry;
    private readonly SemaphoreSlim _gate = new(1);
    internal static IQuotaProvider Create() => new AntigravityCloudProvider(new WindowsAntigravityCredentials(),
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) },
        new AntigravityQuotaProvider(new WindowsAntigravityConnection()), HasStandaloneClient);
    private static bool HasStandaloneClient()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("language_server");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public async Task<ProviderUsage> ReadAsync(QuotaRequest request, CancellationToken token)
    {
        if (request is not CurrentQuotaRequest) throw new ArgumentException("Antigravity solo consulta la sesión actual.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var credential = credentials.Read();
            if (credential is null)
            {
                _fingerprint = _access = _plan = null;
                return localFallback is null ? Missing("Inicia sesión en Antigravity una vez para habilitar la consulta directa.") : await localFallback.ReadAsync(request, token).ConfigureAwait(false);
            }
            if (_fingerprint != credential.Fingerprint)
            { _fingerprint = credential.Fingerprint; _access = credential.Access; _expiry = credential.Expiry; _plan = null; }
            try
            {
                var refreshed = false;
                if (string.IsNullOrEmpty(_access) || _expiry <= DateTimeOffset.UtcNow.AddSeconds(30))
                { await Refresh(credential, token).ConfigureAwait(false); refreshed = true; }
                var quotas = await ReadQuotas(token).ConfigureAwait(false);
                if (quotas.Status is 401 or 403 && !refreshed)
                { await Refresh(credential, token).ConfigureAwait(false); quotas = await ReadQuotas(token).ConfigureAwait(false); }
                if (quotas.Status is 401 or 403) return Missing("La sesión de Antigravity ha caducado. Vuelve a iniciar sesión en Antigravity.");
                if (quotas.Status == 429) return Missing("Google ha limitado las consultas. Espera antes de actualizar.") with { HttpStatus = 429, RetryAfterSeconds = 60 };
                if (quotas.Values is null) return Missing("No se pudo consultar Google. Inténtalo de nuevo más tarde.");
                if (_plan is null)
                {
                    try
                    {
                        using var planDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); planDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                        var plan = await Post("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist", new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}", Encoding.UTF8, "application/json"), true, planDeadline.Token).ConfigureAwait(false);
                        _plan = Text(plan.Json?["paidTier"]?["name"]) ?? Text(plan.Json?["currentTier"]?["name"]);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException || ex is OperationCanceledException && !token.IsCancellationRequested) { }
                }
                if (quotas.Values.Count < 5 && localFallback is not null && (hasRichLocalClient?.Invoke() ?? true))
                {
                    using var localDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); localDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        var local = await localFallback.ReadAsync(request, localDeadline.Token).ConfigureAwait(false);
                        if (local.Status == "ok" && string.Equals(local.Email, credential.Email, StringComparison.OrdinalIgnoreCase))
                            quotas.Values = quotas.Values.Concat(local.Quotas.Where(q => !quotas.Values.Any(c => c.Id == q.Id))).ToArray();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { }
                }
                if (credentials.Read()?.Fingerprint != credential.Fingerprint)
                { _fingerprint = _access = _plan = null; return Missing("La sesión de Antigravity cambió durante la consulta. Actualiza de nuevo."); }
                return new(Id, AuthHash.Bytes(Encoding.UTF8.GetBytes(credential.Email.ToLowerInvariant())), credential.Name, credential.Email,
                    _plan, DateTimeOffset.UtcNow.ToString("O"), "ok", null, quotas.Values);
            }
            catch (CloudAuthException) { return Missing("La sesión de Antigravity ha caducado. Vuelve a iniciar sesión en Antigravity."); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException)
            { return Missing("No se pudo consultar Google. Inténtalo de nuevo más tarde."); }
        }
        finally { _gate.Release(); }
    }
    private ProviderUsage Missing(string message) => ProviderUsage.Unavailable(Id, message);
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length <= 160 ? text : null;
    private async Task Refresh(AntigravityCredential credential, CancellationToken token)
    {
        if (string.IsNullOrEmpty(credential.Refresh)) throw new CloudAuthException();
        var response = await Post("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com",
            ["client_secret"] = "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", ["grant_type"] = "refresh_token", ["refresh_token"] = credential.Refresh
        }), false, token).ConfigureAwait(false);
        if (response.Status is >= 400 and < 500 && response.Status is not (408 or 429)) throw new CloudAuthException();
        if (response.Status != 200 || response.Json?["access_token"] is not JsonValue access || !access.TryGetValue<string>(out var value) || string.IsNullOrEmpty(value)) throw new IOException();
        _access = value;
        var lifetime = response.Json?["expires_in"] is JsonValue seconds && seconds.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 86400) : 3600;
        _expiry = DateTimeOffset.UtcNow.AddSeconds(lifetime);
    }
    private async Task<(int Status, IReadOnlyList<ProviderQuota>? Values)> ReadQuotas(CancellationToken token)
    {
        List<ProviderQuota>? values = null;
        foreach (var host in new[] { "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" })
        {
            (int Status, JsonNode? Json) response;
            try { response = await Post("https://" + host + "/v1internal:retrieveUserQuotaSummary", new StringContent("{}", Encoding.UTF8, "application/json"), true, token).ConfigureAwait(false); }
            catch (HttpRequestException) { continue; }
            if (response.Status is 401 or 403 or 429) return (response.Status, null);
            if (response.Status != 200) continue;
            var parsed = AntigravityQuotaProvider.ParseSummary(response.Json);
            if (parsed is null) continue;
            values ??= [];
            foreach (var quota in parsed)
            {
                var i = values.FindIndex(q => q.Id == quota.Id);
                if (i < 0) values.Add(quota);
                else if (values[i].UsedPercent is null && quota.UsedPercent is not null) values[i] = quota;
            }
            if (values.Count >= 5 && values.All(q => q.UsedPercent is not null)) break;
        }
        return (values is null ? 503 : 200, values);
    }
    private async Task<(int Status, JsonNode? Json)> Post(string url, HttpContent content, bool bearer, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.UserAgent.ParseAdd("antigravity/2.25.0 (aidev_client; os_type=windows; arch=amd64)");
        if (bearer) request.Headers.Authorization = new("Bearer", _access);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, null);
        if (response.Content.Headers.ContentLength > 1048576) throw new IOException();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        { if (buffer.Length + count > 1048576) throw new IOException(); buffer.Write(chunk, 0, count); }
        return ((int)response.StatusCode, JsonNode.Parse(buffer.ToArray()));
    }
    private sealed class CloudAuthException : Exception;
}


