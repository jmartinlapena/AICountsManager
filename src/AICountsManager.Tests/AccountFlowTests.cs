using AICountsManager;
using System.Text.Json;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static Task CodexProviderToggleAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("a", "a", "w", "1", 1700000000);
        SeedProfiles(paths, a, ("A", a));
        var before = File.ReadAllBytes(paths.ActiveAuthPath);
        var client = new FakeUsageClient();
        var agy = new FakeQuotaProvider();
        using (var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), client, false, agy))
        {
            Check(service.CurrentProviders.Single(p => p.Id == ProviderIds.Codex).Enabled, "Existing configuration must enable Codex by default.");
            await service.SetCurrentProviderEnabledAsync(ProviderIds.Codex, false);
            Check(service.BuildDashboardState().Profiles.Count == 0 && Repository(paths).ListProfiles().Count == 1, "Disabled provider lost saved accounts or remained visible.");
            await service.RefreshActiveProfileAsync(); await service.RefreshAllUsageAsync(); await service.RefreshHistoryAsync();
            Check(client.Calls == 0 && agy.Calls == 1, "Disabled Codex queried its credentials or blocked the other provider.");
            Check(!File.Exists(Path.Combine(paths.AppHome,"cache","token_history.json")), "Disabled Codex scanned local history.");
            Check(!service.History.Sources!.ContainsKey(ProviderIds.Codex), "Disabled Codex leaked into history.");
            await service.SetAccountOrderAsync([AccountOrdering.Current(ProviderIds.Antigravity)]);
        }
        using (var restored = CreateService(paths, usage: client))
        {
            Check(!restored.CodexEnabled, "Provider toggle did not persist.");
            await restored.SetCurrentProviderEnabledAsync(ProviderIds.Codex, true);
            Check(restored.BuildDashboardState().Profiles.Single().Name == "A" && client.Calls == 1, "Re-enabling did not restore Codex.");
        }
        Check(before.SequenceEqual(File.ReadAllBytes(paths.ActiveAuthPath)), "Toggling modified the login.");
    });

    private static Task CodexEnrollmentCaptureAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("a", "a", "w", "1", 1700000000); var b = Snapshot("b", "b", "w", "1", 1700000010);
        SeedProfiles(paths, a, ("A", a)); var process = new FakeProcessController(false);
        using var service = new AICountsManagerService(paths, process, new NoPollingAppServer(), new FakeUsageClient(), true, new FakeQuotaProvider(), additionalProviders: []);
        await service.BeginCodexAccountAsync("B");
        Check(!File.Exists(paths.ActiveAuthPath) && service.PendingCodexName == "B", "New account did not enter logged-out state.");
        Check(Repository(paths).ListProfiles().Count == 1 && (await Read(Repository(paths).GetProfileAuthPath("A"))).ContentFingerprint == a.ContentFingerprint, "Add duplicated or lost the old profile.");
        Check(process.CloseCalls == 0 && process.RestartCalls == 0, "Add opened or closed the client.");
        AtomicFile.WriteBytes(paths.ActiveAuthPath, b.Bytes);
        await EventuallyAsync(() => Task.FromResult(service.PendingCodexName is null));
        Check(Repository(paths).LoadAppConfig().ActiveProfile == "B" && Repository(paths).ListProfiles().Count == 2, "Watcher did not capture and activate the new identity.");
        Check((await Read(Repository(paths).GetProfileAuthPath("B"))).ContentFingerprint == b.ContentFingerprint, "Captured the wrong credentials.");
    });

    private static Task CodexEnrollmentRecoveryAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("a", "a", "w", "1", 1700000000); var b = Snapshot("b", "b", "w", "1", 1700000010);
        SeedProfiles(paths, a, ("A", a));
        using (var service = CreateService(paths)) await service.BeginCodexAccountAsync("B");
        AtomicFile.WriteBytes(paths.ActiveAuthPath, b.Bytes);
        using (var recovered = CreateService(paths)) { await recovered.InitializeAsync(); Check(recovered.PendingCodexName is null && recovered.BuildDashboardState().Profiles.Count == 2, "Login completed while manager closed was not recovered."); }
        using (var service = CreateService(paths))
        {
            await service.BeginCodexAccountAsync("C");
            var rejected = false;
            try { await service.DeleteProfileAsync("B"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Pending source profile could be deleted.");
            await service.CancelCodexAccountAsync();
            Check(service.PendingCodexName is null && (await Read(paths.ActiveAuthPath)).Identity.Matches(b.Identity), "Cancel did not restore the source login.");
            Check(Repository(paths).ListProfiles().Count == 2, "Cancellation left an empty account.");
        }
    });

    private static Task CodexEnrollmentBoundariesAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("a", "a", "w", "1", 1700000000); SeedProfiles(paths, a);
        using var service = CreateService(paths);
        await service.BeginCodexAccountAsync("New");
        var preserved = Repository(paths).ListProfiles().Single();
        Check((await Read(Repository(paths).GetProfileAuthPath(preserved.Name))).ContentFingerprint == a.ContentFingerprint, "An unregistered current account was lost.");
        var blocked = false;
        try { await service.SetCurrentProviderEnabledAsync(ProviderIds.Codex, false); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && service.CodexEnabled, "An unfinished login could lose its watcher.");
        // A client writing the old login back must not turn it into the new named account.
        AtomicFile.WriteBytes(paths.ActiveAuthPath, a.Bytes);
        await service.RefreshAllUsageAsync();
        Check(service.PendingCodexName == "New" && Repository(paths).ListProfiles().Count == 1, "The outgoing login was cloned into a new profile.");
        await service.CancelCodexAccountAsync();
        var rejected = false;
        try { await service.BeginCodexAccountAsync(preserved.Name); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.Exists(paths.ActiveAuthPath), "A duplicate name disturbed the active login.");
    });
    private sealed class RefuseCloseController : ICodexProcessController
    {
        public CodexRuntimeState CaptureState() => new(true, null, [42]);
        public Task<bool> CloseNormallyAsync(TimeSpan timeout, CancellationToken token) => Task.FromResult(false);
        public Task<bool> RestartAsync(string? path, TimeSpan timeout, CancellationToken token) => throw new InvalidOperationException("Unexpected restart");
    }

    private static Task CodexEnrollmentFailureAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("a", "a", "w", "1", 1700000000); SeedProfiles(paths, a, ("A", a));
        using (var service = new AICountsManagerService(paths, new RefuseCloseController(), new NoPollingAppServer(), new FakeUsageClient(), false))
        {
            var rejected = false;
            try { await service.BeginCodexAccountAsync("B"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && service.PendingCodexName is null && (await Read(paths.ActiveAuthPath)).ContentFingerprint == a.ContentFingerprint,
                "Refusing to close must leave login and pending state intact.");
        }
        using (var service = CreateService(paths))
        {
            await service.BeginCodexAccountAsync("B");
            var b = Snapshot("b", "b", "w", "1", 1700000010); AtomicFile.WriteBytes(paths.ActiveAuthPath, b.Bytes);
            await service.CancelCodexAccountAsync();
            Check((await Read(paths.ActiveAuthPath)).Identity.Matches(b.Identity) && Repository(paths).LoadAppConfig().ActiveProfile == "B",
                "Cancel discarded a newly completed login before the watcher ran.");
        }
    });

    private static Task CodexDisabledWatcherAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("a", "a", "w", "1", 1700000000); var next = Snapshot("a", "a", "w", "2", 1700000100);
        SeedProfiles(paths, a, ("A", a));
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), true, new FakeQuotaProvider(), additionalProviders: []);
        await service.SetCurrentProviderEnabledAsync(ProviderIds.Codex, false);
        AtomicFile.WriteBytes(paths.ActiveAuthPath, next.Bytes); await Task.Delay(1500);
        Check((await Read(Repository(paths).GetProfileAuthPath("A"))).ContentFingerprint == a.ContentFingerprint,
            "Disabled watcher continued reading credentials and writing profiles.");
        await service.SetCurrentProviderEnabledAsync(ProviderIds.Codex, true);
        Check((await Read(Repository(paths).GetProfileAuthPath("A"))).ContentFingerprint == next.ContentFingerprint,
            "Re-enabling lost credential rotation that occurred while disabled.");
    });

}
