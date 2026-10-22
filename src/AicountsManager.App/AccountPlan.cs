using System.Text.Json.Nodes;

namespace AicountsManager;

internal static class AccountPlan
{
    public static string? Read(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var plan)
        && !string.IsNullOrWhiteSpace(plan) && plan.Length <= 80 ? plan.Trim().ToLowerInvariant() : null;

    // WHAM/Codex identifiers, not multipliers inferred from quota percentages.
    // Pro tiers: https://help.openai.com/en/articles/9793128-about-chatgpt-pro-tiers
    public static string? Label(string? plan) => plan?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "free" => "Free", "go" => "Go", "plus" => "Plus",
        "prolite" => "Pro 100", "pro" => "Pro 200", "promax" => "Pro 500",
        "team" => "Team", "business" => "Business",
        "self_serve_business_prolite" => "Business Premium",
        "self_serve_business_usage_based" => "Business · uso",
        "enterprise" or "ent26" or "hc" or "enterprise_cbp_automation" or "enterprise_cbp_usage_based" => "Enterprise",
        "edu" or "education" => "Edu", "edu_plus" => "Edu Plus", "edu_pro" => "Edu Pro",
        "sci" or "sci_plus" => "Academic",
        _ => "Otro plan",
    };

    public static bool IsWeeklyOnlyPlan(string? plan, IReadOnlyList<UsageWindow> windows)
    {
        // A successful, explicit weekly-only response is required. Missing data,
        // an unrecognized window, or any five-hour window must keep the session row.
        return plan?.ToLowerInvariant() is "pro" or "prolite" or "promax" or "self_serve_business_prolite"
            && windows.Count == 1
            && windows[0] is { WindowDurationMins: UsageWindowClassifier.WeeklyDurationMins,
                UsedPercent: >= 0 and <= 100, ResetsAt: > 0 };
    }
}
