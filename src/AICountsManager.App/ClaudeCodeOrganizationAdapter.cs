using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICountsManager;

internal sealed class ClaudeCodeOrganizationAdapter(string desktopHome) : ILocalOrganizationAdapter
{
    public string Root => Path.Combine(desktopHome, "claude-code-sessions");
    public string Provider => "claude";
    public bool SupportsPath(string path)
    {
        var parts = Path.GetRelativePath(Root, path).Split(Path.DirectorySeparatorChar);
        return parts.Length == 3 && Guid.TryParse(parts[0], out _) && Guid.TryParse(parts[1], out _)
            && parts[2].StartsWith("local_", StringComparison.Ordinal) && parts[2].EndsWith(".json", StringComparison.Ordinal)
            && Guid.TryParse(parts[2][6..^5], out _);
    }
    private static readonly string[] Fields = ["cliSessionId", "cwd", "originCwd", "gitAnchorsFolderRealpath", "title", "titleSource",
        "createdAt", "lastFocusedAt", "lastActivityAt", "lastTurnEdgeAt", "isArchived", "isDeleted", "deletedAt", "isStarred"];
    private string DirectoryFor(OrganizationAccount account)
    {
        if (!Guid.TryParse(account.AccountId, out var id) || !Guid.TryParse(account.ContextId, out var org))
            throw new InvalidDataException("Ámbito de sesión de Claude no válido.");
        return Path.Combine(Root, id.ToString(), org.ToString());
    }
    private Dictionary<string, (string Path, JsonObject Data, byte[] Bytes)> ReadIndex(string directory)
    {
        var result = new Dictionary<string, (string, JsonObject, byte[])>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return result;
        OrganizationFiles.RequirePath(Root, directory);
        long totalBytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "local_*.json", SearchOption.TopDirectoryOnly))
        {
            if (result.Count >= 2000) throw new InvalidDataException("El índice Code supera el tamaño admitido.");
            OrganizationFiles.RequirePath(Root, file);
            var bytes = OrganizationFiles.Read(file)!;
            if ((totalBytes += bytes.Length) > 24 * 1024 * 1024) throw new InvalidDataException("El índice Code supera el tamaño admitido.");
            var data = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("Índice de Code no válido.");
            var session = data["sessionId"]?.GetValue<string>(); var cli = data["cliSessionId"]?.GetValue<string>();
            if (session is null || !session.StartsWith("local_", StringComparison.Ordinal) || !Guid.TryParse(session[6..], out _)
                || !string.Equals(Path.GetFileName(file), session + ".json", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(cli, out var cliId)
                || data["cwd"] is not JsonValue cwd || !cwd.TryGetValue<string>(out var directoryName) || string.IsNullOrWhiteSpace(directoryName))
                throw new InvalidDataException("Índice de Code con identificadores desconocidos. No se ha cambiado la cuenta.");
            if (!result.TryAdd(cliId.ToString(), (file, data, bytes))) throw new InvalidDataException("Hay índices Code duplicados para una conversación. No se ha cambiado la cuenta.");
        }
        return result;
    }
    public IReadOnlyList<OrganizationFile> Prepare(OrganizationAccount source, OrganizationAccount target)
    {
        var sourcePath = DirectoryFor(source); var targetPath = DirectoryFor(target);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase)) return [];
        // Missing source directory means unobserved organization, not an authoritative empty account.
        if (!Directory.Exists(sourcePath)) return [];
        var from = ReadIndex(sourcePath); var to = ReadIndex(targetPath); var changes = new List<OrganizationFile>();
        foreach (var (cliId, entry) in from)
        {
            to.TryGetValue(cliId, out var existing);
            var record = existing.Data?.DeepClone().AsObject() ?? new JsonObject { ["sessionId"] = entry.Data["sessionId"]!.DeepClone() };
            foreach (var key in Fields) { record.Remove(key); if (entry.Data[key] is { } value) record[key] = value.DeepClone(); }
            var destination = existing.Path ?? Path.Combine(targetPath, entry.Data["sessionId"]!.GetValue<string>() + ".json");
            OrganizationFiles.RequirePath(Root, destination);
            if (existing.Path is null && File.Exists(destination)) throw new InvalidDataException("El identificador de sesión Code colisiona en la cuenta de destino.");
            changes.Add(new(destination, existing.Bytes, JsonSerializer.SerializeToUtf8Bytes(record)));
        }
        // Replace local index membership. Transcript files and non-session account files are never removed.
        foreach (var (cliId, entry) in to) if (!from.ContainsKey(cliId)) changes.Add(new(entry.Path, entry.Bytes, null));
        return changes;
    }
}
