using AICountsManager;
using System.Text.Json;
namespace AICountsManager.Tests;
internal static partial class Program
{
    private static Task MeasureManagedClaudeIdleAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        paths.EnsureDirectories(); using var fixture = new ClaudeFixtureStore(paths); fixture.Login(DesktopAccount);
        var accounts = new ClaudeAccounts(paths.AppHome, fixture.Archive, fixture.Http, Repository(paths).SanitizeProfileName, () => {}, (_,_) => {});
        await accounts.SetEnabledAsync(true); await accounts.BeginAsync("Personal Claude"); fixture.Login(ClaudeSecond); await accounts.RefreshAsync(true);
        using var service = new AICountsManagerService(paths, new FakeProcessController(false), new NoPollingAppServer(), new FakeUsageClient(), false, claudeAccounts: accounts);
        using var form = new WidgetForm(service, new UiTheme(true,false,false), false);
        form.ApplyState(service.BuildDashboardState()); form.Visual.Left = -20000; form.Visual.Top = -20000;
        form.Visual.ShowInTaskbar = false; form.Visual.Topmost = false; form.Visual.Show(); await Task.Delay(3000); form.Hide(); await Task.Delay(2000);
        var calls = fixture.Requests;
        var resources = await SampleResourcesAsync("Panel oculto; gestor de dos cuentas Claude y observadores activos; datos ficticios", false);
        Check(fixture.Requests == calls, "Idle account management generated network requests.");
        var result = new { IdleRequests = fixture.Requests - calls, Accounts = accounts.State.Accounts.Count, Resources = resources };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }, 60));
}
