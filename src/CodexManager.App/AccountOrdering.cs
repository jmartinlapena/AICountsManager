namespace CodexManager;

internal sealed record OrderedAccount(string Id, string Provider, string Name, string? Email);
internal static class AccountOrdering
{
    public static string Codex(string name) => "codex:" + name;
    // External adapters expose one current-session slot; changing that session keeps its position.
    public static string Current(string provider) => "provider:" + provider;
    public static IReadOnlyList<OrderedAccount> Resolve(IReadOnlyList<ProfileState> profiles, IReadOnlyList<ProviderUsage>? other, IReadOnlyList<string>? order)
    {
        var ranks = (order ?? []).Select((id, index) => (id, index)).GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
        return profiles.Select(p => new OrderedAccount(Codex(p.Name), ProviderIds.Codex, p.Name, p.Email))
            .Concat((other ?? []).Select(p => new OrderedAccount(Current(p.Provider), p.Provider, p.Name, p.Email)))
            .OrderBy(p => ranks.GetValueOrDefault(p.Id, int.MaxValue)).ToArray();
    }
    // Disabled provider slots retain their position when the visible accounts are rearranged.
    public static string[] ReplaceVisible(IReadOnlyList<string>? saved, IReadOnlyList<string> visible)
    {
        var current = visible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(); var next = 0;
        foreach (var id in (saved ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(current.Contains(id) ? visible[next++] : id);
        result.AddRange(visible.Skip(next));
        return result.ToArray();
    }
}
