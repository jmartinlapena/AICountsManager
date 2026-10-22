using System.Diagnostics;
using System.Text.Json;
using AICountsManager;

namespace AICountsManager.Tests;

internal static partial class Program
{
    private static Task MeasureUiResourcesAsync(string output, bool providers = false) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        using var service = CreateService(paths);
        using var form = new WidgetForm(service, startClock: false);
        form.Visual.Title = "AICountsManager · Medición con datos ficticios";
        ((System.Windows.Controls.TextBlock)form.Visual.FindName("TitleLabel")).Text = "Prueba · datos ficticios"; form.Visual.ShowInTaskbar = false; form.Visual.Topmost = false;
        form.Visual.Height = 710;
        form.Visual.Left = -20000;
        form.Visual.Top = -20000;
        form.ApplyState(providers ? SampleState() with { OtherAccounts = [SampleAntigravity(),
            new(ProviderIds.Claude, "preview", "Claude Code", "claude@example.test", "Max", DateTimeOffset.UtcNow.ToString("O"), "ok", null, ClaudeQuotaProvider.Parse(Element(ClaudeFixture()))),
            new(ProviderIds.Cursor, "preview", "Cursor", "cursor@example.test", "Pro", DateTimeOffset.UtcNow.ToString("O"), "ok", null, CursorQuotaProvider.Parse(Element(CursorFixture())))] } : SampleState());
        form.SetHistoryPreview(providers ? ProviderHistory.Merge(SampleHistory(), SampleAntigravityHistory()) : SampleHistory()); form.Visual.Show();
        if (providers) UiClick(UiField<System.Windows.Controls.Button>(UiField<ProviderOverview>(form, "_overview"), "_today"));
        await Task.Delay(5000);
        var samples = new List<object> { await SampleResourcesAsync("Panel creado fuera de pantalla; datos ficticios de cuatro proveedores", form.AcrylicEnabled) };
        form.Hide(); await Task.Delay(2000);
        samples.Add(await SampleResourcesAsync("Panel oculto después de abrir; datos ficticios", form.AcrylicEnabled));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(samples));
    }, 60));

    private static async Task<object> SampleResourcesAsync(string scenario, bool acrylic)
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var cpu = process.TotalProcessorTime.TotalMilliseconds; var clock = Stopwatch.StartNew();
        await Task.Delay(10000); process.Refresh();
        var delta = process.TotalProcessorTime.TotalMilliseconds - cpu;
        return new
        {
            Scenario = scenario, ProcessId = process.Id, ProductionProcess = false, Acrylic = acrylic,
            Seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), CpuMilliseconds = Math.Round(delta, 2),
            CpuPercentMachine = Math.Round(delta / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, 3),
            PrivateMemoryMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1),
            WorkingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 1), Threads = process.Threads.Count, Handles = process.HandleCount,
        };
    }
}
