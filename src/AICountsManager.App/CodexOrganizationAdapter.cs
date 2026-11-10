using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICountsManager;

internal sealed class CodexOrganizationAdapter(string codexHome) : ILocalOrganizationAdapter
{
    public string Root => codexHome;
    public string Provider => "codex";
    public bool SupportsPath(string path) => string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(Root, ".codex-global-state.json")), StringComparison.OrdinalIgnoreCase);
    // Only local organization and its migration references. Account atoms, credentials and permissions remain separate.
    internal static readonly string[] Fields = ["local-projects", "project-order", "selected-project", "project-appearances",
        "sidebar-project-thread-orders", "thread-project-assignments", "thread-project-membership-host-ids",
        "app-server-project-id-by-legacy-project-id-by-host", "app-server-projects-migration-by-host"];
    internal static string Scope(OrganizationAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AccountId) || string.IsNullOrWhiteSpace(account.ContextId))
            throw new InvalidDataException("No se puede determinar el ámbito de organización de la cuenta de Codex.");
        return AuthHash.Text(JsonSerializer.Serialize(new[] { account.ContextId, account.AccountId })).ToLowerInvariant();
    }
    public IReadOnlyList<OrganizationFile> Prepare(OrganizationAccount source, OrganizationAccount target)
    {
        var path = Path.Combine(Root, ".codex-global-state.json"); OrganizationFiles.RequirePath(Root, path);
        var before = OrganizationFiles.Read(path); if (before is null) return [];
        var root = JsonNode.Parse(before) as JsonObject ?? throw new InvalidDataException("La organización de Codex no es un objeto JSON.");
        if (root["electron-account-renderer-state-v1"] is null) return []; // Older clients already share the global organization.
        if (root["electron-account-renderer-state-v1"] is not JsonObject state
            || state["inactive"] is not JsonObject inactive || state["owner"] is not (null or JsonValue))
            throw new InvalidDataException("Formato de organización de Codex desconocido. No se ha cambiado la cuenta.");
        if (state["owner"] is JsonValue owner && (!owner.TryGetValue<string>(out var ownerId) || ownerId.Length != 64 || ownerId.Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidDataException("El propietario de la organización de Codex no tiene un formato conocido.");
        var key = Scope(target); _ = Scope(source);
        var snapshot = inactive[key] as JsonObject;
        if (inactive[key] is not null && snapshot is null) throw new InvalidDataException("El perfil de organización de destino no es válido.");
        snapshot ??= new JsonObject { ["atoms"] = new JsonObject(), ["globals"] = new JsonObject() };
        if (snapshot["atoms"] is not JsonObject || snapshot["globals"] is not JsonObject globals)
            throw new InvalidDataException("El perfil de organización de destino no es válido.");
        foreach (var field in Fields)
        {
            var value = root[field];
            if (value is not null && (field == "project-order" ? value is not JsonArray : value is not JsonObject))
                throw new InvalidDataException("Un índice de organización de Codex tiene un formato desconocido.");
            if (field == "thread-project-assignments") ReplaceLocalAssignments(globals, value as JsonObject);
            else if (field == "selected-project" && value is JsonObject selected && selected["type"]?.GetValue<string>() is not ("local" or null))
            {
                // Keep a target cloud selection, but never leave a selection of a removed local project.
                if (globals[field] is JsonObject previous && previous["type"]?.GetValue<string>() == "local") globals.Remove(field);
            }
            else { globals.Remove(field); if (value is not null) globals[field] = value.DeepClone(); }
        }
        // Do not change the owner: Codex itself saves the outgoing account and loads the prepared target scope.
        if (inactive[key] is null) inactive[key] = snapshot;
        var after = JsonSerializer.SerializeToUtf8Bytes(root);
        return [new(path, before, after)];
    }
    private static void ReplaceLocalAssignments(JsonObject target, JsonObject? source)
    {
        if (target["thread-project-assignments"] is not null && target["thread-project-assignments"] is not JsonObject)
            throw new InvalidDataException("Índice de asociaciones de destino no válido.");
        var result = target["thread-project-assignments"]?.DeepClone().AsObject() ?? new JsonObject();
        foreach (var p in result.ToArray()) if (p.Value?["projectKind"]?.GetValue<string>() == "local") result.Remove(p.Key);
        if (source is not null) foreach (var p in source)
            if (p.Value is JsonObject item && item["projectKind"]?.GetValue<string>() == "local") result[p.Key] = item.DeepClone();
        target["thread-project-assignments"] = result;
    }
}
