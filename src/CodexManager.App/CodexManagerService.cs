using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexManager;

internal sealed class CodexManagerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public event EventHandler? StateChanged;

    public CodexManagerService()
    {
        _paths = AppPaths.Build();
        _paths.EnsureDirectories();
        Log("CodexManager iniciado.");
    }

    public DashboardState BuildDashboardState()
    {
        var profiles = ListProfiles();
        var activeProfile = DetectActiveProfile();
        var activeUsage = activeProfile is null ? UsageRecord.Empty() : GetUsage(activeProfile.Name);
        var activeView = BuildProfileState(activeProfile, activeUsage, true);
        var profileViews = profiles
            .Select(profile => BuildProfileState(profile, GetUsage(profile.Name), profile.Name.Equals(activeProfile?.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var codexStatus = DetectCodexBinary();

        return new DashboardState(
            ActiveProfile: activeView,
            Profiles: profileViews,
            SettingsSummary: $"Codex: {codexStatus.Message}",
            DataFolder: _paths.AppHome,
            LogsFolder: _paths.LogsDir);
    }

    public string BuildTrayTooltip(DashboardState state)
    {
        var active = state.ActiveProfile;
        var session = active.SessionRemainingPercent.HasValue ? $"{active.SessionRemainingPercent.Value:0}% restante" : "sin datos";
        var text = $"CodexManager - {active.Name} - {session}";
        return text.Length <= 63 ? text : text[..63];
    }

    public async Task RefreshActiveProfileAsync()
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var active = DetectActiveProfile();
            if (active is null)
            {
                Log("Refresh omitido: no hay perfil activo.");
                return;
            }

            if (!File.Exists(_paths.ActiveAuthPath))
            {
                SaveUsage(active.Name, UsageRecord.FromError("No existe auth.json activo."));
                NotifyStateChanged();
                return;
            }

            var snapshot = await ReadUsageSnapshotAsync();
            SaveUsage(active.Name, snapshot);
            if (!string.IsNullOrWhiteSpace(snapshot.Email))
            {
                UpdateProfileMeta(active.Name, meta => meta with { Email = snapshot.Email, UpdatedAt = UtcNow() });
            }
            SaveActiveBackToProfile();
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            var active = DetectActiveProfile();
            if (active is not null)
            {
                SaveUsage(active.Name, UsageRecord.FromError(ex.Message));
            }
            Log("Error refrescando cuenta activa.", ex);
            NotifyStateChanged();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void AddCurrentProfile(string profileName)
    {
        profileName = SanitizeProfileName(profileName);
        if (!File.Exists(_paths.ActiveAuthPath))
        {
            throw new InvalidOperationException("No existe auth.json activo en CODEX_HOME.");
        }

        var profileDir = GetProfileDir(profileName);
        Directory.CreateDirectory(profileDir);
        var authTarget = Path.Combine(profileDir, "auth.json");
        BackupIfExists(authTarget, $"profile-{profileName}");
        File.Copy(_paths.ActiveAuthPath, authTarget, true);

        var meta = new ProfileMeta(profileName, UtcNow(), UtcNow(), _paths.CodexHome, null);
        SaveMeta(profileName, meta);
        SaveAppConfig(new AppConfig(profileName, UtcNow()));
        NotifyStateChanged();
    }

    public void RenameProfile(string currentName, string newName)
    {
        currentName = SanitizeProfileName(currentName);
        newName = SanitizeProfileName(newName);
        var currentDir = GetProfileDir(currentName);
        var newDir = GetProfileDir(newName);
        if (!Directory.Exists(currentDir))
        {
            throw new InvalidOperationException("El perfil no existe.");
        }
        if (Directory.Exists(newDir))
        {
            throw new InvalidOperationException("Ya existe un perfil con ese nombre.");
        }
        Directory.Move(currentDir, newDir);
        SaveMeta(newName, LoadMeta(newName) with { Name = newName, UpdatedAt = UtcNow() });

        var appConfig = LoadAppConfig();
        if (string.Equals(appConfig.ActiveProfile, currentName, StringComparison.OrdinalIgnoreCase))
        {
            SaveAppConfig(appConfig with { ActiveProfile = newName });
        }

        var cache = LoadUsageCache();
        if (cache.Accounts.Remove(currentName, out var usage))
        {
            cache.Accounts[newName] = usage;
            SaveUsageCache(cache);
        }
        NotifyStateChanged();
    }

    public void DeleteProfile(string profileName)
    {
        profileName = SanitizeProfileName(profileName);
        var profileDir = GetProfileDir(profileName);
        if (!Directory.Exists(profileDir))
        {
            return;
        }
        BackupIfExists(Path.Combine(profileDir, "auth.json"), $"deleted-{profileName}");
        Directory.Delete(profileDir, true);

        var cache = LoadUsageCache();
        cache.Accounts.Remove(profileName);
        SaveUsageCache(cache);

        var config = LoadAppConfig();
        if (string.Equals(config.ActiveProfile, profileName, StringComparison.OrdinalIgnoreCase))
        {
            SaveAppConfig(config with { ActiveProfile = null });
        }
        NotifyStateChanged();
    }

    public void SwitchToProfile(string profileName)
    {
        profileName = SanitizeProfileName(profileName);
        var source = Path.Combine(GetProfileDir(profileName), "auth.json");
        if (!File.Exists(source))
        {
            throw new InvalidOperationException("El perfil no tiene auth.json.");
        }
        BackupIfExists(_paths.ActiveAuthPath, "active-auth");
        Directory.CreateDirectory(_paths.CodexHome);
        File.Copy(source, _paths.ActiveAuthPath, true);
        SaveAppConfig(new AppConfig(profileName, UtcNow()));
        NotifyStateChanged();
    }

    public void SaveActiveBackToProfile()
    {
        var config = LoadAppConfig();
        if (string.IsNullOrWhiteSpace(config.ActiveProfile))
        {
            return;
        }
        if (!File.Exists(_paths.ActiveAuthPath))
        {
            throw new InvalidOperationException("No existe auth.json activo.");
        }

        var target = Path.Combine(GetProfileDir(config.ActiveProfile), "auth.json");
        Directory.CreateDirectory(GetProfileDir(config.ActiveProfile));
        BackupIfExists(target, $"profile-{config.ActiveProfile}");
        File.Copy(_paths.ActiveAuthPath, target, true);
        UpdateProfileMeta(config.ActiveProfile, meta => meta with { UpdatedAt = UtcNow() });
    }

    public void EnsureFileModeConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.CodexConfigPath)!);
        if (!File.Exists(_paths.CodexConfigPath))
        {
            File.WriteAllText(_paths.CodexConfigPath, "cli_auth_credentials_store = \"file\"" + Environment.NewLine);
            NotifyStateChanged();
            return;
        }

        BackupIfExists(_paths.CodexConfigPath, "config");
        var content = File.ReadAllText(_paths.CodexConfigPath);
        if (Regex.IsMatch(content, @"^\s*cli_auth_credentials_store\s*=", RegexOptions.Multiline))
        {
            content = Regex.Replace(
                content,
                @"^\s*cli_auth_credentials_store\s*=.*$",
                "cli_auth_credentials_store = \"file\"",
                RegexOptions.Multiline);
        }
        else
        {
            content = content.TrimEnd() + Environment.NewLine + "cli_auth_credentials_store = \"file\"" + Environment.NewLine;
        }
        File.WriteAllText(_paths.CodexConfigPath, content);
        NotifyStateChanged();
    }

    public bool StartupShortcutExists() => File.Exists(_paths.StartupShortcutPath);

    public void SetStartupEnabled(bool enabled)
    {
        if (enabled)
        {
            CreateStartupShortcut();
        }
        else if (File.Exists(_paths.StartupShortcutPath))
        {
            File.Delete(_paths.StartupShortcutPath);
        }
        NotifyStateChanged();
    }

    public void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private void CreateStartupShortcut()
    {
        Directory.CreateDirectory(_paths.StartupFolder);
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo resolver el ejecutable.");
        var command = $"""
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut('{_paths.StartupShortcutPath.Replace("'", "''")}')
            $shortcut.TargetPath = '{exePath.Replace("'", "''")}'
            $shortcut.WorkingDirectory = '{AppContext.BaseDirectory.Replace("'", "''")}'
            $shortcut.IconLocation = '{Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico").Replace("'", "''")},0'
            $shortcut.Description = 'CodexManager'
            $shortcut.Save()
            """;
        var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        process?.WaitForExit();
    }

    private async Task<UsageRecord> ReadUsageSnapshotAsync()
    {
        var creds = LoadCodexCredentials();
        var baseUrl = ResolveChatGptBaseUrl();
        var url = $"{baseUrl}/wham/usage";

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        request.Headers.UserAgent.ParseAdd("CodexManager");
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(creds.AccountId))
        {
            request.Headers.Add("ChatGPT-Account-Id", creds.AccountId);
        }

        using var response = await http.SendAsync(request);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("La sesion de Codex no es valida. Vuelve a iniciar sesion en Codex.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Codex usage API devolvio {(int)response.StatusCode}.");
        }

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return NormalizeUsage(json, json);
    }

    private CodexBinaryStatus DetectCodexBinary()
    {
        foreach (var candidate in EnumerateCodexCandidates())
        {
            if (File.Exists(candidate))
            {
                return new CodexBinaryStatus(true, candidate, candidate);
            }
        }
        return new CodexBinaryStatus(false, null, "No se encontro codex. Define CODEX_BINARY si hace falta.");
    }

    private IEnumerable<string> EnumerateCodexCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            if (seen.Add(path))
            {
                candidates.Add(path);
            }
        }
        Add(Environment.GetEnvironmentVariable("CODEX_BINARY"));
        Add(FindInPath("codex.exe"));
        Add(FindInPath("codex"));

        foreach (var path in RunWhereCodex())
        {
            Add(path);
        }

        var localBinary = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin", "codex.exe");
        Add(localBinary);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = Path.Combine(programFiles, "WindowsApps");
        if (Directory.Exists(windowsApps))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(windowsApps, "OpenAI.Codex_*").OrderDescending())
                {
                    Add(Path.Combine(dir, "app", "resources", "codex.exe"));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return candidates;
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

    private static IEnumerable<string> RunWhereCodex()
    {
        var results = new List<string>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("where.exe", "codex")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return results;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                results.Add(line.Trim());
            }
        }
        catch
        {
        }
        return results;
    }

    private UsageRecord NormalizeUsage(JsonNode? accountNode, JsonNode? rateNode)
    {
        var email = accountNode?["email"]?.GetValue<string>();
        var rateLimit = rateNode?["rate_limit"] as JsonObject;
        var primary = NormalizeWhamWindow(rateLimit?["primary_window"]);
        var secondary = NormalizeWhamWindow(rateLimit?["secondary_window"]);

        return new UsageRecord
        {
            CheckedAt = UtcNow(),
            Status = "ok",
            Error = null,
            Email = email,
            Session = primary,
            Weekly = secondary,
        };
    }

    private static UsageWindow NormalizeWhamWindow(JsonNode? node)
    {
        return new UsageWindow(
            node?["used_percent"]?.GetValue<double?>(),
            node?["limit_window_seconds"]?.GetValue<int?>() is int seconds ? seconds / 60 : null,
            node?["reset_at"]?.GetValue<long?>());
    }

    private static UsageWindow ReadUsageWindow(JsonNode? node)
    {
        return new UsageWindow(
            node?["UsedPercent"]?.GetValue<double?>()
                ?? node?["usedPercent"]?.GetValue<double?>()
                ?? node?["used_percent"]?.GetValue<double?>(),
            node?["WindowDurationMins"]?.GetValue<int?>()
                ?? node?["windowDurationMins"]?.GetValue<int?>()
                ?? (node?["limit_window_seconds"]?.GetValue<int?>() is int seconds ? seconds / 60 : null),
            node?["ResetsAt"]?.GetValue<long?>()
                ?? node?["resetsAt"]?.GetValue<long?>()
                ?? node?["reset_at"]?.GetValue<long?>());
    }

    private CodexCredentials LoadCodexCredentials()
    {
        if (!File.Exists(_paths.ActiveAuthPath))
        {
            throw new InvalidOperationException("No existe auth.json en CODEX_HOME.");
        }

        var json = JsonNode.Parse(File.ReadAllText(_paths.ActiveAuthPath))?.AsObject()
            ?? throw new InvalidOperationException("auth.json no es JSON valido.");

        var apiKey = json["OPENAI_API_KEY"]?.GetValue<string?>()?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return new CodexCredentials(apiKey, null);
        }

        var tokens = json["tokens"]?.AsObject()
            ?? throw new InvalidOperationException("auth.json no contiene tokens.");
        var accessToken = tokens["access_token"]?.GetValue<string?>()?.Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("auth.json no contiene access_token.");
        }

        var accountId = tokens["account_id"]?.GetValue<string?>()?.Trim();
        return new CodexCredentials(accessToken, string.IsNullOrWhiteSpace(accountId) ? null : accountId);
    }

    private string ResolveChatGptBaseUrl()
    {
        var baseUrl = "https://chatgpt.com/backend-api";
        if (!File.Exists(_paths.CodexConfigPath))
        {
            return baseUrl;
        }

        try
        {
            var content = File.ReadAllText(_paths.CodexConfigPath);
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Split('#')[0].Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var parts = line.Split('=', 2);
                if (parts.Length != 2 || parts[0].Trim() != "chatgpt_base_url")
                {
                    continue;
                }
                var value = parts[1].Trim().Trim('"', '\'').Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }
                while (value.EndsWith("/"))
                {
                    value = value[..^1];
                }
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

    private ProfileState BuildProfileState(ProfileMeta? profile, UsageRecord usage, bool isActive)
    {
        var status = BuildStatus(usage);
        var name = profile?.Name ?? "Sin perfil activo";
        var session = usage.Session.UsedPercent;
        var weekly = usage.Weekly.UsedPercent;
        return new ProfileState(
            Name: name,
            IsActive: isActive,
            SessionUsedPercent: DisplayUsedPercent(usage.Session),
            SessionRemainingPercent: DisplayRemainingPercent(usage.Session),
            SessionResetLabel: FormatReset(usage.Session.ResetsAt),
            WeeklyUsedPercent: DisplayUsedPercent(usage.Weekly),
            WeeklyRemainingPercent: DisplayRemainingPercent(usage.Weekly),
            WeeklyResetLabel: FormatReset(usage.Weekly.ResetsAt),
            UpdatedLabel: FormatRelative(usage.CheckedAt),
            Status: status,
            Error: usage.Error,
            Email: profile?.Email);
    }

    private static string BuildStatus(UsageRecord usage)
    {
        if (string.Equals(usage.Status, "error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }
        if (string.IsNullOrWhiteSpace(usage.CheckedAt))
        {
            return "never_checked";
        }
        if (DateTimeOffset.TryParse(usage.CheckedAt, out var checkedAt))
        {
            if (DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromMinutes(15))
            {
                return "stale";
            }
        }
        return "ok";
    }

    private List<ProfileMeta> ListProfiles()
    {
        Directory.CreateDirectory(_paths.AccountsDir);
        return Directory.GetDirectories(_paths.AccountsDir)
            .Select(dir => LoadMeta(Path.GetFileName(dir)!))
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ProfileMeta? DetectActiveProfile()
    {
        var config = LoadAppConfig();
        if (!string.IsNullOrWhiteSpace(config.ActiveProfile) && File.Exists(Path.Combine(GetProfileDir(config.ActiveProfile), "auth.json")))
        {
            return LoadMeta(config.ActiveProfile);
        }

        if (!File.Exists(_paths.ActiveAuthPath))
        {
            return null;
        }

        var activeHash = ComputeSha(_paths.ActiveAuthPath);
        foreach (var profile in ListProfiles())
        {
            var authPath = Path.Combine(GetProfileDir(profile.Name), "auth.json");
            if (File.Exists(authPath) && string.Equals(ComputeSha(authPath), activeHash, StringComparison.OrdinalIgnoreCase))
            {
                SaveAppConfig(config with { ActiveProfile = profile.Name });
                return profile;
            }
        }
        return null;
    }

    private UsageRecord GetUsage(string profileName)
    {
        var cache = LoadUsageCache();
        return cache.Accounts.TryGetValue(profileName, out var usage) ? usage : UsageRecord.Empty();
    }

    private void SaveUsage(string profileName, UsageRecord usage)
    {
        var cache = LoadUsageCache();
        cache.Accounts[profileName] = usage;
        SaveUsageCache(cache);
    }

    private UsageCacheFile LoadUsageCache()
    {
        if (!File.Exists(_paths.UsageCachePath))
        {
            return new UsageCacheFile(new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase));
        }

        try
        {
            var json = File.ReadAllText(_paths.UsageCachePath);
            var node = JsonNode.Parse(json)?.AsObject();
            var accountsNode =
                node?["Accounts"]?.AsObject()
                ?? node?["accounts"]?.AsObject();
            var accounts = new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);

            if (accountsNode is not null)
            {
                foreach (var item in accountsNode)
                {
                    if (string.IsNullOrWhiteSpace(item.Key) || item.Value is not JsonObject usageObject)
                    {
                        continue;
                    }

                    accounts[item.Key] = new UsageRecord
                    {
                        CheckedAt =
                            usageObject["CheckedAt"]?.GetValue<string?>()
                            ?? usageObject["checked_at"]?.GetValue<string?>(),
                        Status =
                            usageObject["Status"]?.GetValue<string?>()
                            ?? usageObject["status"]?.GetValue<string?>()
                            ?? "never_checked",
                        Error =
                            usageObject["Error"]?.GetValue<string?>()
                            ?? usageObject["error"]?.GetValue<string?>(),
                        Email =
                            usageObject["Email"]?.GetValue<string?>()
                            ?? usageObject["email"]?.GetValue<string?>()
                            ?? usageObject["account"]?["email"]?.GetValue<string?>(),
                        Session = ReadUsageWindow(
                            usageObject["Session"]
                            ?? usageObject["session"]
                            ?? usageObject["rateLimits"]?["primary"]),
                        Weekly = ReadUsageWindow(
                            usageObject["Weekly"]
                            ?? usageObject["weekly"]
                            ?? usageObject["rateLimits"]?["secondary"]),
                    };
                }
            }

            return new UsageCacheFile(accounts);
        }
        catch
        {
            return new UsageCacheFile(new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void SaveUsageCache(UsageCacheFile cache)
    {
        File.WriteAllText(_paths.UsageCachePath, JsonSerializer.Serialize(cache, JsonOptions));
    }

    private AppConfig LoadAppConfig()
    {
        if (!File.Exists(_paths.AppConfigPath))
        {
            return new AppConfig(null, null);
        }
        try
        {
            var json = File.ReadAllText(_paths.AppConfigPath);
            var node = JsonNode.Parse(json)?.AsObject();
            var activeProfile =
                node?["ActiveProfile"]?.GetValue<string?>()
                ?? node?["active_profile"]?.GetValue<string?>()
                ?? node?["activeProfile"]?.GetValue<string?>();
            var lastUpdatedAt =
                node?["LastUpdatedAt"]?.GetValue<string?>()
                ?? node?["last_updated_at"]?.GetValue<string?>()
                ?? node?["lastUpdatedAt"]?.GetValue<string?>();
            return new AppConfig(NormalizeProfileNameOrNull(activeProfile), lastUpdatedAt);
        }
        catch
        {
            return new AppConfig(null, null);
        }
    }

    private void SaveAppConfig(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.AppConfigPath)!);
        File.WriteAllText(_paths.AppConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    private ProfileMeta LoadMeta(string profileName)
    {
        var metaPath = Path.Combine(GetProfileDir(profileName), "meta.json");
        if (!File.Exists(metaPath))
        {
            return new ProfileMeta(profileName, null, null, _paths.CodexHome, null);
        }
        try
        {
            var json = File.ReadAllText(metaPath);
            var node = JsonNode.Parse(json)?.AsObject();
            var name =
                NormalizeProfileNameOrNull(node?["Name"]?.GetValue<string?>())
                ?? NormalizeProfileNameOrNull(node?["profile_name"]?.GetValue<string?>())
                ?? NormalizeProfileNameOrNull(node?["name"]?.GetValue<string?>())
                ?? profileName;
            var createdAt =
                node?["CreatedAt"]?.GetValue<string?>()
                ?? node?["created_at"]?.GetValue<string?>();
            var updatedAt =
                node?["UpdatedAt"]?.GetValue<string?>()
                ?? node?["updated_at"]?.GetValue<string?>();
            var sourceCodexHome =
                node?["SourceCodexHome"]?.GetValue<string?>()
                ?? node?["source_codex_home"]?.GetValue<string?>()
                ?? _paths.CodexHome;
            var email =
                node?["Email"]?.GetValue<string?>()
                ?? node?["optional_display_email"]?.GetValue<string?>()
                ?? node?["email"]?.GetValue<string?>();
            return new ProfileMeta(name, createdAt, updatedAt, sourceCodexHome, email);
        }
        catch
        {
            return new ProfileMeta(profileName, null, null, _paths.CodexHome, null);
        }
    }

    private void SaveMeta(string profileName, ProfileMeta meta)
    {
        var metaPath = Path.Combine(GetProfileDir(profileName), "meta.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOptions));
    }

    private void UpdateProfileMeta(string profileName, Func<ProfileMeta, ProfileMeta> update)
    {
        SaveMeta(profileName, update(LoadMeta(profileName)));
    }

    private static string ComputeSha(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private void BackupIfExists(string filePath, string prefix)
    {
        if (!File.Exists(filePath))
        {
            return;
        }
        Directory.CreateDirectory(_paths.BackupsDir);
        var safePrefix = Regex.Replace(prefix, @"[^A-Za-z0-9._-]", "_");
        var backupPath = Path.Combine(_paths.BackupsDir, $"{safePrefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bak");
        File.Copy(filePath, backupPath, true);
    }

    private string SanitizeProfileName(string name)
    {
        var value = name.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("El nombre del perfil no puede estar vacio.");
        }
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9._ -]{1,64}$"))
        {
            throw new InvalidOperationException("El nombre del perfil contiene caracteres no permitidos.");
        }
        return value;
    }

    private string GetProfileDir(string profileName) => Path.Combine(_paths.AccountsDir, SanitizeProfileName(profileName));

    private string? NormalizeProfileNameOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        try
        {
            return SanitizeProfileName(value);
        }
        catch
        {
            return null;
        }
    }

    private string ReadConfigSummary()
    {
        if (!File.Exists(_paths.CodexConfigPath))
        {
            return "config.toml no existe";
        }
        var content = File.ReadAllText(_paths.CodexConfigPath);
        return content.Contains("cli_auth_credentials_store = \"file\"", StringComparison.Ordinal)
            ? "modo file"
            : "sin modo file";
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void Log(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDir);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (ex is not null)
            {
                line += $" {ex}";
            }
            File.AppendAllLines(_paths.LogFilePath, [line]);
        }
        catch
        {
        }
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");

    private static double? Clamp(double? value) => value.HasValue ? Math.Clamp(value.Value, 0d, 100d) : null;
    private static double? Remaining(double? value) => value.HasValue ? Math.Clamp(100d - value.Value, 0d, 100d) : null;
    private static double? DisplayUsedPercent(UsageWindow window) => HasResetElapsed(window.ResetsAt) ? 0d : Clamp(window.UsedPercent);
    private static double? DisplayRemainingPercent(UsageWindow window) => HasResetElapsed(window.ResetsAt) ? 100d : Remaining(window.UsedPercent);

    private static bool HasResetElapsed(long? unixSeconds)
    {
        if (!unixSeconds.HasValue)
        {
            return false;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) <= DateTimeOffset.Now;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatReset(long? unixSeconds)
    {
        if (!unixSeconds.HasValue)
        {
            return "Sin reset";
        }
        try
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).ToLocalTime();
            var delta = reset - DateTimeOffset.Now;
            if (delta <= TimeSpan.Zero)
            {
                return "Updated";
            }
            if (delta.TotalDays >= 1)
            {
                return reset.ToString("dd/MM");
            }
            return reset.ToString("HH:mm");
        }
        catch
        {
            return "Sin reset";
        }
    }

    private static string FormatRelative(string? checkedAt)
    {
        if (!DateTimeOffset.TryParse(checkedAt, out var time))
        {
            return "Sin datos recientes";
        }
        var delta = DateTimeOffset.UtcNow - time;
        if (delta.TotalMinutes < 1)
        {
            return "Updated just now";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"Updated {Math.Max(1, (int)delta.TotalMinutes)}m ago";
        }
        if (delta.TotalHours < 24)
        {
            return $"Updated {(int)delta.TotalHours}h ago";
        }
        return $"Updated {(int)delta.TotalDays}d ago";
    }
}

internal sealed record AppPaths(
    string CodexHome,
    string ActiveAuthPath,
    string CodexConfigPath,
    string AppHome,
    string AccountsDir,
    string BackupsDir,
    string UsageCachePath,
    string AppConfigPath,
    string LogsDir,
    string LogFilePath,
    string StartupFolder,
    string StartupShortcutPath)
{
    public static AppPaths Build()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appHome = Path.Combine(appData, "CodexAccountTrayManager");
        var startupFolder = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

        return new AppPaths(
            codexHome,
            Path.Combine(codexHome, "auth.json"),
            Path.Combine(codexHome, "config.toml"),
            appHome,
            Path.Combine(appHome, "accounts"),
            Path.Combine(appHome, "backups"),
            Path.Combine(appHome, "cache", "usage_cache.json"),
            Path.Combine(appHome, "config", "app_config.json"),
            Path.Combine(appHome, "logs"),
            Path.Combine(appHome, "logs", "app.log"),
            startupFolder,
            Path.Combine(startupFolder, "CodexManager.lnk"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(CodexHome);
        Directory.CreateDirectory(AppHome);
        Directory.CreateDirectory(AccountsDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(UsageCachePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(AppConfigPath)!);
        Directory.CreateDirectory(LogsDir);
    }
}

internal sealed record CodexBinaryStatus(bool Available, string? Path, string Message);
internal sealed record AppConfig(string? ActiveProfile, string? LastUpdatedAt);
internal sealed record ProfileMeta(string Name, string? CreatedAt, string? UpdatedAt, string SourceCodexHome, string? Email);
internal sealed record UsageWindow(double? UsedPercent, int? WindowDurationMins, long? ResetsAt);
internal sealed record UsageRecord
{
    public string? CheckedAt { get; init; }
    public string Status { get; init; } = "never_checked";
    public string? Error { get; init; }
    public string? Email { get; init; }
    public UsageWindow Session { get; init; } = new(null, null, null);
    public UsageWindow Weekly { get; init; } = new(null, null, null);

    public static UsageRecord Empty() => new();
    public static UsageRecord FromError(string message) => new() { CheckedAt = DateTimeOffset.UtcNow.ToString("O"), Status = "error", Error = message };
}

internal sealed record UsageCacheFile(Dictionary<string, UsageRecord> Accounts);
internal sealed record CodexCredentials(string AccessToken, string? AccountId);

internal sealed record ProfileState(
    string Name,
    bool IsActive,
    double? SessionUsedPercent,
    double? SessionRemainingPercent,
    string SessionResetLabel,
    double? WeeklyUsedPercent,
    double? WeeklyRemainingPercent,
    string WeeklyResetLabel,
    string UpdatedLabel,
    string Status,
    string? Error,
    string? Email);

internal sealed record DashboardState(
    ProfileState ActiveProfile,
    IReadOnlyList<ProfileState> Profiles,
    string SettingsSummary,
    string DataFolder,
    string LogsFolder);
