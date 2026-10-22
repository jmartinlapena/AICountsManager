namespace CodexManager;

internal sealed record QuotaPace(double ExpectedRemaining, double ProjectedUsed, double? SecondsToEmpty, double DailyBudget)
{
    public static QuotaPace? Calculate(UsageWindow window, DateTimeOffset now)
    {
        if (window.UsedPercent is not double used || !double.IsFinite(used)
            || window.WindowDurationMins is not > 0 || window.ResetsAt is not long reset) return null;
        double left = reset - now.ToUnixTimeSeconds(), duration = window.WindowDurationMins.Value * 60d;
        if (left <= 0 || left > duration) return null;
        var elapsed = duration - left;
        // A very young window does not provide a useful rate estimate.
        if (elapsed < Math.Max(300, duration * .03)) return null;
        used = Math.Clamp(used, 0, 100);
        var rate = used / elapsed;
        return new QuotaPace(left / duration * 100, used + rate * left,
            rate > 0 ? (100 - used) / rate : null, (100 - used) / (left / 86400));
    }
}

internal static class UsagePresentation
{
    public static string Countdown(long? reset, DateTimeOffset now)
    {
        if (reset is null) return "Sin fecha";
        try
        {
            var delta = DateTimeOffset.FromUnixTimeSeconds(reset.Value) - now;
            if (delta <= TimeSpan.Zero) return "Pendiente de actualizar";
            return "Resets in " + Duration(delta.TotalSeconds);
        }
        catch (ArgumentOutOfRangeException) { return "Sin fecha"; }
    }
    public static string Duration(double seconds)
    {
        if (seconds < 60) return "<1m";
        var span = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2));
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
    }
    public static string Tokens(long count) => count >= 1_000_000_000 ? $"{count / 1e9:0.##}B"
        : count >= 1_000_000 ? $"{count / 1e6:0.##}M" : count >= 1000 ? $"{count / 1e3:0.#}k" : count.ToString("N0");
}
