using AICountsManager;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using C = System.Windows.Controls;
using D = System.Windows.Documents;
using W = System.Windows;
using M = System.Windows.Media;

namespace AICountsManager.Tests;
internal static partial class Program
{
    private static Task QuotaFeedbackExpiryAsync() => RunOnUiThreadAsync(() =>
    {
        var now = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
        foreach (var theme in new[] { new UiTheme(true, false, false), new UiTheme(false, false, false), new UiTheme(true, false, true) })
        {
            var view = new AvailableResetsView(); var label = UiField<C.TextBlock>(view, "_label");
            foreach (var (remaining, visible, urgent) in new[]
            { (TimeSpan.FromDays(30), true, false), (TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1), true, false), (TimeSpan.FromDays(7), true, true),
              (TimeSpan.FromDays(3) + TimeSpan.FromHours(4), true, true), (TimeSpan.FromHours(6), true, true), (TimeSpan.FromSeconds(1), true, true), (TimeSpan.Zero, false, false) })
            {
                var inventory = new ResetInventory(1, [new("full", "Reset completo", "Semanal", 1, now + remaining)], now);
                view.Update(inventory, theme, now); view.Update(inventory, theme, now);
                Check((view.Visibility == W.Visibility.Visible) == visible && !view.Expanded, "Expiry hint changed zero-inventory visibility or expanded the disclosure.");
                var hints = label.Inlines.OfType<D.Run>().Where(r => r.Text.Contains("Caduca pronto")).ToArray();
                Check(hints.Length == (urgent ? 1 : 0), "Seven-day boundary, duplicate warning or stale inline state is incorrect.");
                Check(W.Automation.AutomationProperties.GetName(UiField<C.Button>(view, "_toggle")).Contains("Caduca pronto") == urgent,
                    "Accessible reset label omitted or retained the expiry warning.");
                if (urgent) Check(((M.SolidColorBrush)hints.Single().Foreground).Color == theme.Warning
                    && ((M.SolidColorBrush)label.Foreground).Color == theme.Muted, "Only the impending expiry should use the warning color.");
            }
            view.Update(new(0, [], now), theme, now);
            Check(view.Visibility == W.Visibility.Collapsed && view.EstimatedHeight == 0, "Zero resets occupied a line.");
            view.Update(new(1, [new("full", "Reset", "", 1, null)], now), theme, now);
            Check(!new D.TextRange(label.ContentStart, label.ContentEnd).Text.Contains("Caduca pronto"), "No expiry gained an invented warning.");
            view.Update(new(3, [new("full", "Reset", "", 1, now.AddDays(-1)), new("full", "Reset", "", 1, now.AddDays(3).AddHours(4)),
                new("full", "Reset", "", 1, now.AddDays(30))], now), theme, now);
            var caption = new D.TextRange(label.ContentStart, label.ContentEnd).Text;
            Check(caption.Contains("2 resets disponibles") && caption.Contains("3d 4h"), "Next expiry counted an expired grant or chose a later expiration.");
            var inventoryAtStart = new ResetInventory(1, [new("full", "Reset", "", 1, now.AddDays(8))], now);
            view.Update(inventoryAtStart, theme, now); Check(!new D.TextRange(label.ContentStart, label.ContentEnd).Text.Contains("Caduca pronto"), "Eight days was considered urgent.");
            view.Update(inventoryAtStart, theme, now.AddDays(1)); Check(new D.TextRange(label.ContentStart, label.ContentEnd).Text.Contains("Caduca pronto"), "Existing clock refresh did not update the urgency.");
        }
        return Task.CompletedTask;
    });

    private static async Task QuotaFeedbackTransportAsync()
    {
        var offline = new HttpRequestException("raw request details must not reach the UI", new SocketException((int)SocketError.NetworkUnreachable));
        Check(QuotaFailure.FromException(offline) == QuotaFailure.Connection
            && QuotaFailure.FromException(new SocketException((int)SocketError.HostNotFound)) == QuotaFailure.Connection
            && QuotaFailure.FromException(new IOException("stream", offline)) == QuotaFailure.Connection, "Transport/DNS errors were not classified as connection failures.");
        Check(QuotaFailure.FromException(new HttpRequestException("raw 503", null, HttpStatusCode.ServiceUnavailable)) == QuotaFailure.Service
            && QuotaFailure.FromException(new TimeoutException()) == QuotaFailure.Timeout, "Server refusal or timeout became missing Wi-Fi.");
        var handler = new SessionHttp(); using var http = new HttpClient(handler);
        var auth = Snapshot("feedback", "feedback-account", "feedback-org", "one", 100);
        var source = new SessionFixture(); var cursorSource = new SessionFixture { Value = new(CursorJwt("feedback")) };
        var providers = new (IQuotaProvider Provider, QuotaRequest Request)[]
        { (new CodexQuotaProvider(new UsageClient(http)), new CodexQuotaRequest(auth, "missing-config")),
          (new ClaudeQuotaProvider(source, http), new CurrentQuotaRequest()), (new CursorQuotaProvider(cursorSource, http), new CurrentQuotaRequest()) };
        foreach (var (provider, request) in providers)
        {
            handler.Reply = (_, _) => throw offline;
            Check((await QuotaQuery.ReadAsync(provider, request, default)).Error == QuotaFailure.Connection, "Provider lost the network error classification: " + provider.Id);
            foreach (var (status, expected) in new[] { (HttpStatusCode.Unauthorized, QuotaFailure.Authentication),
                (HttpStatusCode.Forbidden, QuotaFailure.Authentication), (HttpStatusCode.InternalServerError, QuotaFailure.Service),
                (HttpStatusCode.ServiceUnavailable, QuotaFailure.Service), (HttpStatusCode.TooManyRequests, QuotaFailure.Limited) })
            {
                handler.Reply = (_, _) => Task.FromResult(Response("{}", status));
                var reading = await QuotaQuery.ReadAsync(provider, request, default);
                Check(reading.Error == expected && reading.HttpStatus == (int)status && !reading.Error.Contains(((int)status).ToString()), "HTTP category or hidden diagnostic status is wrong: " + provider.Id);
            }
            handler.Reply = (_, _) => throw new TaskCanceledException();
            Check((await QuotaQuery.ReadAsync(provider, request, default)).Error == QuotaFailure.Timeout, "Internal timeout was misreported.");
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); var cancelled = false;
            try { await QuotaQuery.ReadAsync(provider, request, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Explicit cancellation was converted to a connection notice.");
        }
        var cloud = new CloudHandler(); using var cloudHttp = new HttpClient(cloud);
        var google = new AntigravityCloudProvider(new CloudCredentials(), cloudHttp);
        cloud.Reply = _ => throw offline;
        Check((await QuotaQuery.ReadAsync(google, new CurrentQuotaRequest(), default)).Error == QuotaFailure.Connection,
            "Google fallback discarded the real offline exception and invented HTTP 503.");
        cloud.Reply = _ => new(HttpStatusCode.ServiceUnavailable);
        Check((await google.ReadAsync(new CurrentQuotaRequest(), default)).Error == QuotaFailure.Service && cloud.Refreshes == 0, "Google server error was confused with connectivity or renewed the session.");
        cloud.Reply = _ => new(HttpStatusCode.Unauthorized);
        Check((await google.ReadAsync(new CurrentQuotaRequest(), default)).Error == QuotaFailure.Authentication, "Google auth error lost its category.");
    }

    private static Task QuotaFeedbackCacheAsync() => InTemporaryStoreAsync(async paths =>
    {
        var auth = Snapshot("feedback", "feedback-account", "feedback-org", "one", 100); SeedProfiles(paths, auth, ("Personal", auth));
        var repository = Repository(paths); repository.SaveUsage("Personal", FakeUsageClient.Result() with { Resets = InventoryFixture(3) });
        var before = File.ReadAllBytes(paths.ActiveAuthPath);
        var handler = new SessionHttp { Reply = (_, _) => throw new HttpRequestException("DNS unavailable") }; using var http = new HttpClient(handler);
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new FakeAppServerClient(), new UsageClient(http), false);
        await service.RefreshAllUsageAsync();
        var reading = repository.LoadUsageCache().Accounts["Personal"];
        Check(reading.Error == QuotaFailure.Connection && reading.Weekly.UsedPercent == 20 && reading.Resets?.AvailableCount == 3
            && before.SequenceEqual(File.ReadAllBytes(paths.ActiveAuthPath)), "Offline refresh lost quotas, resets or changed credentials.");
        repository.SaveUsage("Personal", UsageRecord.FromError(QuotaFailure.Authentication) with { HttpStatus = 401 });
        Check(repository.LoadUsageCache().Accounts["Personal"].HttpStatus == 401, "Cache discarded the diagnostic error status.");

        using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var original = fixture.Handler.Reply;
        fixture.Handler.Reply = async (request, token) =>
        {
            var response = await original(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("profile")) return response;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!; body["cedar_ember"] = ClaudeResetFixture();
            response.Dispose(); return Response(body.ToJsonString());
        };
        using var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, repository.SanitizeProfileName, () => { }, (_, _) => { });
        await accounts.SetEnabledAsync(true); var digests = fixture.Digests();
        fixture.Handler.Reply = (_, _) => throw new HttpRequestException("Network unavailable");
        using (var restored = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, repository.SanitizeProfileName, () => { }, (_, _) => { }))
        {
            await restored.RefreshAsync(false);
            var current = restored.Accounts.Single();
            Check(current.Status == "stale" && current.Error == QuotaFailure.Connection && current.Quotas.Count == 4 && current.Resets?.AvailableCount == 1,
                "Unregistered Claude account lost its cached view when offline.");
        }
        Check(digests.All(p => fixture.Digests()[p.Key] == p.Value), "Connection diagnosis changed Claude authentication data.");
    });

    private static Task QuotaFeedbackNoticeAsync() => RunOnUiThreadAsync(() =>
    {
        var theme = new UiTheme(true, false, false); using var codex = new AccountRow(theme); using var claude = new CurrentProviderCard(theme);
        foreach (var message in new[] { QuotaFailure.Connection, QuotaFailure.Authentication, QuotaFailure.Service, QuotaFailure.Timeout })
        {
            codex.Update(SampleProfile("Personal", true) with { Status = "error", Error = message }, false);
            claude.Update(SampleAntigravity() with { Provider = ProviderIds.Claude, Status = "stale", Error = message });
            var notices = new[] { UiField<C.TextBlock>(codex, "_notice"), ((C.StackPanel)claude.Child).Children.OfType<C.TextBlock>().Last() };
            foreach (var notice in notices)
                Check(notice.Text == message && notice.TextWrapping == W.TextWrapping.NoWrap && notice.ToolTip is null,
                    "Error notice duplicated text, changed placement or introduced extra lines/hover.");
        }
        return Task.CompletedTask;
    });

    private static Task RenderQuotaFeedbackAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(() =>
    {
        Directory.CreateDirectory(output); using var service = CreateService(paths); var now = DateTimeOffset.UtcNow;
        var inventory = new ResetInventory(3, [new("codex_rate_limits", "Reset semanal", "Semanal de Codex", 1, now.AddDays(3).AddHours(4)),
            new("codex_rate_limits", "Reset semanal", "Semanal de Codex", 2, now.AddDays(30))], now);
        var profile = SampleProfile("Personal", true) with { Email = "personal@ejemplo.com", PlanType = "prolite", SessionLimitNotApplicable = true,
            WeeklyWindow = new(28, 10080, now.AddDays(4).AddHours(19).ToUnixTimeSeconds()), WeeklyRemainingPercent = 72, Resets = inventory, CheckedAt = now.ToString("O") };
        var claude = new ProviderUsage(ProviderIds.Claude, "demo", "Claude personal", "claude@ejemplo.com", "Pro", now.ToString("O"), "ok", null,
            [new("five_hour", "Claude", "Sesión · 5 h", 14, 300, now.AddHours(2).AddMinutes(14).ToUnixTimeSeconds()),
             new("seven_day", "Claude", "Semanal", 42, 10080, now.AddDays(3).AddHours(6).ToUnixTimeSeconds())],
            Resets: new(1, [new("full", "Reset completo", "Sesión de 5 h y semanal", 1, now.AddDays(30))], now));
        var state = SampleState() with { ActiveProfile = profile, Profiles = [profile], OtherAccounts = [claude] };
        using var form = new WidgetForm(service, new UiTheme(true, false, false, M.Color.FromRgb(70, 74, 53), M.Color.FromRgb(137, 155, 93)), false);
        form.Visual.Topmost = false; form.Visual.Left = form.Visual.Top = -20000; form.Visual.Height = 650;
        form.ApplyState(state); form.SetHistoryPreview(ProviderHistory.Merge(new Dictionary<string, TokenHistorySnapshot>
        { [ProviderIds.Codex] = SampleHistory(), [ProviderIds.Claude] = new([new(DateTime.Today.ToString("yyyy-MM-dd"), new Dictionary<string, long> { ["Claude"] = 220000 })], false, now.ToString("O"), null) }));
        UiClick(UiField<C.Button>(UiField<ProviderOverview>(form, "_overview"), "_quotas"));
        form.Visual.Show(); form.Visual.UpdateLayout(); SaveResetDemo(form.Visual, Path.Combine(output, "caducidad-proxima.png"));
        var empty = new ResetInventory(0, [], now);
        form.Hide();
        form.ApplyState(state with { Profiles = [profile with { Resets = empty }], OtherAccounts = [claude with { Resets = empty }] });
        form.Visual.Height = 604; form.Visual.Show(); form.Visual.UpdateLayout(); SaveResetDemo(form.Visual, Path.Combine(output, "sin-resets.png"));
        var offline = profile with { Status = "error", Error = QuotaFailure.Connection, Resets = empty };
        var auth = profile with { Name = "Trabajo", Email = "trabajo@ejemplo.com", IsActive = false, Status = "error", Error = QuotaFailure.Authentication, Resets = empty };
        form.Hide();
        form.ApplyState(state with { ActiveProfile = offline, Profiles = [offline, auth], OtherAccounts = [claude with { Status = "stale", Error = QuotaFailure.Service, Resets = empty }] });
        form.Visual.Height = 780; form.Visual.Show(); form.Visual.UpdateLayout(); SaveResetDemo(form.Visual, Path.Combine(output, "errores-de-consulta.png"));
        Console.WriteLine("Real WPF previews: imminent expiry, zero resets, connection/authentication/service failures. Synthetic data only.");
        return Task.CompletedTask;
    }));
}
