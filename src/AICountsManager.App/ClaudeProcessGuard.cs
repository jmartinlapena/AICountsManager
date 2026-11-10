using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AICountsManager;

internal static class ClaudeProcessGuard
{
    // Called for an account mutation/pending login only, never by the idle quota poll.
    internal static bool IsRunning()
    {
        var clients = Process.GetProcessesByName("claude");
        var native = clients.Length != 0; foreach (var p in clients) p.Dispose();
        if (native) return true;
        var nodes = Process.GetProcessesByName("node");
        var hasNodes = nodes.Length != 0; foreach (var p in nodes) p.Dispose();
        return hasNodes && LegacyProcessIds().Count != 0;
    }

    // Select only IDs of the legacy npm client. No command lines are returned or logged.
    internal static IReadOnlyList<int> LegacyProcessIds()
    {
        var nodes = Process.GetProcessesByName("node");
        var hasNodes = nodes.Length != 0; foreach (var node in nodes) node.Dispose();
        if (!hasNodes) return [];
        var ids = new List<int>();
        object? locator = null, services = null, results = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            services = ((dynamic)locator!).ConnectServer(".", "root/cimv2");
            results = ((dynamic)services).ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='node.exe' AND CommandLine LIKE '%@anthropic-ai%claude-code%'");
            foreach (var result in (System.Collections.IEnumerable)results)
            {
                try { ids.Add(Convert.ToInt32(((dynamic)result).ProcessId)); }
                finally { if (Marshal.IsComObject(result)) Marshal.ReleaseComObject(result); }
            }
            return ids;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { throw new InvalidOperationException("No se pudo comprobar si Claude Code sigue abierto. No se ha cambiado su sesión."); }
        finally
        {
            foreach (var value in new[] { results, services, locator }) if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }
}
