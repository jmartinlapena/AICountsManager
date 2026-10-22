using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexManager;

internal sealed record LocalQuotaEndpoint(int ProcessId, Uri Address, string Csrf);
internal interface IAntigravityConnection
{
    Task<IReadOnlyList<LocalQuotaEndpoint>> DiscoverAsync(CancellationToken token);
    Task<JsonNode?> CallAsync(LocalQuotaEndpoint endpoint, string method, CancellationToken token);
}
internal sealed class AntigravityQuotaProvider(IAntigravityConnection connection) : IQuotaProvider
{
    public string Id => ProviderIds.Antigravity;
    public bool CanSwitchAccounts => false;
    public async Task<ProviderUsage> ReadAsync(QuotaRequest request, CancellationToken token)
    {
        if (request is not CurrentQuotaRequest) throw new ArgumentException("Antigravity solo consulta la sesión actual.");
        var endpoints = await connection.DiscoverAsync(token).ConfigureAwait(false);
        ProviderUsage? found = null;
        foreach (var process in endpoints.GroupBy(e => e.ProcessId).Take(4))
        {
            foreach (var endpoint in process.Take(8))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var status = await connection.CallAsync(endpoint, "GetUserStatus", token).ConfigureAwait(false);
                    var user = status?["userStatus"];
                    var email = String(user?["email"]);
                    if (email is null) continue; // Do not attach a previous account's limits to an anonymous session.
                    var summary = await connection.CallAsync(endpoint, "RetrieveUserQuotaSummary", token).ConfigureAwait(false);
                    var verified = await connection.CallAsync(endpoint, "GetUserStatus", token).ConfigureAwait(false);
                    if (!string.Equals(email, String(verified?["userStatus"]?["email"]), StringComparison.OrdinalIgnoreCase)) continue;
                    var quotas = ParseSummary(summary);
                    var result = new ProviderUsage(Id, AuthHash.Bytes(Encoding.UTF8.GetBytes(email.ToLowerInvariant())),
                        String(user?["name"]) ?? "Antigravity", email,
                        String(user?["userTier"]?["name"]) ?? String(user?["planStatus"]?["planInfo"]?["planName"]),
                        DateTimeOffset.UtcNow.ToString("O"), quotas is null ? "error" : "ok",
                        quotas is null ? "Esta versión no expone el resumen de cuotas. Actualiza Antigravity." : null, quotas ?? EmptyQuotas());
                    if (found is not null && found.Identity != result.Identity)
                        return ProviderUsage.Unavailable(Id, "Hay varias sesiones distintas de Antigravity abiertas. Deja abierta la que quieras consultar.");
                    found = result;
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException) { }
            }
        }
        return found ?? ProviderUsage.Unavailable(Id, "Abre Antigravity e inicia sesión para consultar sus límites.");
    }
    internal static readonly (string Id, string Group, string Label, int Minutes)[] Specs =
    [
        ("gemini-weekly", "Gemini", "Semanal", 10080), ("gemini-5h", "Gemini", "Sesión · 5 h", 300),
        ("3p-weekly", "Claude / GPT", "Semanal", 10080), ("3p-5h", "Claude / GPT", "Sesión · 5 h", 300)
    ];
    private static string? String(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s[..Math.Min(s.Length, 160)] : null;
    internal static List<ProviderQuota> EmptyQuotas() => Specs.Select(s => new ProviderQuota(s.Id, s.Group, s.Label, null, s.Minutes, null)).ToList();
    internal static IReadOnlyList<ProviderQuota>? ParseSummary(JsonNode? root)
    {
        if ((root?["response"] ?? root)?["groups"] is not JsonArray groups) return null;
        var values = EmptyQuotas();
        var buckets = groups.OfType<JsonObject>().Where(g => String(g["displayName"]) != "Gemini 4 Argon")
            .SelectMany(g => (g["buckets"] as JsonArray ?? []).OfType<JsonObject>()).ToArray();
        for (var i = 0; i < Specs.Length; i++)
        {
            var spec = Specs[i];
            var bucket = buckets.FirstOrDefault(b => String(b["bucketId"]) == spec.Id && Fraction(b) is not null);
            if (bucket is not null) values[i] = Quota(bucket, spec.Id, spec.Group, spec.Label, spec.Minutes);
        }
        var argon = groups.OfType<JsonObject>().Where(g => String(g["displayName"]) == "Gemini 4 Argon")
            .SelectMany(g => (g["buckets"] as JsonArray ?? []).OfType<JsonObject>()).Where(b => String(b["window"]) == "weekly").ToArray();
        if (argon.Length == 1 && String(argon[0]["bucketId"]) is { } id && !Specs.Any(s => s.Id == id) && Fraction(argon[0]) is not null)
            values.Add(Quota(argon[0], id, "Gemini 4 Argon", "Semanal", 10080));
        return values;
    }
    private static double? Fraction(JsonObject bucket)
    {
        if (bucket["disabled"] is { } disabled && (disabled is not JsonValue d || !d.TryGetValue<bool>(out var flag) || flag)) return null;
        if (bucket["remainingFraction"] is not JsonValue n || n.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        if (!double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        return double.IsFinite(v) && v >= 0 && v <= 1 ? v : null;
    }
    private static ProviderQuota Quota(JsonObject b, string id, string group, string label, int minutes) =>
        new(id, group, label, Fraction(b) is double f ? (1 - f) * 100 : null, minutes,
            DateTimeOffset.TryParse(String(b["resetTime"]), out var t) ? t.ToUnixTimeSeconds() : null);
}

// Windows discovery is on demand, off the UI thread. No process polling, shell helper,
// credential cache or OAuth refresh. The app's session token only goes to its own loopback port.
internal sealed class WindowsAntigravityConnection : IAntigravityConnection
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = false, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri is { Host: "127.0.0.1", Scheme: "https" }
    }) { Timeout = TimeSpan.FromSeconds(3) };
    public Task<IReadOnlyList<LocalQuotaEndpoint>> DiscoverAsync(CancellationToken token) => Task.Run<IReadOnlyList<LocalQuotaEndpoint>>(() =>
    {
        var endpoints = new List<LocalQuotaEndpoint>();
        object? locator = null, service = null, result = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
            result = ((dynamic)service).ExecQuery("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='language_server.exe' OR Name='language_server_windows_x64.exe' OR Name='language_server_windows_arm.exe'");
            if (Convert.ToInt32(((dynamic)result).Count) == 0) return endpoints;
            var listeners = ListeningPorts();
            foreach (var item in (System.Collections.IEnumerable)result)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    dynamic process = item;
                    var path = ReadProperty(item, "ExecutablePath") as string; var command = ReadProperty(item, "CommandLine") as string;
                    if (path is null || !path.Contains("\\antigravity\\", StringComparison.OrdinalIgnoreCase) || command is null) continue;
                    var csrf = Regex.Match(command, "--csrf_token(?:=|\\s+)(?:\"([^\"]+)\"|([^\\s]+))");
                    if (!csrf.Success) continue;
                    var secret = csrf.Groups[1].Success ? csrf.Groups[1].Value : csrf.Groups[2].Value;
                    if (secret.Length > 512 || secret.Any(char.IsControl)) continue;
                    int pid = Convert.ToInt32(ReadProperty(item, "ProcessId"));
                    if (!listeners.TryGetValue(pid, out var ports)) continue;
                    foreach (var port in ports.Order().Take(4))
                    {
                        endpoints.Add(new(pid, new Uri($"https://127.0.0.1:{port}/"), secret));
                        endpoints.Add(new(pid, new Uri($"http://127.0.0.1:{port}/"), secret));
                    }
                }
                catch (COMException) { /* A process can exit while WMI enumerates it. */ }
                finally { Release(item); }
            }
        }
        finally { Release(result); Release(service); Release(locator); }
        return endpoints;
    }, token);
    private static object? ReadProperty(object item, string name)
    {
        object? properties = null, property = null;
        try
        {
            properties = ((dynamic)item).Properties_;
            property = ((dynamic)properties).Item(name, 0);
            return ((dynamic)property).Value;
        }
        finally { Release(property); Release(properties); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public async Task<JsonNode?> CallAsync(LocalQuotaEndpoint endpoint, string method, CancellationToken token)
    {
        if (endpoint.Address.Host != "127.0.0.1" || endpoint.Address.Scheme is not ("https" or "http") ||
            method is not ("GetUserStatus" or "RetrieveUserQuotaSummary")) throw new InvalidOperationException("Destino de consulta local no válido.");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.Address, "exa.language_server_pb.LanguageServerService/" + method));
        message.Headers.Add("x-codeium-csrf-token", endpoint.Csrf);
        message.Headers.Add("Connect-Protocol-Version", "1");
        message.Content = new StringContent("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}", Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Respuesta demasiado grande.");
        using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await input.ReadAsync(bytes, deadline.Token).ConfigureAwait(false)) != 0)
        { if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Respuesta demasiado grande."); buffer.Write(bytes, 0, count); }
        return JsonNode.Parse(buffer.ToArray());
    }
    private static Dictionary<int, List<int>> ListeningPorts()
    {
        var size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size <= 0 || size > 4 * 1024 * 1024) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) return [];
            var count = Marshal.ReadInt32(buffer); var result = new Dictionary<int, List<int>>();
            if (count < 0 || count > (size - 4) / 24) return result;
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * 24); var address = Marshal.ReadInt32(row, 4);
                if (address != 0 && address != 0x0100007f) continue;
                var raw = Marshal.ReadInt32(row, 8); var port = ((raw & 255) << 8) | ((raw >> 8) & 255);
                var pid = Marshal.ReadInt32(row, 20); if (!result.TryGetValue(pid, out var ports)) result[pid] = ports = [];
                if (port > 0 && !ports.Contains(port)) ports.Add(port);
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
