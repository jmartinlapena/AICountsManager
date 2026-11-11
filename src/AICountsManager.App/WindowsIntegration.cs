using System.Diagnostics;
using System.Reflection;

namespace AICountsManager;

internal sealed class WindowsIntegration(AppPaths paths)
{
    private readonly AppPaths _paths = paths;
    public bool StartupShortcutExists() => File.Exists(_paths.StartupShortcutPath);

    public void SetStartupEnabled(bool enabled)
    {
        if (enabled)
        {
            CreateStartupShortcut();
        }
        else if (File.Exists(_paths.StartupShortcutPath))
        {
            File.Delete(_paths.StartupShortcutPath);
        }
    }

    public void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void CreateStartupShortcut()
    {
        Directory.CreateDirectory(_paths.StartupFolder);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("No se pudo resolver el ejecutable.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        var hostedByDotnet = string.Equals(
            Path.GetFileNameWithoutExtension(executable),
            "dotnet",
            StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssembly);
        var arguments = hostedByDotnet ? $"\"{entryAssembly}\"" : string.Empty;
        var command = $"""
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut('{_paths.StartupShortcutPath.Replace("'", "''")}')
            $shortcut.TargetPath = '{executable.Replace("'", "''")}'
            $shortcut.Arguments = '{arguments.Replace("'", "''")}'
            $shortcut.WorkingDirectory = '{AppContext.BaseDirectory.Replace("'", "''")}'
            $shortcut.IconLocation = '{Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico").Replace("'", "''")},0'
            $shortcut.Description = 'AICountsManager'
            $shortcut.WindowStyle = 7
            $shortcut.Save()
            """;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                command,
            },
        });
        if (process is null) throw new IOException("No se pudo actualizar el inicio con Windows.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Windows no pudo guardar el acceso directo de inicio.");
    }

}
