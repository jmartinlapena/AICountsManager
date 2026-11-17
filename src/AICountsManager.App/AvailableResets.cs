using System.Globalization;
using System.Text.Json;

namespace AICountsManager;

// Display metadata only. Provider grant identifiers are redemption handles and must not be persisted.
internal sealed record AvailableReset(string Kind, string Label, string Scope, int Count,
    DateTimeOffset? ExpiresAtUtc, bool ExpiryKnown = true, bool? UsableNow = null);

internal sealed record ResetInventory(int? AvailableCount, IReadOnlyList<AvailableReset> Items,
    DateTimeOffset CheckedAtUtc, string Status = "ok", bool DetailsComplete = true,
    int? HttpStatus = null, int? RetryAfterSeconds = null)
{
    public static ResetInventory Unknown(DateTimeOffset now, int? status = null, int? retry = null) =>
        new(null, [], now, "unknown", false, status, retry);
    public ResetInventory Stale() => this with { Status = "stale" };
    public IReadOnlyList<AvailableReset> ActiveItems(DateTimeOffset now) => (Items ?? [])
        .Where(i => i is not null && i.Count is > 0 and <= 10000 && (i.ExpiresAtUtc is null || i.ExpiresAtUtc > now))
        .OrderBy(i => i.ExpiresAtUtc ?? DateTimeOffset.MaxValue).Take(200).ToArray();
    public int? CountAt(DateTimeOffset now) => AvailableCount is >= 0 and <= 10000
        ? Math.Max(0, AvailableCount.Value - (Items ?? []).Where(i => i is not null && i.Count is > 0 and <= 10000
            && i.ExpiresAtUtc <= now).Sum(i => i.Count)) : null;
    public static ResetInventory? Merge(ResetInventory? reading, ResetInventory? previous) =>
        reading is null || reading.Status == "unknown"
            ? previous is not null ? previous.Stale() with { HttpStatus = reading?.HttpStatus, RetryAfterSeconds = reading?.RetryAfterSeconds } : reading
            : reading;
    public static ResetInventory? ReadCache(JsonElement value)
    {
        try
        {
            var result = value.Deserialize<ResetInventory>();
            return result is { Items.Count: <= 200 } && (result.AvailableCount is null or >= 0 and <= 10000)
                && result.Status is "ok" or "stale" or "unknown" or "unsupported" && result.Items.All(i => i is not null && i.Count is > 0 and <= 10000
                && i.Label is { Length: <= 120 } && i.Scope is { Length: <= 200 }) ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }
}

internal static class ResetInventoryParser
{
    private static int? Count(JsonElement value) => value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var n) && n is >= 0 and <= 10000 ? n : null;
    private static bool IsFalse(JsonElement value) => value.ValueKind == JsonValueKind.False;
    private static string? Text(JsonElement root, string field, int length = 120) => ProviderJson.Text(root, field) is { } s
        && s.Length <= length && !s.Any(char.IsControl) ? s : null;
    private static bool Bound(JsonElement root, string field, out DateTimeOffset? date, out bool known)
    {
        var value = ProviderJson.Get(root, field); date = null; known = value.ValueKind != JsonValueKind.Undefined;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        { date = DateTimeOffset.FromUnixTimeSeconds(seconds); return true; }
        if (value.ValueKind != JsonValueKind.String) return false;
        var raw = value.GetString()!;
        // Never interpret a locale-dependent or offset-less date as an absolute expiration.
        if (!System.Text.RegularExpressions.Regex.IsMatch(raw, @"T.*(?:Z|[+-]\d{2}:\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed.ToUniversalTime(); return true;
    }
    public static ResetInventory Codex(JsonElement root, bool weeklyOnly, DateTimeOffset now, bool summary = false)
    {
        if (summary) root = ProviderJson.Get(root, "rate_limit_reset_credits");
        if (Count(ProviderJson.Get(root, "available_count")) is not { } available) return ResetInventory.Unknown(now);
        var credits = ProviderJson.Get(root, "credits"); var items = new List<AvailableReset>();
        var complete = credits.ValueKind == JsonValueKind.Array && credits.GetArrayLength() <= 200;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (complete)
            foreach (var credit in credits.EnumerateArray())
            {
                if (Text(credit, "status") != "available" || IsFalse(ProviderJson.Get(credit, "is_supported_by_plan"))) continue;
                if (Text(credit, "id", 200) is { } id && !ids.Add(id)) continue;
                if (!Bound(credit, "expires_at", out var expiry, out var known)) { complete = false; continue; }
                // The authoritative count is corrected below for expirations that elapsed since the response.
                var kind = Text(credit, "reset_type") ?? "unknown";
                var label = kind == "codex_rate_limits" ? weeklyOnly ? "Reset semanal" : "Reset completo"
                    : Text(credit, "title") ?? "Reset disponible";
                items.Add(new(kind, label, kind == "codex_rate_limits" ? weeklyOnly ? "Semanal de Codex" : "Sesión de 5 h y semanal" : "Codex", 1, expiry, known));
            }
        complete &= items.Sum(i => i.Count) == available;
        if (items.Count > available) return ResetInventory.Unknown(now);
        return new(available, items, now, DetailsComplete: complete || available == 0);
    }
    public static ResetInventory Claude(JsonElement root, DateTimeOffset now)
    {
        var block = ProviderJson.Get(root, "cedar_ember");
        if (ProviderJson.Get(block, "eligible").ValueKind != JsonValueKind.True)
            return Text(block, "ineligible_reason") is "plan" or "subscription" ? new(0, [], now, "unsupported") : ResetInventory.Unknown(now);
        var grants = ProviderJson.Get(block, "grants");
        if (grants.ValueKind != JsonValueKind.Array || grants.GetArrayLength() > 200) return ResetInventory.Unknown(now);
        var items = new List<AvailableReset>(); var complete = true; var total = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants.EnumerateArray())
        {
            var remaining = Count(ProviderJson.Get(grant, "resets_left"));
            var maximum = Count(ProviderJson.Get(grant, "resets_total"));
            var paused = ProviderJson.Get(grant, "paused");
            if (remaining is null || maximum < remaining || paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !Bound(grant, "starts_at", out var starts, out _) || !Bound(grant, "ends_at", out var ends, out var known))
            { complete = false; continue; }
            if (remaining == 0 || !IsFalse(paused) || IsFalse(ProviderJson.Get(grant, "started")) || starts > now || ends <= now) continue;
            if (Text(grant, "id", 200) is { } id && !ids.Add(id)) continue;
            if (total + remaining > 10000) return ResetInventory.Unknown(now);
            var clears = ProviderJson.Get(grant, "clears");
            var limits = clears.ValueKind == JsonValueKind.Array ? clears.EnumerateArray().Take(24)
                .Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).ToArray() : [];
            var session = limits.Contains("five_hour"); var weekly = limits.Contains("seven_day");
            var label = session && weekly ? "Reset completo" : weekly ? "Reset semanal" : session ? "Reset de sesión" : "Reset de modelo";
            var scope = session && weekly ? "Sesión de 5 h y semanal" : weekly ? "Semanal de Claude" : session ? "Sesión de 5 h"
                : Text(grant, "label") ?? "Límite específico de Claude";
            if (!weekly && ProviderJson.Get(grant, "gives_back_weekly").ValueKind == JsonValueKind.True) scope += " · recupera cuota semanal";
            var usable = ProviderJson.Get(grant, "usable_now");
            items.Add(new(Text(grant, "type") ?? "unknown", label, scope, remaining.Value, ends, known,
                usable.ValueKind is JsonValueKind.True or JsonValueKind.False ? usable.GetBoolean() : null));
            total += remaining.Value;
        }
        return new(complete ? total : null, items, now, complete ? "ok" : "unknown", complete);
    }
}
