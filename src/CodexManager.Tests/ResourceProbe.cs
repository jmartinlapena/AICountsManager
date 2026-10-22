using System.Diagnostics;
using System.Text.Json;
using CodexManager;

namespace CodexManager.Tests;

internal static partial class Program
{
    private static Task MeasureUiResourcesAsync(string output) => InTemporaryStoreAsync(paths => RunOnUiThreadAsync(async () =>
    {
        using var service = CreateService(paths);
        using var form = new WidgetForm(service, startClock: false);
        form.Visual.Title = "Codex manager · Medición con datos ficticios";
        form.Visual.ShowInTaskbar = true; form.Visual.Topmost = false;
        form.Visual.Height = 710;
        form.Visual.Left = Math.Max(0, System.Windows.SystemParameters.WorkArea.Right - 400);
        form.Visual.Top = Math.Max(0, System.Windows.SystemParameters.WorkArea.Bottom - 730);
        form.ApplyState(SampleState()); form.SetHistoryPreview(SampleHistory()); form.Visual.Show();
        await Task.Delay(5000);
        var samples = new List<object> { await SampleResourcesAsync("Panel visible; datos ficticios", form.AcrylicEnabled) };
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
