using System.Text.Json;

namespace AICountsManager;

// ContextId is the Codex user ID or the Claude organization UUID, respectively.
internal sealed record OrganizationAccount(string AccountId, string ContextId);
internal sealed record OrganizationFile(string Path, byte[]? Before, byte[]? After);
internal interface ILocalOrganizationAdapter
{
    string Root { get; }
    string Provider { get; }
    bool SupportsPath(string path);
    IReadOnlyList<OrganizationFile> Prepare(OrganizationAccount source, OrganizationAccount target);
}
internal sealed record OrganizationJournal(string Id, string Provider, string Root, string SourceIdentity,
    string TargetIdentity, string Stage, IReadOnlyList<OrganizationFile> Files);

// Called only within the provider's existing credential lock. No watcher, timer or background scan.
internal sealed class LocalOrganizationTransfer(ILocalOrganizationAdapter adapter, string transactions, string backups, Action requireClosed)
{
    private ILocalOrganizationAdapter Adapter => adapter;
    private void RequireClosed() => requireClosed();
    private string JournalPath => Path.Combine(transactions, "organization-" + adapter.Provider + ".bin");
    private string BackupPath => Path.Combine(backups, "organization-" + adapter.Provider + "-last.bin");
    internal bool HasRecovery => File.Exists(JournalPath);
    internal Action<int>? AfterWriteForTesting { get; set; }
    internal OrganizationChange Prepare(string sourceIdentity, string targetIdentity, OrganizationAccount source, OrganizationAccount target)
    {
        requireClosed();
        if (HasRecovery) throw new InvalidOperationException("Hay una recuperación de organización pendiente. No se ha cambiado la cuenta.");
        var files = adapter.Prepare(source, target);
        var journal = new OrganizationJournal(Guid.NewGuid().ToString("N"), adapter.Provider, Path.GetFullPath(adapter.Root), sourceIdentity, targetIdentity, "prepared", files);
        Validate(journal); requireClosed();
        return new OrganizationChange(this, journal);
    }
    private void Validate(OrganizationJournal journal)
    {
        if (journal.Provider != adapter.Provider || !string.Equals(Path.GetFullPath(adapter.Root), journal.Root, StringComparison.OrdinalIgnoreCase)
            || journal.Stage is not ("prepared" or "applied") || journal.Files.Count > 2000
            || journal.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count
            || journal.Files.Sum(f => (long)(f.Before?.Length ?? 0) + (f.After?.Length ?? 0)) > 48L * 1024 * 1024)
            throw new InvalidDataException("El respaldo de organización no corresponde a esta instalación o supera el límite admitido.");
        foreach (var file in journal.Files)
        {
            OrganizationFiles.RequirePath(adapter.Root, file.Path);
            if (!adapter.SupportsPath(file.Path)) throw new InvalidDataException("Archivo ajeno a la organización local del proveedor.");
        }
    }
    internal void Recover(string? currentIdentity)
    {
        if (!HasRecovery) return;
        requireClosed();
        var journal = ClaudeSessionArchive.LoadProtected<OrganizationJournal>(JournalPath); Validate(journal);
        if (currentIdentity == journal.TargetIdentity && journal.Stage == "applied")
        {
            // The target may have been used after a crash. Its newer edits remain authoritative.
            Archive(); return;
        }
        if (currentIdentity != journal.SourceIdentity)
            throw new InvalidOperationException("La identidad actual no coincide con el cambio interrumpido. Se conservan los respaldos sin sobrescribir la organización.");
        Restore(journal); Archive();
    }
    private void Restore(OrganizationJournal journal)
    {
        requireClosed();
        foreach (var file in journal.Files)
        {
            var current = OrganizationFiles.Read(file.Path);
            if (!OrganizationFiles.Equal(current, file.Before) && !OrganizationFiles.Equal(current, file.After))
                throw new IOException("La organización cambió fuera de la operación. Se conserva el respaldo para evitar perder esos cambios.");
        }
        foreach (var file in journal.Files) { requireClosed(); OrganizationFiles.Write(adapter.Root, file.Path, file.Before); }
        foreach (var file in journal.Files) if (!OrganizationFiles.Equal(OrganizationFiles.Read(file.Path), file.Before)) throw new IOException("La restauración de organización no coincide.");
    }
    private void Archive()
    {
        if (!File.Exists(JournalPath)) return;
        Directory.CreateDirectory(backups); AtomicFile.TryRestrictDirectory(backups);
        File.Move(JournalPath, BackupPath, overwrite: true);
    }

    internal sealed class OrganizationChange(LocalOrganizationTransfer owner, OrganizationJournal journal)
    {
        private bool _begun, _rolledBack;
        internal bool HasChanges => journal.Files.Count != 0;
        internal void Apply()
        {
            if (!HasChanges) return;
            owner.RequireClosed();
            foreach (var file in journal.Files)
                if (!OrganizationFiles.Equal(OrganizationFiles.Read(file.Path), file.Before)) throw new IOException("La organización cambió mientras se preparaba la cuenta.");
            ClaudeSessionArchive.SaveProtected(owner.JournalPath, journal); _begun = true;
            var index = 0;
            foreach (var file in journal.Files)
            {
                owner.RequireClosed(); OrganizationFiles.Write(owner.Adapter.Root, file.Path, file.After);
                owner.AfterWriteForTesting?.Invoke(++index);
            }
            Verify(); journal = journal with { Stage = "applied" };
            ClaudeSessionArchive.SaveProtected(owner.JournalPath, journal);
        }
        internal void Verify()
        {
            if (!HasChanges) return;
            owner.RequireClosed();
            foreach (var file in journal.Files)
                if (!OrganizationFiles.Equal(OrganizationFiles.Read(file.Path), file.After)) throw new IOException("La verificación de la organización no coincide.");
        }
        internal void Commit() { if (HasChanges) { Verify(); owner.Archive(); } }
        internal void Rollback()
        {
            if (!_begun || _rolledBack) return;
            owner.Restore(journal); owner.Archive(); _rolledBack = true;
        }
    }
}

internal static class OrganizationFiles
{
    internal static byte[]? Read(string path) => ClaudeSessionArchive.ReadFile(path, 16 * 1024 * 1024);
    internal static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    internal static void RequirePath(string root, string path)
    {
        var canonical = Path.GetFullPath(path); var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta de organización fuera del directorio del proveedor.");
        for (var p = canonical; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("No se pueden sustituir enlaces de organización.");
    }
    internal static void Write(string root, string path, byte[]? bytes)
    {
        RequirePath(root, path);
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); }
        else AtomicFile.WriteBytes(path, bytes);
    }
}
