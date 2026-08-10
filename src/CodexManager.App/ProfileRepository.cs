using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexManager;

internal sealed record ProfileAuthMatch(string Name, ProfileMeta Meta, AuthSnapshot Snapshot);

internal sealed class ProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly Action<string, Exception?> _log;

    public ProfileRepository(AppPaths paths, Action<string, Exception?> log)
    {
        _paths = paths;
        _log = log;
    }

    public IReadOnlyList<ProfileMeta> ListProfiles()
    {
        Directory.CreateDirectory(_paths.AccountsDir);
        return Directory.GetDirectories(_paths.AccountsDir)
            .Select(directory => LoadMeta(Path.GetFileName(directory)!))
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ProfileAuthMatch?> FindByIdentityAsync(
        AuthSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<ProfileAuthMatch>();
        foreach (var profile in ListProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authPath = GetProfileAuthPath(profile.Name);
            if (!File.Exists(authPath))
            {
                continue;
            }

            try
            {
                var stored = await StableAuthFile.ReadAsync(authPath, cancellationToken: cancellationToken);
                if (snapshot.Identity.Matches(stored.Identity))
                {
                    matches.Add(new ProfileAuthMatch(profile.Name, profile, stored));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                _log($"Perfil '{profile.Name}' omitido: auth.json no es valido.", ex);
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var configured = LoadAppConfig().ActiveProfile;
        var configuredMatch = matches.FirstOrDefault(match =>
            string.Equals(match.Name, configured, StringComparison.OrdinalIgnoreCase));
        return configuredMatch
            ?? matches.OrderByDescending(match => match.Snapshot.GenerationTime).First();
    }

    public async Task<AuthSnapshot> LoadProfileSnapshotAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        profileName = SanitizeProfileName(profileName);
        var path = GetProfileAuthPath(profileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"El perfil '{profileName}' no contiene auth.json.");
        }
        return await StableAuthFile.ReadAsync(path, cancellationToken: cancellationToken);
    }

    public async Task<bool> SaveIfNewerAsync(
        string profileName,
        AuthSnapshot incoming,
        CancellationToken cancellationToken = default)
    {
        profileName = SanitizeProfileName(profileName);
        var path = GetProfileAuthPath(profileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var stored = await StableAuthFile.ReadAsync(path, cancellationToken: cancellationToken);
            if (!incoming.Identity.Matches(stored.Identity))
            {
                throw new InvalidOperationException(
                    $"La identidad activa no coincide con el perfil '{profileName}'. No se ha sobrescrito.");
            }
            if (string.Equals(incoming.CredentialFingerprint, stored.CredentialFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            var comparison = incoming.CompareGenerationTo(stored);
            if (comparison < 0)
            {
                _log(
                    $"Se rechazo una generacion anterior para '{profileName}' ({AuthHash.Short(incoming.ContentFingerprint)}).",
                    null);
                return false;
            }
            if (comparison == 0)
            {
                if (!incoming.GenerationTime.HasValue && !stored.GenerationTime.HasValue)
                {
                    _log(
                        $"Se rechazo una generacion sin marca temporal para '{profileName}' ({AuthHash.Short(incoming.ContentFingerprint)}).",
                        null);
                    return false;
                }

                // The live auth.json is authoritative when its generation time
                // is equal: OAuth rotations can occur within the same timestamp
                // precision. Only a strictly older generation is a downgrade.
            }
        }

        AtomicFile.WriteBytes(path, incoming.Bytes);
        var verified = await StableAuthFile.ReadAsync(path, cancellationToken: cancellationToken);
        if (!string.Equals(verified.ContentFingerprint, incoming.ContentFingerprint, StringComparison.Ordinal)
            || !verified.Identity.Matches(incoming.Identity))
        {
            throw new IOException($"No se pudo verificar el auth.json guardado para '{profileName}'.");
        }

        UpdateMetaFromSnapshot(profileName, incoming, preserveCreatedAt: true);
        return true;
    }

    public void AddOrUpdateFromSnapshot(string profileName, AuthSnapshot snapshot)
    {
        profileName = SanitizeProfileName(profileName);
        var path = GetProfileAuthPath(profileName);
        if (File.Exists(path))
        {
            var stored = AuthSnapshot.Parse(File.ReadAllBytes(path), path);
            if (!snapshot.Identity.Matches(stored.Identity))
            {
                throw new InvalidOperationException(
                    $"El nombre '{profileName}' ya pertenece a otra identidad. No se ha sobrescrito.");
            }
            if (snapshot.CompareGenerationTo(stored) < 0)
            {
                throw new InvalidOperationException(
                    $"El perfil '{profileName}' contiene una generacion mas reciente.");
            }
            if (string.Equals(snapshot.CredentialFingerprint, stored.CredentialFingerprint, StringComparison.Ordinal))
            {
                SaveAppConfig(LoadAppConfig() with
                {
                    ActiveProfile = profileName,
                    LastUpdatedAt = UtcNow(),
                });
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteBytes(path, snapshot.Bytes);
        UpdateMetaFromSnapshot(profileName, snapshot, preserveCreatedAt: true);
        SaveAppConfig(LoadAppConfig() with
        {
            ActiveProfile = profileName,
            LastUpdatedAt = UtcNow(),
        });
    }

    public void Rename(string currentName, string newName)
    {
        currentName = SanitizeProfileName(currentName);
        newName = SanitizeProfileName(newName);
        var currentDirectory = GetProfileDir(currentName);
        var newDirectory = GetProfileDir(newName);
        if (!Directory.Exists(currentDirectory))
        {
            throw new InvalidOperationException("El perfil no existe.");
        }
        if (Directory.Exists(newDirectory))
        {
            throw new InvalidOperationException("Ya existe un perfil con ese nombre.");
        }

        Directory.Move(currentDirectory, newDirectory);
        SaveMeta(newName, LoadMeta(newName) with { Name = newName, UpdatedAt = UtcNow() });
        var appConfig = LoadAppConfig();
        if (string.Equals(appConfig.ActiveProfile, currentName, StringComparison.OrdinalIgnoreCase))
        {
            SaveAppConfig(appConfig with { ActiveProfile = newName, LastUpdatedAt = UtcNow() });
        }

        var cache = LoadUsageCache();
        if (cache.Accounts.Remove(currentName, out var usage))
        {
            cache.Accounts[newName] = usage;
            SaveUsageCache(cache);
        }
    }

    public void Delete(string profileName)
    {
        profileName = SanitizeProfileName(profileName);
        var profileDirectory = GetProfileDir(profileName);
        if (!Directory.Exists(profileDirectory))
        {
            return;
        }

        BackupIfChanged(GetProfileAuthPath(profileName), $"deleted-{profileName}");
        Directory.Delete(profileDirectory, recursive: true);
        var cache = LoadUsageCache();
        cache.Accounts.Remove(profileName);
        SaveUsageCache(cache);

        var config = LoadAppConfig();
        if (string.Equals(config.ActiveProfile, profileName, StringComparison.OrdinalIgnoreCase))
        {
            SaveAppConfig(config with { ActiveProfile = null, LastUpdatedAt = UtcNow() });
        }
    }

    public ProfileMeta LoadMeta(string profileName)
    {
        profileName = SanitizeProfileName(profileName);
        var metaPath = Path.Combine(GetProfileDir(profileName), "meta.json");
        if (!File.Exists(metaPath))
        {
            return new ProfileMeta(profileName, null, null, _paths.CodexHome, null);
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllBytes(metaPath))?.AsObject();
            return new ProfileMeta(
                Name: NormalizeProfileNameOrNull(ReadString(node, "Name", "profile_name", "name")) ?? profileName,
                CreatedAt: ReadString(node, "CreatedAt", "created_at"),
                UpdatedAt: ReadString(node, "UpdatedAt", "updated_at"),
                SourceCodexHome: ReadString(node, "SourceCodexHome", "source_codex_home") ?? _paths.CodexHome,
                Email: ReadString(node, "Email", "optional_display_email", "email"),
                IdentityFingerprint: ReadString(node, "IdentityFingerprint", "identity_fingerprint"),
                AuthFingerprint: ReadString(node, "AuthFingerprint", "auth_fingerprint"),
                LastRefresh: ReadString(node, "LastRefresh", "last_refresh"));
        }
        catch
        {
            return new ProfileMeta(profileName, null, null, _paths.CodexHome, null);
        }
    }

    public AppConfig LoadAppConfig()
    {
        if (!File.Exists(_paths.AppConfigPath))
        {
            return new AppConfig(null, null);
        }
        try
        {
            var node = JsonNode.Parse(File.ReadAllBytes(_paths.AppConfigPath))?.AsObject();
            return new AppConfig(
                NormalizeProfileNameOrNull(ReadString(node, "ActiveProfile", "active_profile", "activeProfile")),
                ReadString(node, "LastUpdatedAt", "last_updated_at", "lastUpdatedAt"),
                ReadBool(node, "StrictVerification", "strict_verification") ?? false);
        }
        catch
        {
            return new AppConfig(null, null);
        }
    }

    public void SaveAppConfig(AppConfig config) =>
        AtomicFile.WriteText(_paths.AppConfigPath, JsonSerializer.Serialize(config, JsonOptions));

    public UsageCacheFile LoadUsageCache()
    {
        if (!File.Exists(_paths.UsageCachePath))
        {
            return EmptyUsageCache();
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllBytes(_paths.UsageCachePath))?.AsObject();
            var accountsNode = node?["Accounts"]?.AsObject() ?? node?["accounts"]?.AsObject();
            var accounts = new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);
            if (accountsNode is null)
            {
                return new UsageCacheFile(accounts);
            }

            foreach (var item in accountsNode)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Value is not JsonObject usage)
                {
                    continue;
                }
                accounts[item.Key] = UsageWindowClassifier.NormalizeCached(new UsageRecord
                {
                    CheckedAt = ReadString(usage, "CheckedAt", "checked_at"),
                    Status = ReadString(usage, "Status", "status") ?? "never_checked",
                    Error = ReadString(usage, "Error", "error"),
                    Email = ReadString(usage, "Email", "email")
                        ?? usage["account"]?["email"]?.GetValue<string?>(),
                    Session = ReadUsageWindow(usage["Session"] ?? usage["session"] ?? usage["rateLimits"]?["primary"]),
                    Weekly = ReadUsageWindow(usage["Weekly"] ?? usage["weekly"] ?? usage["rateLimits"]?["secondary"]),
                });
            }
            return new UsageCacheFile(accounts);
        }
        catch
        {
            return EmptyUsageCache();
        }
    }

    public void SaveUsage(string profileName, UsageRecord usage)
    {
        var cache = LoadUsageCache();
        cache.Accounts[SanitizeProfileName(profileName)] = usage;
        SaveUsageCache(cache);
    }

    public void SaveUsageCache(UsageCacheFile cache) =>
        AtomicFile.WriteText(_paths.UsageCachePath, JsonSerializer.Serialize(cache, JsonOptions));

    public void WriteJournal(CredentialTransaction transaction) =>
        AtomicFile.WriteText(_paths.TransactionJournalPath, JsonSerializer.Serialize(transaction, JsonOptions));

    public CredentialTransaction? LoadJournal()
    {
        if (!File.Exists(_paths.TransactionJournalPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<CredentialTransaction>(File.ReadAllText(_paths.TransactionJournalPath));
        }
        catch (Exception ex)
        {
            _log("El diario transaccional no se pudo leer; se conserva para inspeccion.", ex);
            return null;
        }
    }

    public void ClearJournal()
    {
        if (File.Exists(_paths.TransactionJournalPath))
        {
            File.Delete(_paths.TransactionJournalPath);
        }
    }

    public async Task<bool> RecoverJournalAsync(CancellationToken cancellationToken = default)
    {
        var journal = LoadJournal();
        if (journal is null || !File.Exists(_paths.ActiveAuthPath))
        {
            return journal is null;
        }

        try
        {
            var active = await StableAuthFile.ReadAsync(_paths.ActiveAuthPath, cancellationToken: cancellationToken);
            if (string.Equals(active.Identity.Fingerprint, journal.TargetIdentityFingerprint, StringComparison.Ordinal)
                && (string.Equals(active.ContentFingerprint, journal.TargetAuthFingerprint, StringComparison.Ordinal)
                    || string.Equals(journal.Stage, "installed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(journal.Stage, "verified", StringComparison.OrdinalIgnoreCase)))
            {
                var config = LoadAppConfig();
                SaveAppConfig(config with
                {
                    ActiveProfile = journal.TargetProfile,
                    LastUpdatedAt = UtcNow(),
                });
                ClearJournal();
                _log($"Transaccion '{journal.Id}' recuperada en el perfil de destino.", null);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(journal.SourceProfile)
                && !string.IsNullOrWhiteSpace(journal.SourceAuthFingerprint)
                && string.Equals(active.ContentFingerprint, journal.SourceAuthFingerprint, StringComparison.Ordinal))
            {
                var config = LoadAppConfig();
                SaveAppConfig(config with
                {
                    ActiveProfile = journal.SourceProfile,
                    LastUpdatedAt = UtcNow(),
                });
                ClearJournal();
                _log($"Transaccion '{journal.Id}' recuperada en el perfil de origen.", null);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _log("No se pudo reconciliar el diario transaccional; no se modificaron credenciales.", ex);
        }
        return false;
    }

    public string GetProfileAuthPath(string profileName) =>
        Path.Combine(GetProfileDir(profileName), "auth.json");

    public string SanitizeProfileName(string name)
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

    private void UpdateMetaFromSnapshot(string profileName, AuthSnapshot snapshot, bool preserveCreatedAt)
    {
        var current = LoadMeta(profileName);
        var createdAt = preserveCreatedAt ? current.CreatedAt ?? UtcNow() : UtcNow();
        SaveMeta(profileName, current with
        {
            Name = profileName,
            CreatedAt = createdAt,
            UpdatedAt = UtcNow(),
            SourceCodexHome = _paths.CodexHome,
            Email = snapshot.Identity.Email ?? current.Email,
            IdentityFingerprint = snapshot.Identity.Fingerprint,
            AuthFingerprint = snapshot.ContentFingerprint,
            LastRefresh = snapshot.LastRefresh?.ToString("O"),
        });
    }

    private void SaveMeta(string profileName, ProfileMeta meta)
    {
        var metaPath = Path.Combine(GetProfileDir(profileName), "meta.json");
        AtomicFile.WriteText(metaPath, JsonSerializer.Serialize(meta, JsonOptions));
    }

    private void BackupIfChanged(string filePath, string prefix)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var content = File.ReadAllBytes(filePath);
        var fingerprint = AuthHash.Short(AuthHash.Bytes(content));
        var safePrefix = Regex.Replace(prefix, @"[^A-Za-z0-9._-]", "_");
        if (Directory.GetFiles(_paths.BackupsDir, $"{safePrefix}-*-{fingerprint}.bak").Length > 0)
        {
            return;
        }

        var backupPath = Path.Combine(
            _paths.BackupsDir,
            $"{safePrefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{fingerprint}.bak");
        AtomicFile.WriteBytes(backupPath, content);
    }

    private string GetProfileDir(string profileName) =>
        Path.Combine(_paths.AccountsDir, SanitizeProfileName(profileName));

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

    private static string? ReadString(JsonObject? node, params string[] properties)
    {
        foreach (var property in properties)
        {
            try
            {
                if (node?[property]?.GetValue<string?>()?.Trim() is { Length: > 0 } value)
                {
                    return value;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static bool? ReadBool(JsonObject? node, params string[] properties)
    {
        foreach (var property in properties)
        {
            try
            {
                if (node?[property]?.GetValue<bool?>() is bool value)
                {
                    return value;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static UsageWindow ReadUsageWindow(JsonNode? node) => new(
        node?["UsedPercent"]?.GetValue<double?>()
            ?? node?["usedPercent"]?.GetValue<double?>()
            ?? node?["used_percent"]?.GetValue<double?>(),
        node?["WindowDurationMins"]?.GetValue<int?>()
            ?? node?["windowDurationMins"]?.GetValue<int?>()
            ?? (node?["limit_window_seconds"]?.GetValue<int?>() is int seconds ? seconds / 60 : null),
        node?["ResetsAt"]?.GetValue<long?>()
            ?? node?["resetsAt"]?.GetValue<long?>()
            ?? node?["reset_at"]?.GetValue<long?>());

    private static UsageCacheFile EmptyUsageCache() =>
        new(new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase));

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");
}
