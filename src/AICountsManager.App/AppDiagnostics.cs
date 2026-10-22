using System.Text;

namespace AICountsManager;

// Event-only, bounded local diagnostics. No polling, worker or open file handle.
// Exception messages/data can contain HTTP bodies or credentials: record types and
// stacks only. Neither account names nor token values are written here.
internal sealed class AppDiagnostics(string logsDirectory)
{
    private const long MaxBytes = 128 * 1024;
    private static readonly object Sync = new();
    internal string FilePath => Path.Combine(logsDirectory, "diagnostics.log");

    public void Write(string operation, Exception? error = null)
    {
        try
        {
            var entry = new StringBuilder().Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(" pid=").Append(Environment.ProcessId).Append(' ').AppendLine(operation);
            for (var current = error; current is not null; current = current.InnerException)
            {
                entry.Append(current.GetType().FullName).Append(" HResult=0x")
                    .AppendLine(current.HResult.ToString("X8"));
                entry.AppendLine(current.StackTrace);
                if (entry.Length > 16000) break;
            }
            if (entry.Length > 16000) entry.Length = 16000;
            var text = entry.AppendLine().ToString();
            lock (Sync)
            {
                Directory.CreateDirectory(logsDirectory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length + Encoding.UTF8.GetByteCount(text) > MaxBytes)
                    File.Move(FilePath, FilePath + ".previous", overwrite: true);
                File.AppendAllText(FilePath, text);
            }
        }
        catch { /* Diagnostics must never replace the original failure. */ }
    }
}
