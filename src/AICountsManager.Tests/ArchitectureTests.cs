using AICountsManager;
using System.Security.Cryptography;
using System.Text.Json;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static Task ProtectedArchiveCompatibilityAsync() => InTemporaryStoreAsync(paths =>
    {
        paths.EnsureDirectories();
        var file = Path.Combine(paths.BackupsDir, "legacy-format.bin");
        var bundle = new ClaudeCredentialBundle([1,2,3], "same-installation", ClaudeSessionArchive.CookieNames.ToDictionary(k => k, _ => (byte[]?)[4,5]), [6,7], [8,9]);
        var oldJson = JsonSerializer.SerializeToUtf8Bytes(bundle);
        AtomicFile.WriteBytes(file, ProtectedData.Protect(oldJson, null, DataProtectionScope.CurrentUser));
        var restored = ProtectedJsonStore.Load<ClaudeCredentialBundle>(file);
        Check(JsonSerializer.SerializeToUtf8Bytes(restored).SequenceEqual(oldJson), "Shared persistence cannot read the existing credential format.");
        ProtectedJsonStore.Save(file, restored);
        var oldReader = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        Check(oldReader.SequenceEqual(oldJson), "New persistence changed the JSON, DPAPI scope or entropy used by previous releases.");
        var journal = new OrganizationJournal("fixture", "codex", paths.CodexHome, "source", "target", "applied", [new(Path.Combine(paths.CodexHome,".codex-global-state.json"),[1],[2])]);
        oldJson = JsonSerializer.SerializeToUtf8Bytes(journal);
        AtomicFile.WriteBytes(file, ProtectedData.Protect(oldJson, null, DataProtectionScope.CurrentUser));
        Check(JsonSerializer.SerializeToUtf8Bytes(ProtectedJsonStore.Load<OrganizationJournal>(file)).SequenceEqual(oldJson), "An existing organization recovery journal is no longer readable.");
        AtomicFile.WriteBytes(file, [1,2,3,4]); var rejected = false;
        try { _ = BoundedFile.Read(file, 3); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Common file reads lost their size limit.");
        return Task.CompletedTask;
    });

    private static async Task InitializationIsSharedAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var initialization = new ServiceInitialization(async token => { Interlocked.Increment(ref calls); started.SetResult(); await release.Task.WaitAsync(token); });
        using var cancelledCaller = new CancellationTokenSource();
        var one = initialization.RunAsync(cancelledCaller.Token); var two = initialization.RunAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancelledCaller.Cancel();
        var cancelled = false; try { await one; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !two.IsCompleted && calls == 1 && initialization.IsRunning, "One caller cancelled another caller's initialization or started a second startup.");
        release.SetResult(); await two;
        await Task.WhenAll(Enumerable.Range(0,10).Select(_ => initialization.RunAsync()));
        Check(calls == 1 && !initialization.IsRunning, "Completed initialization ran again.");
    }

    private static async Task InitializationShutdownAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialization = new ServiceInitialization(async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.SetResult(); }
        });
        var task = initialization.RunAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        initialization.Dispose(); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var cancelled = false; try { await task; } catch (OperationCanceledException) { cancelled = true; }
        var rejected = false; try { await initialization.RunAsync(); } catch (ObjectDisposedException) { rejected = true; }
        Check(cancelled && rejected, "Shutdown left startup running or allowed it to restart.");
    }

    private static Task TrayStartsBeforeRecoveryAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        var a = Snapshot("a","a","w","1",1700000000); SeedProfiles(paths,a,("A",a));
        using var blocked = new GlobalAuthSwitchLock(paths.CodexHome);
        await blocked.AcquireAsync(TimeSpan.FromSeconds(2), default);
        using var service = CreateService(paths);
        using var tray = new TrayApplicationContext(service);
        try
        {
            Check(UiField<System.Windows.Forms.NotifyIcon>(tray,"_notifyIcon").Visible && !tray.WidgetCreated, "Startup hid the tray or eagerly created the flyout.");
            var recovery = service.InitializeAsync();
            Check(!recovery.IsCompleted && service.IsMutationInProgress, "The recovery phase did not remain behind the existing credential lock.");
            blocked.Dispose(); await recovery;
            Check(service.BuildDashboardState().ActiveProfile.Name == "A" && !tray.WidgetCreated, "Async recovery changed startup state or created the interface.");
        }
        finally { tray.ExitThread(); }
    }));

    private static Task SharedAccountManagementAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a = Snapshot("a","a","w","1",1700000000); var b = Snapshot("b","b","w","1",1700000010);
        SeedProfiles(paths,a,("A",a),("B",b));
        using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var claude = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => {}, (_,_) => {});
        await claude.SetEnabledAsync(true); await claude.BeginAsync("Second"); fixture.Login(ClaudeSecond); await claude.RefreshAsync(true);
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, claudeAccounts:claude);
        await service.InitializeAsync(); var registry = service.AccountProviders;
        Check(registry.CanManage(ProviderIds.Codex) && registry.CanManage(ProviderIds.Claude), "Named accounts still use incompatible provider contracts.");
        var queries = fixture.Requests;
        await registry.Manager(ProviderIds.Codex).RefreshAsync(true, default);
        Check(fixture.Requests == queries, "A provider-specific refresh queried a different provider.");
        var beforeClaude = fixture.Digests();
        var result = await service.ActivateProviderAccountAsync(ProviderIds.Codex,"B");
        Check(result.Status == AccountActivationStatus.Success && (await Read(paths.ActiveAuthPath)).Identity.Matches(b.Identity)
            && beforeClaude.All(p => fixture.Digests()[p.Key] == p.Value), "Generic Codex activation changed its behavior or Claude credentials.");
        var beforeCodex = File.ReadAllBytes(paths.ActiveAuthPath);
        var first = claude.State.Accounts.Single(x => x.Account == DesktopAccount);
        await service.ActivateProviderAccountAsync(ProviderIds.Claude, first.Id);
        await service.RenameProviderAccountAsync(ProviderIds.Claude, first.Id, "Renamed");
        Check(claude.State.ActiveId == first.Id && claude.State.Accounts.Single(x => x.Id == first.Id).Name == "Renamed" && File.ReadAllBytes(paths.ActiveAuthPath).SequenceEqual(beforeCodex), "Generic Claude management changed Codex or the saved identity.");
        await service.SetCurrentProviderEnabledAsync(ProviderIds.Claude,false);
        Check(!registry.IsEnabled(ProviderIds.Claude) && claude.State.Accounts.Count == 2 && registry.IsEnabled(ProviderIds.Codex), "Common enablement lost accounts or changed another provider.");
        foreach (var status in Enum.GetValues<SwitchProfileStatus>())
        {
            var original = new SwitchProfileResult(status,"exact message","A","B",true);
            Check(AccountActivationResult.FromCodex(original).ToCodex() == original, "The compatibility facade changed a Codex result.");
        }
    });

    private static async Task ExtensibleAccountRegistryAsync()
    {
        var enabled = true; var events = new List<string>();
        var reading = ProviderUsage.Unavailable("future-ai","fixture") with { ProfileId="saved-id" };
        var future = new ManagedAccountProvider("future-ai",0,() => enabled,() => "pending",() => [reading],
            (value,_) => { enabled=value; events.Add("enabled"); return Task.CompletedTask; },
            (all,_) => { events.Add(all?"all":"current"); return Task.CompletedTask; },new(
                (name,_) => { events.Add("begin:"+name); return Task.FromResult<string?>(name); },
                _ => { events.Add("cancel"); return Task.CompletedTask; },
                (id,_,_) => { events.Add("activate:"+id); return Task.FromResult(new AccountActivationResult(AccountActivationStatus.Success,"ok",null,id)); },
                (id,name,_) => { events.Add("rename:"+id+":"+name); return Task.CompletedTask; },
                (id,_) => { events.Add("delete:"+id); return Task.CompletedTask; }));
        var registry = new AccountProviderRegistry([future]);
        Check(registry.Settings.Single().Id == "future-ai" && registry.CanManage("future-ai") && registry.PendingName("future-ai") == "pending", "A new named-account provider required a hardcoded branch.");
        var manager=registry.Manager("future-ai"); await manager.BeginAsync("name",default); await manager.ActivateAsync("saved-id",false,default);
        await manager.RenameAsync("saved-id","next",default); await manager.CancelAsync(default); await manager.DeleteAsync("saved-id",default);
        await registry.RefreshExceptAsync("other",true,default); await registry.SetEnabledAsync("future-ai",false,default);
        var count=events.Count; await registry.RefreshExceptAsync("other",true,default);
        Check(events.Count == count && registry.ReadingsExcept("other").Count == 0 && events.SequenceEqual(["begin:name","activate:saved-id","rename:saved-id:next","cancel","delete:saved-id","all","enabled"]), "The provider registry changed commands or queried a disabled provider.");
        var rejected=false; try { _=new AccountProviderRegistry([future,future]); } catch(ArgumentException) { rejected=true; }
        Check(rejected,"Duplicate provider identities were accepted.");
    }

    private static Task ConstructorPreservesSavedDataAsync() => InTemporaryStoreAsync(async paths =>
    {
        var a=Snapshot("a","a","w","1",1700000000); SeedProfiles(paths,a,("A",a));
        var source=Path.Combine(paths.CodexHome,".codex-global-state.json"); PutJson(source,CodexOrganization(new("a","a"),new("b","b")));
        var tracked=new[]{paths.ActiveAuthPath,paths.AppConfigPath,Repository(paths).GetProfileAuthPath("A"),source};
        var before=tracked.ToDictionary(p=>p,File.ReadAllBytes);
        using(var service=CreateService(paths))
        {
            Check(before.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Constructing the service altered persisted state.");
            await service.InitializeAsync(); await service.InitializeAsync();
            Check(before.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Initialization migrated or replaced unchanged account data.");
        }
        Check(Repository(paths).ListProfiles().Single().Name=="A","Shutdown lost a stored account.");
        using(var adding=CreateService(paths)) await adding.BeginCodexAccountAsync("B");
        var b=Snapshot("b","b","w","1",1700000010); AtomicFile.WriteBytes(paths.ActiveAuthPath,b.Bytes);
        using(var recovered=CreateService(paths))
        {
            var result=await recovered.AccountProviders.Manager(ProviderIds.Codex).ActivateAsync("A",false,default);
            Check(result.Status==AccountActivationStatus.Success && Repository(paths).ListProfiles().Count==2 && recovered.PendingCodexName is null,
                "A direct common-contract command bypassed startup recovery or lost a completed login.");
        }
    });

    private static Task NamedTestGroupsStableAsync()
    {
        foreach(var group in TestGroupDefinitions.Keys)
        {
            var expected=SelectTestGroup(group).Select(t=>t.Run.Method.Name).ToHashSet(StringComparer.Ordinal);
            var actual=SelectTestGroup(group,Tests.Reverse().Append(("new unrelated case",()=>Task.CompletedTask))).Select(t=>t.Run.Method.Name).ToHashSet(StringComparer.Ordinal);
            Check(expected.SetEquals(actual),"Adding or reordering tests changed a named group: "+group);
            var missing=Tests.Where(t=>t.Run.Method.Name!=expected.First());
            var rejected=false; try { SelectTestGroup(group,missing); } catch(InvalidOperationException) { rejected=true; }
            Check(rejected,"An incomplete group passed silently: "+group);
        }
        return Task.CompletedTask;
    }
}
