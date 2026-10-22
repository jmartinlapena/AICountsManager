using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexManager;

namespace CodexManager.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("refresh survives final storage read failure and retries", RefreshStorageFailureAsync),
        ("posted tray notification contains storage failures", TrayPostedFailureAsync),
        ("disposing flyout cancels pending refresh safely", RefreshDisposalAsync),
        ("diagnostics are bounded and exclude exception messages", BoundedDiagnosticsAsync),
        ("history day selection preserves chart and updates exact totals/models", HistoryDaySelectionAsync),
        ("Business Premium uses actual tier and migrates cached quota applicability", BusinessPremiumAsync),
        ("quiet refresh retains cooldown/footer/errors without hover annotations", QuietRefreshUiAsync),
        ("plan labels and weekly-only Pro normalization", PlanNormalizationAsync),
        ("missing or ambiguous quotas do not imply unlimited sessions", MissingSessionIsNotUnlimitedAsync),
        ("plans survive cache, errors and dashboard updates", PlanCacheAndDashboardAsync),
        ("app-server preserves plan and session applicability", AppServerPlanAsync),
        ("plan badges fit and reused cards collapse and restore session", CompactPlanCardAsync),
        ("custom account order survives CRUD and switching", AccountOrderPersistsAsync),
        ("settings arrows reorder visible accounts", UiAccountOrderAsync),
        ("tray creates flyout only on demand and reopens correctly", TrayLazyFlyoutAsync),
        ("background strict polling stays read-only and lightweight", StrictPollingStaysLightAsync),
        ("UI refresh, history, settings and reopen flows", UiRefreshAndHistoryAsync),
        ("history incremental, models, restart and archive", HistoryIncrementalAsync),
        ("history partial lines and truncation", HistoryPartialLineAsync),
        ("history excludes inherited fork and subagent counters", HistoryInheritedAsync),
        ("history respects byte budget and oversized records", HistoryBudgetAsync),
        ("history handles malformed cache and token values", HistoryMalformedAsync),
        ("history local dates and calendar weeks", HistoryCalendarAsync),
        ("quota pace, daily budget and countdown", PaceAndCountdownAsync),
        ("bulk refresh bounded, read-only and isolated failures", BulkRefreshAsync),
        ("bulk refresh obeys rate limits", BulkRateLimitAsync),
        ("bulk cancellation and overlapping refresh", BulkCancellationAsync),
        ("history incremental performance", HistoryBenchmarkAsync),
        ("identity and generation", IdentityAndGenerationAsync),
        ("repository rejects credential downgrade", RepositoryRejectsDowngradeAsync),
        ("equal timestamp rotation is captured", EqualTimestampRotationIsCapturedAsync),
        ("watcher captures a refreshed generation", WatcherCapturesRefreshAsync),
        ("closed switch preserves outgoing credentials", ClosedSwitchPreservesOutgoingAsync),
        ("open switch closes and restarts Codex", OpenSwitchClosesAndRestartsAsync),
        ("strict invalidation restores previous account", StrictInvalidationRestoresPreviousAsync),
        ("strict refresh is captured immediately", StrictRefreshIsCapturedAsync),
        ("unknown live account is never closed or overwritten", UnknownLiveAccountIsProtectedAsync),
        ("usage refresh is credential read-only", UsageRefreshIsCredentialReadOnlyAsync),
        ("usage windows are classified by duration", UsageWindowsAreClassifiedByDurationAsync),
        ("transaction journal reconciles installed target", JournalRecoveryAsync),
        ("profile names cannot escape or alias directories", ProfileNamesAreSafeAsync),
        ("metadata cannot select a different profile", MetadataNameIsNotAuthoritativeAsync),
        ("failed verification restores missing auth", MissingAuthRollbackAsync),
        ("verification exception restores missing auth", MissingAuthExceptionRollbackAsync),
        ("profile actions complete on WinForms UI thread", UiProfileActionsAsync),
        ("expired cached window is unknown instead of full", ExpiredUsageIsUnknownAsync),
        ("usage errors preserve last successful observation", UsageErrorsPreserveObservationAsync),
        ("file mode is written outside TOML tables", FileModeIsRootSettingAsync),
        ("cards are reused and removed controls disposed", UiCardLifetimeAsync),
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--probe-refresh-storage-failure"]) { await RefreshStorageFailureAsync(); return 0; }
        if (args is ["--probe-tray-refresh-live-quota"]) { await ProbeTrayRefreshAsync(true, true); return 0; }
        if (args is ["--probe-tray-refresh-live-history"]) { await ProbeTrayRefreshAsync(true); return 0; }
        if (args is ["--probe-tray-refresh"]) { await ProbeTrayRefreshAsync(); return 0; }
        if (args is ["--measure-ui", var resourceOutput]) { await MeasureUiResourcesAsync(resourceOutput); return 0; }
        if (args is ["--benchmark-history", var home, var cache])
        {
            var scanner = new TokenHistory(home, cache);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await scanner.RefreshAsync();
            Console.WriteLine($"LOCAL HISTORY elapsed={watch.ElapsedMilliseconds}ms bytes={scanner.LastBytesRead} days={result.Days.Count} partial={result.Partial} catchingUp={result.CatchingUp} error={result.Error}");
            watch.Restart(); result = await scanner.RefreshAsync();
            Console.WriteLine($"NEXT PASS elapsed={watch.ElapsedMilliseconds}ms bytes={scanner.LastBytesRead} days={result.Days.Count} partial={result.Partial} catchingUp={result.CatchingUp} error={result.Error}");
            return 0;
        }
        if (args is ["--preview-ui"])
        {
            await PreviewUiAsync();
            return 0;
        }
        if (args is ["--render-ui", var output])
        {
            await RenderUiAsync(output);
            return 0;
        }
        if (args is ["--validate-live-readonly"])
        {
            return await ValidateLiveStoreReadOnlyAsync();
        }

        var failures = 0;
        foreach (var test in Tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} tests passed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> ValidateLiveStoreReadOnlyAsync()
    {
        var paths = AppPaths.Build();
        if (!File.Exists(paths.ActiveAuthPath))
        {
            Console.Error.WriteLine("INVALID active auth.json is missing");
            return 1;
        }

        var live = await StableAuthFile.ReadAsync(paths.ActiveAuthPath);
        var repository = Repository(paths);
        var profiles = repository.ListProfiles();
        var valid = 0;
        var invalid = 0;
        var identityMatches = 0;
        var newestMatchingComparison = int.MinValue;
        var configuredProfileMatchesLive = false;
        var configuredProfile = repository.LoadAppConfig().ActiveProfile;
        foreach (var profile in profiles)
        {
            try
            {
                var snapshot = await repository.LoadProfileSnapshotAsync(profile.Name);
                valid++;
                if (snapshot.Identity.Matches(live.Identity))
                {
                    identityMatches++;
                    newestMatchingComparison = Math.Max(newestMatchingComparison, snapshot.CompareGenerationTo(live));
                    configuredProfileMatchesLive |= string.Equals(
                        configuredProfile,
                        profile.Name,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                invalid++;
            }
        }

        Console.WriteLine(
            $"READONLY_VALIDATION live={AuthHash.Short(live.Identity.Fingerprint)} profiles={profiles.Count} valid={valid} invalid={invalid} identity_matches={identityMatches} configured_match={configuredProfileMatchesLive} matching_generation_comparison={newestMatchingComparison}");
        return live.Identity.IsUsable && profiles.Count > 0 && invalid == 0 && identityMatches > 0 ? 0 : 1;
    }

    private static Task IdentityAndGenerationAsync()
    {
        var oldA = Snapshot("user-a", "account-a", "workspace-a", "old", 1_700_000_000);
        var newA = Snapshot("user-a", "account-a", "workspace-a", "new", 1_700_000_100);
        var accountB = Snapshot("user-b", "account-b", "workspace-b", "only", 1_700_000_050);

        Check(oldA.Identity.Matches(newA.Identity), "The same account was not matched.");
        Check(!oldA.Identity.Matches(accountB.Identity), "Different accounts were matched.");
        Check(newA.CompareGenerationTo(oldA) > 0, "A newer last_refresh was not preferred.");
        Check(oldA.CompareGenerationTo(newA) < 0, "An older last_refresh was not rejected.");
        return Task.CompletedTask;
    }

    private static async Task RepositoryRejectsDowngradeAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            paths.EnsureDirectories();
            var repository = Repository(paths);
            var older = Snapshot("user-a", "account-a", "workspace-a", "old", 1_700_000_000);
            var newer = Snapshot("user-a", "account-a", "workspace-a", "new", 1_700_000_100);
            repository.AddOrUpdateFromSnapshot("AccountA", newer);

            var before = AuthHash.Bytes(await File.ReadAllBytesAsync(repository.GetProfileAuthPath("AccountA")));
            var changed = await repository.SaveIfNewerAsync("AccountA", older);
            var after = AuthHash.Bytes(await File.ReadAllBytesAsync(repository.GetProfileAuthPath("AccountA")));

            Check(!changed, "An older generation was reported as saved.");
            Check(before == after, "An older generation replaced the stored credentials.");
            Check(Directory.GetFiles(paths.BackupsDir).Length == 0, "A no-op created a backup.");
        });
    }

    private static async Task WatcherCapturesRefreshAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var older = Snapshot("user-a", "account-a", "workspace-a", "old", 1_700_000_000);
            var newer = Snapshot("user-a", "account-a", "workspace-a", "new", 1_700_000_100);
            SeedProfiles(paths, older, ("AccountA", older));

            using var service = CreateService(paths, startWatcher: true);
            AtomicFile.WriteBytes(paths.ActiveAuthPath, newer.Bytes);

            var storedPath = Repository(paths).GetProfileAuthPath("AccountA");
            await EventuallyAsync(async () =>
                AuthHash.Bytes(await File.ReadAllBytesAsync(storedPath)) == newer.ContentFingerprint);
            Check(Directory.GetFiles(paths.BackupsDir).Length == 0, "Watcher refresh created a backup.");
        });
    }

    private static async Task EqualTimestampRotationIsCapturedAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            paths.EnsureDirectories();
            var repository = Repository(paths);
            var first = Snapshot("user-a", "account-a", "workspace-a", "first", 1_700_000_000);
            var rotated = Snapshot("user-a", "account-a", "workspace-a", "rotated", 1_700_000_000);
            repository.AddOrUpdateFromSnapshot("AccountA", first);

            Check(await repository.SaveIfNewerAsync("AccountA", rotated),
                "A live rotation with the same timestamp was not captured.");
            Check((await repository.LoadProfileSnapshotAsync("AccountA")).CredentialFingerprint
                    == rotated.CredentialFingerprint,
                "The equal-timestamp rotation was not persisted.");
        });
    }

    private static async Task ClosedSwitchPreservesOutgoingAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var accountA = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
            var accountB = Snapshot("user-b", "account-b", "workspace-b", "b", 1_700_000_010);
            SeedProfiles(paths, accountA, ("AccountA", accountA), ("AccountB", accountB));
            var process = new FakeProcessController(running: false);

            using var service = CreateService(paths, process: process);
            var result = await service.SwitchToProfileAsync("AccountB");

            Check(result.Status == SwitchProfileStatus.Success, result.Message);
            Check((await Read(paths.ActiveAuthPath)).ContentFingerprint == accountB.ContentFingerprint,
                "The target account was not installed.");
            Check((await Read(Repository(paths).GetProfileAuthPath("AccountA"))).ContentFingerprint == accountA.ContentFingerprint,
                "The outgoing account was not preserved.");
            Check(Repository(paths).LoadAppConfig().ActiveProfile == "AccountB", "ActiveProfile changed too early or not at all.");
            Check(process.CloseCalls == 0 && process.RestartCalls == 0, "A closed switch touched Codex processes.");
        });
    }

    private static async Task OpenSwitchClosesAndRestartsAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var accountA = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
            var accountB = Snapshot("user-b", "account-b", "workspace-b", "b", 1_700_000_010);
            SeedProfiles(paths, accountA, ("AccountA", accountA), ("AccountB", accountB));
            var process = new FakeProcessController(running: true);

            using var service = CreateService(paths, process: process);
            var result = await service.SwitchToProfileAsync("AccountB");

            Check(result.Status == SwitchProfileStatus.Success, result.Message);
            Check(process.CloseCalls == 1, "Codex was not closed normally exactly once.");
            Check(process.RestartCalls == 1 && process.Running, "Codex was not restarted.");
            Check((await Read(paths.ActiveAuthPath)).Identity.Matches(accountB.Identity), "Restarted Codex has the wrong account.");
        });
    }

    private static async Task StrictInvalidationRestoresPreviousAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var accountA = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
            var accountB = Snapshot("user-b", "account-b", "workspace-b", "b", 1_700_000_010);
            SeedProfiles(paths, accountA, ("AccountA", accountA), ("AccountB", accountB));
            var repository = Repository(paths);
            repository.SaveAppConfig(repository.LoadAppConfig() with { StrictVerification = true });
            var verifier = new FakeAppServerClient(new AuthVerificationResult(
                AuthVerificationStatus.RefreshTokenInvalidated,
                null,
                "refresh_token_invalidated"));

            using var service = CreateService(paths, appServer: verifier);
            var result = await service.SwitchToProfileAsync("AccountB");

            Check(result.Status == SwitchProfileStatus.RequiresReauthentication, "Invalid refresh token was not surfaced.");
            Check((await Read(paths.ActiveAuthPath)).ContentFingerprint == accountA.ContentFingerprint,
                "The outgoing credentials were not restored.");
            Check(repository.LoadAppConfig().ActiveProfile == "AccountA", "ActiveProfile was not rolled back.");
            Check(!File.Exists(paths.TransactionJournalPath), "A completed rollback left a transaction journal.");
        });
    }

    private static async Task UnknownLiveAccountIsProtectedAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var accountA = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
            var accountB = Snapshot("user-b", "account-b", "workspace-b", "b", 1_700_000_010);
            var unknown = Snapshot("user-c", "account-c", "workspace-c", "c", 1_700_000_020);
            SeedProfiles(paths, unknown, ("AccountA", accountA), ("AccountB", accountB));
            var process = new FakeProcessController(running: true);

            using var service = CreateService(paths, process: process);
            var before = (await Read(paths.ActiveAuthPath)).ContentFingerprint;
            var result = await service.SwitchToProfileAsync("AccountB");

            Check(result.Status == SwitchProfileStatus.Failed, "An unknown live account was allowed to switch.");
            Check(process.CloseCalls == 0, "Codex was closed before the unknown account was protected.");
            Check((await Read(paths.ActiveAuthPath)).ContentFingerprint == before, "Unknown live credentials were overwritten.");
        });
    }

    private static async Task StrictRefreshIsCapturedAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var accountA = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
            var accountBOld = Snapshot("user-b", "account-b", "workspace-b", "b-old", 1_700_000_010);
            var accountBNew = Snapshot("user-b", "account-b", "workspace-b", "b-new", 1_700_000_020);
            SeedProfiles(paths, accountA, ("AccountA", accountA), ("AccountB", accountBOld));
            var repository = Repository(paths);
            repository.SaveAppConfig(repository.LoadAppConfig() with { StrictVerification = true });
            var verifier = new FakeAppServerClient(
                new AuthVerificationResult(AuthVerificationStatus.Success, "fake@example.test", null),
                onVerify: () => AtomicFile.WriteBytes(paths.ActiveAuthPath, accountBNew.Bytes));

            using var service = CreateService(paths, appServer: verifier);
            var result = await service.SwitchToProfileAsync("AccountB");

            Check(result.Status == SwitchProfileStatus.Success, result.Message);
            Check((await Read(paths.ActiveAuthPath)).ContentFingerprint == accountBNew.ContentFingerprint,
                "The refreshed live generation was not retained.");
            Check((await Read(repository.GetProfileAuthPath("AccountB"))).ContentFingerprint == accountBNew.ContentFingerprint,
                "The refreshed generation was not captured in the target profile.");
        });
    }

    private static async Task UsageRefreshIsCredentialReadOnlyAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var accountA = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
            SeedProfiles(paths, accountA, ("AccountA", accountA));
            var usage = new FakeUsageClient();
            var profilePath = Repository(paths).GetProfileAuthPath("AccountA");
            var liveBefore = AuthHash.Bytes(await File.ReadAllBytesAsync(paths.ActiveAuthPath));
            var profileBefore = AuthHash.Bytes(await File.ReadAllBytesAsync(profilePath));

            using var service = CreateService(paths, usage: usage);
            await service.RefreshActiveProfileAsync();

            Check(AuthHash.Bytes(await File.ReadAllBytesAsync(paths.ActiveAuthPath)) == liveBefore,
                "Usage refresh modified live credentials.");
            Check(AuthHash.Bytes(await File.ReadAllBytesAsync(profilePath)) == profileBefore,
                "Usage refresh modified stored credentials.");
            Check(Directory.GetFiles(paths.BackupsDir).Length == 0, "Usage refresh created a backup.");
            Check(usage.Calls == 1 && File.Exists(paths.UsageCachePath), "Usage cache was not updated.");
        });
    }

    private static async Task JournalRecoveryAsync()
    {
        await InTemporaryStoreAsync(async paths =>
        {
            var accountA = Snapshot("user-a", "account-a", "workspace-a", "a", 1_700_000_000);
            var accountB = Snapshot("user-b", "account-b", "workspace-b", "b", 1_700_000_010);
            SeedProfiles(paths, accountB, ("AccountA", accountA), ("AccountB", accountB));
            var repository = Repository(paths);
            repository.SaveAppConfig(repository.LoadAppConfig() with { ActiveProfile = "AccountA" });
            repository.WriteJournal(new CredentialTransaction(
                "tx", "switch", "installed", "AccountA", "AccountB",
                accountA.ContentFingerprint, accountB.ContentFingerprint,
                accountB.Identity.Fingerprint, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")));

            Check(await repository.RecoverJournalAsync(), "The transaction was not recovered.");
            Check(repository.LoadAppConfig().ActiveProfile == "AccountB", "Journal recovery chose the wrong profile.");
            Check(!File.Exists(paths.TransactionJournalPath), "Recovered journal was not cleared.");
        });
    }

    private static Task UsageWindowsAreClassifiedByDurationAsync()
    {
        var appServerJson = JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 71, "windowDurationMins": 10080, "resetsAt": 200 },
                "secondary": { "usedPercent": 13, "windowDurationMins": 300, "resetsAt": 100 }
              }
            }
            """);
        var appServerUsage = CodexAppServerClient.NormalizeRateLimits(appServerJson);
        Check(appServerUsage.Session.WindowDurationMins == 300 && appServerUsage.Session.UsedPercent == 13,
            "app-server session window was selected by position instead of duration.");
        Check(appServerUsage.Weekly.WindowDurationMins == 10_080 && appServerUsage.Weekly.UsedPercent == 71,
            "app-server weekly window was selected by position instead of duration.");

        var whamJson = JsonNode.Parse("""
            {
              "rate_limit": {
                "primary_window": { "used_percent": 62, "limit_window_seconds": 604800, "reset_at": 200 },
                "secondary_window": { "used_percent": 9, "limit_window_seconds": 18000, "reset_at": 100 }
              }
            }
            """);
        var whamUsage = UsageClient.NormalizeWhamUsage(whamJson);
        Check(whamUsage.Session.WindowDurationMins == 300 && whamUsage.Session.UsedPercent == 9,
            "wham session window was selected by position instead of duration.");
        Check(whamUsage.Weekly.WindowDurationMins == 10_080 && whamUsage.Weekly.UsedPercent == 62,
            "wham weekly window was selected by position instead of duration.");

        var weeklyOnlyJson = JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 44, "windowDurationMins": 10080, "resetsAt": 300 },
                "secondary": null
              }
            }
            """);
        var weeklyOnly = CodexAppServerClient.NormalizeRateLimits(weeklyOnlyJson);
        Check(!weeklyOnly.Session.UsedPercent.HasValue && !weeklyOnly.Session.WindowDurationMins.HasValue,
            "A weekly-only response was duplicated into the five-hour window.");
        Check(weeklyOnly.Weekly.WindowDurationMins == 10_080 && weeklyOnly.Weekly.UsedPercent == 44,
            "The weekly-only response was not retained as weekly.");

        var cachedDuplicate = UsageWindowClassifier.NormalizeCached(new UsageRecord
        {
            Session = new UsageWindow(44, 10_080, 300),
            Weekly = new UsageWindow(44, 10_080, 300),
        });
        Check(!cachedDuplicate.Session.UsedPercent.HasValue && cachedDuplicate.Weekly.UsedPercent == 44,
            "A duplicated weekly window already present in the cache was not repaired.");
        return Task.CompletedTask;
    }

    private static CodexManagerService CreateService(
        AppPaths paths,
        FakeProcessController? process = null,
        FakeAppServerClient? appServer = null,
        FakeUsageClient? usage = null,
        bool startWatcher = false) =>
        new(paths, process ?? new FakeProcessController(false), appServer ?? new FakeAppServerClient(),
            usage ?? new FakeUsageClient(), startWatcher);

    private static ProfileRepository Repository(AppPaths paths) => new(paths, (_, _) => { });

    private static void SeedProfiles(
        AppPaths paths,
        AuthSnapshot live,
        params (string Name, AuthSnapshot Snapshot)[] profiles)
    {
        paths.EnsureDirectories();
        var repository = Repository(paths);
        foreach (var profile in profiles)
        {
            repository.AddOrUpdateFromSnapshot(profile.Name, profile.Snapshot);
        }
        AtomicFile.WriteBytes(paths.ActiveAuthPath, live.Bytes);
        var active = profiles.FirstOrDefault(item => item.Snapshot.Identity.Matches(live.Identity));
        repository.SaveAppConfig(repository.LoadAppConfig() with
        {
            ActiveProfile = string.IsNullOrWhiteSpace(active.Name) ? null : active.Name,
            LastUpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    private static AuthSnapshot Snapshot(
        string userId,
        string accountId,
        string workspaceId,
        string generation,
        long issuedAt)
    {
        var claims = new Dictionary<string, object?>
        {
            ["sub"] = userId,
            ["email"] = $"{userId}@example.test",
            ["iat"] = issuedAt,
            ["exp"] = issuedAt + 3_600,
            ["nonce"] = generation,
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_user_id"] = userId,
                ["chatgpt_account_id"] = accountId,
                ["organizations"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = workspaceId,
                        ["is_default"] = true,
                    },
                },
            },
        };
        var root = new Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgpt",
            ["tokens"] = new Dictionary<string, object?>
            {
                ["id_token"] = Jwt(claims),
                ["access_token"] = Jwt(claims),
                ["refresh_token"] = $"fake-refresh-{userId}-{generation}",
                ["account_id"] = accountId,
            },
            ["last_refresh"] = DateTimeOffset.FromUnixTimeSeconds(issuedAt).ToString("O"),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
        return AuthSnapshot.Parse(bytes, "fake auth");
    }

    private static string Jwt(object claims)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"))}.{Encode(JsonSerializer.SerializeToUtf8Bytes(claims))}.signature";
    }

    private static async Task<AuthSnapshot> Read(string path) => await StableAuthFile.ReadAsync(
        path,
        quietPeriod: TimeSpan.FromMilliseconds(5));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (IOException)
            {
                // The watcher may be between File.Replace and readback.
            }
            await Task.Delay(75);
        }
        throw new TimeoutException("The expected asynchronous state was not reached.");
    }

    private static async Task InTemporaryStoreAsync(Func<AppPaths, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await test(AppPaths.CreateForTesting(root));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeProcessController(bool running) : ICodexProcessController
    {
        public bool Running { get; private set; } = running;
        public int CloseCalls { get; private set; }
        public int RestartCalls { get; private set; }

        public CodexRuntimeState CaptureState() => new(Running, "fake-codex.exe", Running ? [42] : []);

        public Task<bool> CloseNormallyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            CloseCalls++;
            Running = false;
            return Task.FromResult(true);
        }

        public Task<bool> RestartAsync(string? preferredExecutablePath, TimeSpan timeout, CancellationToken cancellationToken)
        {
            RestartCalls++;
            Running = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeAppServerClient(
        AuthVerificationResult? verification = null,
        UsageRecord? usage = null,
        Action? onVerify = null) : ICodexAppServerClient
    {
        public Task<AuthVerificationResult> VerifyAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            onVerify?.Invoke();
            return Task.FromResult(
                verification ?? new AuthVerificationResult(AuthVerificationStatus.Success, "fake@example.test", null));
        }

        public Task<UsageRecord> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(usage ?? FakeUsageClient.Result());
    }

    private sealed class FakeUsageClient : IUsageClient
    {
        public int Calls { get; private set; }

        public Task<UsageRecord> ReadAsync(AuthSnapshot snapshot, string codexConfigPath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result());
        }

        public static UsageRecord Result() => new()
        {
            CheckedAt = DateTimeOffset.UtcNow.ToString("O"),
            Status = "ok",
            Session = new UsageWindow(10, 300, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()),
            Weekly = new UsageWindow(20, 10_080, DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds()),
        };
    }
}
