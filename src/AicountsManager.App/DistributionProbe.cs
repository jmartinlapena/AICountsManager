using System.Runtime.InteropServices;
using System.Text.Json;

namespace AicountsManager;

internal static class DistributionProbe
{
    // Runs before credentials, timers, mutexes or tray creation. Used to validate packaged dependencies.
    public static void Run(string output)
    {
        ApplicationConfiguration.Initialize();
        using var stream = typeof(DistributionProbe).Assembly.GetManifestResourceStream("AicountsManager.WidgetWindow.xaml") ?? throw new IOException("Falta la interfaz.");
        var window = (System.Windows.Window)System.Windows.Markup.XamlReader.Load(stream);
        using var icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico"));
        var sqlite = NativeLibrary.Load("winsqlite3.dll"); NativeLibrary.Free(sqlite);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Product = "AicountsManager", Version = typeof(DistributionProbe).Assembly.GetName().Version?.ToString(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription, UiLoaded = window.Width > 0, IconLoaded = icon.Width > 0, SqliteLoaded = true }));
        window.Close();
    }
}
