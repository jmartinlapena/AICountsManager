using AICountsManager;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private const string ClaudeSecond = "33333333-3333-4333-8333-333333333333";
    private sealed class ClaudeFixtureStore : IDisposable
    {
        internal readonly string Desktop, Code, CodeConfig;
        internal readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        internal readonly SessionHttp Handler = new();
        internal readonly HttpClient Http;
        internal readonly ClaudeSessionArchive Archive;
        internal bool Running;
        internal int Requests;
        internal ClaudeFixtureStore(AppPaths paths)
        {
            Desktop = Path.Combine(paths.AppHome, "fake-desktop"); Code = Path.Combine(paths.AppHome, "fake-code");
            CodeConfig = Path.Combine(paths.AppHome, "fake-code.json");
            Directory.CreateDirectory(Path.Combine(Desktop, "Network")); Directory.CreateDirectory(Code);
            var wrapped = ProtectedData.Protect(Key, null, DataProtectionScope.CurrentUser);
            AtomicFile.WriteText(Path.Combine(Desktop, "Local State"), JsonSerializer.Serialize(new { os_crypt = new { encrypted_key = Convert.ToBase64String("DPAPI"u8.ToArray().Concat(wrapped).ToArray()) } }));
            Handler.Reply = (request, _) =>
            {
                Requests++;
                var b = request.Headers.Authorization?.Parameter?.Contains("second") == true;
                return Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("profile")
                    ? JsonSerializer.Serialize(new { account = new { uuid = b ? ClaudeSecond : DesktopAccount, email = b ? "b@example.test" : "a@example.test", display_name = b ? "Second" : "First" }, organization = new { uuid = DesktopOrg } })
                    : ClaudeFixture()));
            };
            Http = new HttpClient(Handler);
            Archive = new ClaudeSessionArchive(Desktop, Code, CodeConfig, Path.Combine(paths.TransactionsDir, "claude-test-rollback.bin"), Http, () => Running);
        }
        internal void Login(string account, bool code = true)
        {
            var token = account == ClaudeSecond ? "fixture-second" : "fixture-first";
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { [DesktopKey(account)] = DesktopEntry(token) });
            var bytes = new byte[31 + plain.Length]; "v10"u8.CopyTo(bytes); RandomNumberGenerator.Fill(bytes.AsSpan(3, 12));
            using var aes = new AesGcm(Key, 16); aes.Encrypt(bytes.AsSpan(3, 12), plain, bytes.AsSpan(15, plain.Length), bytes.AsSpan(bytes.Length - 16));
            AtomicFile.WriteText(Path.Combine(Desktop, "config.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            { ["theme"] = "user-theme", ["lastKnownAccountUuid"] = account, ["oauth:tokenCacheV2"] = Convert.ToBase64String(bytes) }));
            AtomicFile.WriteBytes(Path.Combine(Desktop, "Network", "Cookies"), Encoding.UTF8.GetBytes("encrypted-cookie-fixture-" + account));
            AtomicFile.WriteText(Path.Combine(Desktop, "keep-history.txt"), "history-not-auth");
            if (code)
            {
                AtomicFile.WriteText(Path.Combine(Code, ".credentials.json"), JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = token, refreshToken = "fixture-refresh-only", expiresAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds() }, otherSetting = "keep" }));
                AtomicFile.WriteText(CodeConfig, JsonSerializer.Serialize(new { oauthAccount = new { accountUuid = account, organizationUuid = DesktopOrg }, projects = new { preserved = true } }));
            }
        }
        internal Dictionary<string, string> Digests() => new[] { Path.Combine(Desktop,"config.json"), Path.Combine(Desktop,"Network","Cookies"), Path.Combine(Code,".credentials.json"), CodeConfig, Path.Combine(Desktop,"Local State") }
            .ToDictionary(p => p, p => File.Exists(p) ? AuthHash.Bytes(File.ReadAllBytes(p)) : "absent");
        public void Dispose() { Http.Dispose(); CryptographicOperations.ZeroMemory(Key); }
    }
    private static Task ClaudeArchiveRoundTripAsync() => InTemporaryStoreAsync(async paths =>
    {
        using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var first = fixture.Archive.Capture(); var identified = await fixture.Archive.IdentifyAsync(first, default);
        Check(identified?.Account == DesktopAccount, "Desktop and CLI identity verification failed.");
        fixture.Archive.Apply(ClaudeSessionArchive.Empty);
        Check(new ClaudeDesktopSessionSource(fixture.Desktop).Read() is null && new ClaudeSessionSource(fixture.Code).Read() is null, "New login did not clear both session sources.");
        Check(!File.Exists(Path.Combine(fixture.Desktop,"Network","Cookies")), "The previous web session survived new-account preparation.");
        Check(JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Code,".credentials.json")))!["otherSetting"]!.GetValue<string>() == "keep", "CLI settings were removed.");
        fixture.Login(ClaudeSecond); var second = fixture.Archive.Capture();
        fixture.Archive.Apply(first);
        Check((await fixture.Archive.IdentifyAsync(fixture.Archive.Capture(), default))?.Account == DesktopAccount, "Restore A did not restore both client logins.");
        fixture.Archive.Apply(second);
        Check((await fixture.Archive.IdentifyAsync(fixture.Archive.Capture(), default))?.Account == ClaudeSecond, "Restore B did not restore both client logins.");
        Check(File.ReadAllText(Path.Combine(fixture.Desktop,"keep-history.txt")) == "history-not-auth", "Switch modified history.");
        Check(JsonNode.Parse(File.ReadAllBytes(fixture.CodeConfig))!["projects"]!["preserved"]!.GetValue<bool>(), "Switch modified CLI projects.");
    });
    private static Task ClaudeArchiveRollbackAsync() => InTemporaryStoreAsync(paths =>
    {
        using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var before = fixture.Digests();
        for (var failure = 1; failure <= 7; failure++)
        {
            fixture.Archive.AfterWriteForTesting = count => { if (count == failure) throw new IOException("injected"); };
            var rejected = false;
            try { fixture.Archive.Apply(ClaudeSessionArchive.Empty); } catch (IOException) { rejected = true; }
            Check(rejected && before.All(p => fixture.Digests()[p.Key] == p.Value) && !fixture.Archive.HasRecovery, "Rollback was not byte-exact after write " + failure);
        }
        fixture.Archive.AfterWriteForTesting = n => { if (n == 1) fixture.Running = true; };
        try { fixture.Archive.Apply(ClaudeSessionArchive.Empty); } catch (InvalidOperationException) { }
        Check(fixture.Archive.HasRecovery, "A client opening mid-write lost the recovery journal.");
        fixture.Running = false; fixture.Archive.Recover();
        Check(before.All(p => fixture.Digests()[p.Key] == p.Value), "Deferred rollback was not byte-exact.");
        fixture.Archive.AfterWriteForTesting = null; fixture.Running = true;
        var blocked = false; try { fixture.Archive.Apply(ClaudeSessionArchive.Empty); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && before.All(p => fixture.Digests()[p.Key] == p.Value), "Running Claude was overwritten.");
        fixture.Running = false;
        var bundle = fixture.Archive.Capture() with { DesktopKey = "another-machine" };
        blocked = false; try { fixture.Archive.Apply(bundle); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && before.All(p => fixture.Digests()[p.Key] == p.Value), "A profile encrypted by another installation was installed.");
        bundle = fixture.Archive.Capture(); bundle.Cookies["../history"] = [1];
        blocked = false; try { fixture.Archive.Apply(bundle); } catch (InvalidDataException) { blocked = true; }
        Check(blocked, "An archive selected a path outside its fixed file set.");
        return Task.CompletedTask;
    });
    private static Task ClaudeAccountsLifecycleAsync() => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories(); using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        using var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => {}, (_,_) => {});
        await accounts.SetEnabledAsync(true);
        await accounts.BeginAsync("Personal Claude");
        var first = accounts.State.Accounts.Single();
        Check(accounts.PendingName == "Personal Claude" && first.Name != "Personal Claude", "Add cloned current login under the new name.");
        fixture.Login(ClaudeSecond);
        await accounts.RefreshAsync(true);
        Check(accounts.PendingName is null && accounts.Accounts.Count == 2, "New login not captured.");
        var second = accounts.State.Accounts.Single(a => a.Account == ClaudeSecond);
        Check(second.Name == "Personal Claude", "New account lost its requested name.");
        var before = fixture.Digests(); await accounts.RefreshAsync(true);
        Check(before.All(p => fixture.Digests()[p.Key] == p.Value), "Bulk refresh modified a client login.");
        Check(accounts.Accounts.All(a => a.Status == "ok"), "Refresh did not query all saved accounts.");
        await accounts.SwitchAsync(first.Id);
        Check((await fixture.Archive.IdentifyAsync(fixture.Archive.Capture(), default))?.Account == DesktopAccount, "Switch did not install the selected account.");
        await accounts.RenameAsync(second.Id, "Trabajo Claude");
        Check(accounts.State.Accounts.Single(a => a.Id == second.Id).Name == "Trabajo Claude", "Rename changed the wrong account.");
        await accounts.BeginAsync("Temporal"); await accounts.CancelAsync();
        Check(accounts.PendingName is null && (await fixture.Archive.IdentifyAsync(fixture.Archive.Capture(), default))?.Account == DesktopAccount, "Cancel lost the old account.");
        await accounts.SetEnabledAsync(false); var calls = fixture.Requests;
        await accounts.RefreshAsync(true);
        Check(fixture.Requests == calls && accounts.Accounts.Count == 0 && accounts.State.Accounts.Count == 2, "Disabled Claude queried or lost saved accounts.");
        using var restored = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => {}, (_,_) => {});
        Check(!restored.Enabled && restored.State.Accounts.Count == 2, "Accounts or toggle did not survive restart.");
        fixture.Handler.Reply = (_, _) =>
        {
            fixture.Requests++;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        };
        calls = fixture.Requests;
        await restored.SetEnabledAsync(true);
        await restored.RefreshAsync(true); await restored.RefreshAsync(true);
        Check(fixture.Requests == calls + 1, "Claude ignored the shared Retry-After across saved accounts.");
        Check(restored.Accounts.All(a => a.Status == "stale" && a.Quotas.Count != 0), "Rate limiting discarded cached quota readings.");
    });
    private static Task ClaudeMixedIdentityAsync() => InTemporaryStoreAsync(async paths =>
    {
        paths.EnsureDirectories(); using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var firstCode = File.ReadAllBytes(Path.Combine(fixture.Code,".credentials.json"));
        fixture.Login(ClaudeSecond); AtomicFile.WriteBytes(Path.Combine(fixture.Code,".credentials.json"), firstCode);
        var before = fixture.Digests();
        using var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => {}, (_,_) => {});
        await accounts.SetEnabledAsync(true);
        var blocked = false; try { await accounts.BeginAsync("New"); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && before.All(p => fixture.Digests()[p.Key] == p.Value), "Mixed Desktop/terminal identities were overwritten.");
    });
    private static Task ClaudeAccountsUiAsync() => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        paths.EnsureDirectories(); using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => {}, (_,_) => {});
        await accounts.SetEnabledAsync(true); await accounts.BeginAsync("Second"); fixture.Login(ClaudeSecond); await accounts.RefreshAsync(true);
        var process = new FakeProcessController(true); // Codex must not block or be touched by a Claude switch.
        using var service = new AICountsManagerService(paths, process, new NoPollingAppServer(), new FakeUsageClient(), false, claudeAccounts: accounts);
        await service.SwitchClaudeAccountAsync(accounts.State.Accounts.First().Id);
        Check(process.Running && process.CloseCalls == 0 && process.RestartCalls == 0, "Claude switching touched Codex.");
        using var form = new WidgetForm(service, new UiTheme(true,false,false), false); form.ApplyState(service.BuildDashboardState());
        var items = UiField<System.Windows.Controls.ContextMenu>(form,"_addMenu").Items.OfType<System.Windows.Controls.MenuItem>().ToArray();
        Check(items.Count(m => m.Header.ToString()!.StartsWith("Añadir cuenta")) == 2, "Plus menu does not offer Codex and Claude.");
        var state = service.BuildDashboardState();
        Check(state.OrderedAccounts.Count == 2 && state.OrderedAccounts.Select(a => a.Id).Distinct().Count() == 2, "Claude accounts collided in ordering.");
        await service.SetAccountOrderAsync(state.OrderedAccounts.Reverse().Select(a => a.Id).ToArray());
        form.ApplyState(service.BuildDashboardState()); form.ShowSettings(true);
        Check(((System.Windows.Controls.StackPanel)form.Visual.FindName("OrderList")).Children.Count == 2, "Settings lost a Claude account.");
        var cards = UiField<Dictionary<string,CurrentProviderCard>>(form,"_externalRows");
        Check(cards.Count == 2, "Dashboard reused one provider card for different accounts.");
    }));
}
