using System.Security.AccessControl;
using System.Security.Principal;

namespace CodexManager;

internal static class AtomicFile
{
    public static void WriteBytes(string destinationPath, byte[] content)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("La ruta de destino no tiene directorio.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            TryRestrictFile(temporaryPath);
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }

            TryRestrictFile(destinationPath);
            var written = File.ReadAllBytes(destinationPath);
            if (!string.Equals(AuthHash.Bytes(written), AuthHash.Bytes(content), StringComparison.Ordinal))
            {
                throw new IOException($"La verificacion posterior de '{destinationPath}' no coincide.");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    public static void WriteText(string destinationPath, string content) =>
        WriteBytes(destinationPath, System.Text.Encoding.UTF8.GetBytes(content));

    public static void TryRestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var identity = WindowsIdentity.GetCurrent().User;
            if (identity is null)
            {
                return;
            }

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        }
        catch
        {
            // Some managed/corporate volumes reject ACL updates. Atomicity must still work.
        }
    }

    public static void TryRestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var identity = WindowsIdentity.GetCurrent().User;
            if (identity is null)
            {
                return;
            }

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
        }
        catch
        {
            // Best effort: auth.json itself may live on a volume without Windows ACL support.
        }
    }
}

internal sealed class GlobalAuthSwitchLock : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _acquired;

    public GlobalAuthSwitchLock(string codexHome)
    {
        var suffix = AuthHash.Short(AuthHash.Text(Path.GetFullPath(codexHome).ToUpperInvariant()));
        _semaphore = new Semaphore(1, 1, $@"Local\CodexManager.AuthSwitch.{suffix}");
    }

    public async Task AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_acquired)
        {
            return;
        }

        _acquired = await Task.Run(
            () => WaitHandle.WaitAny([_semaphore, cancellationToken.WaitHandle], timeout) switch
            {
                0 => true,
                1 => throw new OperationCanceledException(cancellationToken),
                _ => false,
            },
            CancellationToken.None);

        if (!_acquired)
        {
            throw new TimeoutException("Otra instancia de CodexManager esta cambiando las credenciales.");
        }
    }

    public void Dispose()
    {
        if (_acquired)
        {
            _semaphore.Release();
            _acquired = false;
        }
        _semaphore.Dispose();
    }
}
