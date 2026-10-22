namespace AICountsManager;

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
    string TransactionsDir,
    string TransactionJournalPath,
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
        var appHome = Path.Combine(appData, "AICountsManager");
        var startupFolder = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        return Create(codexHome, appHome, startupFolder);
    }

    public static AppPaths CreateForTesting(string root)
    {
        var codexHome = Path.Combine(root, "codex-home");
        var appHome = Path.Combine(root, "app-data");
        return Create(codexHome, appHome, Path.Combine(root, "startup"));
    }

    private static AppPaths Create(string codexHome, string appHome, string startupFolder)
    {
        var transactionsDir = Path.Combine(appHome, "transactions");
        return new AppPaths(
            Path.GetFullPath(codexHome),
            Path.Combine(codexHome, "auth.json"),
            Path.Combine(codexHome, "config.toml"),
            Path.GetFullPath(appHome),
            Path.Combine(appHome, "accounts"),
            Path.Combine(appHome, "backups"),
            Path.Combine(appHome, "cache", "usage_cache.json"),
            Path.Combine(appHome, "config", "app_config.json"),
            Path.Combine(appHome, "logs"),
            Path.Combine(appHome, "logs", "app.log"),
            transactionsDir,
            Path.Combine(transactionsDir, "current.json"),
            startupFolder,
            Path.Combine(startupFolder, "AICountsManager.lnk"));
    }

    public void EnsureDirectories()
    {
        // CODEX_HOME belongs to Codex itself. Create it when necessary, but do
        // not rewrite the ACL of the whole tree. Atomic auth writes restrict the
        // individual temporary and destination files instead.
        Directory.CreateDirectory(CodexHome);

        foreach (var directory in new[]
        {
            AppHome,
            AccountsDir,
            BackupsDir,
            Path.GetDirectoryName(UsageCachePath)!,
            Path.GetDirectoryName(AppConfigPath)!,
            LogsDir,
            TransactionsDir,
        })
        {
            Directory.CreateDirectory(directory);
            AtomicFile.TryRestrictDirectory(directory);
        }
    }
}

internal sealed record CodexBinaryStatus(bool Available, string? Path, string Message);

internal sealed record AppConfig(
    string? ActiveProfile,
    string? LastUpdatedAt,
    bool StrictVerification = false,
    IReadOnlyList<string>? ProfileOrder = null, IReadOnlyList<string>? AccountOrder = null);

internal sealed record ProfileMeta(
    string Name,
    string? CreatedAt,
    string? UpdatedAt,
    string SourceCodexHome,
    string? Email,
    string? IdentityFingerprint = null,
    string? AuthFingerprint = null,
    string? LastRefresh = null);

internal sealed record UsageWindow(double? UsedPercent, int? WindowDurationMins, long? ResetsAt);

internal sealed record UsageRecord
{
    public int? HttpStatus { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public string? CheckedAt { get; init; }
    public string Status { get; init; } = "never_checked";
    public string? Error { get; init; }
    public string? Email { get; init; }
    public string? PlanType { get; init; }
    public bool SessionLimitNotApplicable { get; init; }
    public UsageWindow Session { get; init; } = new(null, null, null);
    public UsageWindow Weekly { get; init; } = new(null, null, null);

    public static UsageRecord Empty() => new();

    public static UsageRecord FromError(string message) => new()
    {
        CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
        Status = "error",
        Error = message,
    };
}

internal sealed record UsageCacheFile(Dictionary<string, UsageRecord> Accounts);

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
    string? Email,
    UsageWindow? SessionWindow = null,
    UsageWindow? WeeklyWindow = null,
    string? PlanType = null,
    bool SessionLimitNotApplicable = false);

internal sealed record DashboardState(
    ProfileState ActiveProfile,
    IReadOnlyList<ProfileState> Profiles,
    string SettingsSummary,
    string DataFolder,
    string LogsFolder,
    bool StrictVerification,
    bool CodexRunning,
    string? UpdatedLabel = null, IReadOnlyList<ProviderUsage>? OtherAccounts = null, IReadOnlyList<string>? AccountOrder = null)
{
    public IReadOnlyList<OrderedAccount> OrderedAccounts => AccountOrdering.Resolve(Profiles, OtherAccounts, AccountOrder);
}

internal enum SwitchProfileStatus
{
    Success,
    CodexStillRunning,
    RequiresReauthentication,
    RestartFailed,
    Failed,
}

internal sealed record SwitchProfileResult(
    SwitchProfileStatus Status,
    string Message,
    string? PreviousProfile,
    string TargetProfile,
    bool CodexWasRestarted = false);

internal sealed record CredentialTransaction(
    string Id,
    string Operation,
    string Stage,
    string? SourceProfile,
    string TargetProfile,
    string? SourceAuthFingerprint,
    string TargetAuthFingerprint,
    string TargetIdentityFingerprint,
    string StartedAt,
    string UpdatedAt);
