namespace CodexManager;

internal static class UsageWindowClassifier
{
    public const int SessionDurationMins = 300;
    public const int WeeklyDurationMins = 10_080;

    public static (UsageWindow Session, UsageWindow Weekly) Classify(IEnumerable<UsageWindow> source)
    {
        var windows = source.Where(HasData).ToList();
        var session = windows.FirstOrDefault(window => window.WindowDurationMins == SessionDurationMins);
        var weekly = windows.FirstOrDefault(window => window.WindowDurationMins == WeeklyDurationMins);

        // Current Codex responses identify each window by duration. Positional
        // fallback is kept only for legacy responses where both durations are
        // absent, and each source window is consumed at most once.
        if (session is null && weekly is null)
        {
            var unclassified = windows
                .Where(window => !window.WindowDurationMins.HasValue)
                .ToList();
            if (unclassified.Count >= 2)
            {
                session = unclassified[0];
                weekly = unclassified[1];
            }
        }

        return (session ?? Empty(), weekly ?? Empty());
    }

    public static UsageRecord NormalizeCached(UsageRecord usage)
    {
        var classified = Classify([usage.Session, usage.Weekly]);
        return usage with
        {
            Session = classified.Session,
            Weekly = classified.Weekly,
        };
    }

    private static bool HasData(UsageWindow window) =>
        window.UsedPercent.HasValue
        || window.WindowDurationMins.HasValue
        || window.ResetsAt.HasValue;

    private static UsageWindow Empty() => new(null, null, null);
}
