using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexManager;

internal sealed record CodexRuntimeState(
    bool IsRunning,
    string? DesktopExecutablePath,
    IReadOnlyList<int> ProcessIds);

internal interface ICodexProcessController
{
    CodexRuntimeState CaptureState();
    Task<bool> CloseNormallyAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<bool> RestartAsync(string? preferredExecutablePath, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class CodexProcessController : ICodexProcessController
{
    public CodexRuntimeState CaptureState()
    {
        var processIds = new List<int>();
        string? desktopExecutable = null;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    var executable = TryGetExecutablePath(process);
                    var isDesktop = string.Equals(name, "ChatGPT", StringComparison.OrdinalIgnoreCase)
                        && IsCodexDesktopPackagePath(executable);
                    var isBundledCodex =
                        (string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "codex-code-mode-host", StringComparison.OrdinalIgnoreCase))
                        && IsCodexDesktopPackagePath(executable);

                    if (!isDesktop && !isBundledCodex)
                    {
                        continue;
                    }

                    processIds.Add(process.Id);
                    if (isDesktop && !string.IsNullOrWhiteSpace(executable))
                    {
                        desktopExecutable ??= executable;
                    }
                }
                catch
                {
                }
            }
        }

        return new CodexRuntimeState(processIds.Count > 0, desktopExecutable, processIds);
    }

    public async Task<bool> CloseNormallyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var desktopProcesses = Process.GetProcessesByName("ChatGPT");
        try
        {
            foreach (var process in desktopProcesses.OrderByDescending(item => item.MainWindowHandle != IntPtr.Zero))
            {
                try
                {
                    if (IsCodexDesktopPackagePath(TryGetExecutablePath(process)))
                    {
                        process.CloseMainWindow();
                    }
                }
                catch
                {
                }
            }
        }
        finally
        {
            foreach (var process in desktopProcesses)
            {
                process.Dispose();
            }
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CaptureState().IsRunning)
            {
                return true;
            }
            await Task.Delay(250, cancellationToken);
        }

        return !CaptureState().IsRunning;
    }

    public async Task<bool> RestartAsync(
        string? preferredExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(preferredExecutablePath) && File.Exists(preferredExecutablePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = preferredExecutablePath,
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App",
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CaptureState().IsRunning)
            {
                return true;
            }
            await Task.Delay(250, cancellationToken);
        }
        return CaptureState().IsRunning;
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCodexDesktopPackagePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)
        && path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
}

internal static class CodexBinaryLocator
{
    public static string? Find()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_BINARY");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        foreach (var name in new[] { "codex.exe", "codex" })
        {
            var fromPath = FindInPath(name);
            if (fromPath is not null)
            {
                return fromPath;
            }
        }

        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        try
        {
            foreach (var directory in Directory.GetDirectories(packageRoot, "OpenAI.Codex_*").OrderDescending())
            {
                var candidate = Path.Combine(directory, "app", "resources", "codex.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? FindInPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var segment in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }
        return null;
    }
}

internal enum AuthVerificationStatus
{
    Success,
    RefreshTokenInvalidated,
    Failed,
    Unavailable,
}

internal sealed record AuthVerificationResult(
    AuthVerificationStatus Status,
    string? Email,
    string? Error)
{
    public static AuthVerificationResult Unavailable(string error) =>
        new(AuthVerificationStatus.Unavailable, null, error);
}

internal interface ICodexAppServerClient
{
    Task<AuthVerificationResult> VerifyAsync(bool forceRefresh, CancellationToken cancellationToken);
    Task<UsageRecord> ReadRateLimitsAsync(CancellationToken cancellationToken);
}

internal sealed class CodexAppServerClient : ICodexAppServerClient
{
    private readonly AppPaths _paths;

    public CodexAppServerClient(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<AuthVerificationResult> VerifyAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var binary = CodexBinaryLocator.Find();
        if (binary is null)
        {
            return AuthVerificationResult.Unavailable("No se encontro el binario de Codex para verificar la cuenta.");
        }

        try
        {
            await using var session = await AppServerSession.StartAsync(binary, _paths.CodexHome, cancellationToken);
            var response = await session.RequestAsync(
                "account/read",
                new JsonObject { ["refreshToken"] = forceRefresh },
                cancellationToken);

            if (response["error"] is not null)
            {
                var serialized = response["error"]!.ToJsonString();
                return serialized.Contains("refresh_token_invalidated", StringComparison.OrdinalIgnoreCase)
                    ? new AuthVerificationResult(
                        AuthVerificationStatus.RefreshTokenInvalidated,
                        null,
                        "El refresh_token fue invalidado y la cuenta necesita autenticarse de nuevo.")
                    : new AuthVerificationResult(
                        AuthVerificationStatus.Failed,
                        null,
                        "Codex app-server no pudo verificar la cuenta.");
            }

            var account = response["result"]?["account"] as JsonObject;
            if (account is null)
            {
                return new AuthVerificationResult(
                    AuthVerificationStatus.Failed,
                    null,
                    "Codex app-server no reconocio una cuenta activa.");
            }

            return new AuthVerificationResult(
                AuthVerificationStatus.Success,
                account["email"]?.GetValue<string?>(),
                null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidOperationException)
        {
            var message = ex.Message.Contains("refresh_token_invalidated", StringComparison.OrdinalIgnoreCase)
                ? "El refresh_token fue invalidado y la cuenta necesita autenticarse de nuevo."
                : "No se pudo completar la verificacion mediante Codex app-server.";
            var status = ex.Message.Contains("refresh_token_invalidated", StringComparison.OrdinalIgnoreCase)
                ? AuthVerificationStatus.RefreshTokenInvalidated
                : AuthVerificationStatus.Failed;
            return new AuthVerificationResult(status, null, message);
        }
    }

    public async Task<UsageRecord> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var binary = CodexBinaryLocator.Find()
            ?? throw new InvalidOperationException("No se encontro el binario de Codex.");
        await using var session = await AppServerSession.StartAsync(binary, _paths.CodexHome, cancellationToken);
        var response = await session.RequestAsync("account/rateLimits/read", new JsonObject(), cancellationToken);
        if (response["error"] is not null)
        {
            throw new InvalidOperationException("Codex app-server no pudo consultar los limites.");
        }

        return NormalizeRateLimits(response["result"]);
    }

    internal static UsageRecord NormalizeRateLimits(JsonNode? result)
    {
        var windows = new List<UsageWindow>();
        var preferred = result?["rateLimitsByLimitId"]?["codex"] ?? result?["rateLimits"];
        AddWindow(preferred?["primary"], windows);
        AddWindow(preferred?["secondary"], windows);

        if (windows.Count == 0 && result?["rateLimitsByLimitId"] is JsonObject buckets)
        {
            foreach (var bucket in buckets)
            {
                AddWindow(bucket.Value?["primary"], windows);
                AddWindow(bucket.Value?["secondary"], windows);
            }
        }

        var classified = UsageWindowClassifier.Classify(windows);

        return new UsageRecord
        {
            CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
            Status = "ok",
            Session = classified.Session,
            Weekly = classified.Weekly,
        };
    }

    private static void AddWindow(JsonNode? node, ICollection<UsageWindow> destination)
    {
        if (node is null)
        {
            return;
        }
        destination.Add(new UsageWindow(
            node["usedPercent"]?.GetValue<double?>(),
            node["windowDurationMins"]?.GetValue<int?>(),
            node["resetsAt"]?.GetValue<long?>()));
    }

    private sealed class AppServerSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private int _nextId = 1;

        private AppServerSession(Process process)
        {
            _process = process;
            _input = process.StandardInput;
            _output = process.StandardOutput;
        }

        public static async Task<AppServerSession> StartAsync(
            string binary,
            string codexHome,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = binary,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("stdio://");
            startInfo.Environment["CODEX_HOME"] = codexHome;

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("No se pudo iniciar Codex app-server.");
            var session = new AppServerSession(process);
            try
            {
                var initialize = await session.RequestAsync(
                    "initialize",
                    new JsonObject
                    {
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "codex_manager",
                            ["title"] = "CodexManager",
                            ["version"] = "2.0.0",
                        },
                    },
                    cancellationToken);
                if (initialize["error"] is not null)
                {
                    throw new InvalidOperationException("Codex app-server rechazo la inicializacion.");
                }
                await session.SendAsync(new JsonObject
                {
                    ["method"] = "initialized",
                    ["params"] = new JsonObject(),
                }, cancellationToken);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonObject> RequestAsync(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            await SendAsync(new JsonObject
            {
                ["method"] = method,
                ["id"] = id,
                ["params"] = parameters,
            }, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var line = await _output.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    var stderr = await ReadRedactedErrorAsync();
                    throw new IOException($"Codex app-server termino antes de responder. {stderr}".Trim());
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message?["id"]?.GetValue<int?>() == id)
                {
                    return message;
                }
            }
        }

        private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
        {
            await _input.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }

        private async Task<string> ReadRedactedErrorAsync()
        {
            try
            {
                var text = await _process.StandardError.ReadToEndAsync();
                return text.Contains("refresh_token_invalidated", StringComparison.OrdinalIgnoreCase)
                    ? "refresh_token_invalidated"
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _input.Close();
            }
            catch
            {
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            }
            _process.Dispose();
        }
    }
}
