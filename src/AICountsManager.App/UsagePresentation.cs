namespace AICountsManager;

internal sealed record QuotaPace(double ExpectedRemaining, double ProjectedUsed, double? SecondsToEmpty, double DailyBudget)
{
    public static double? WeeklyReference(UsageWindow window, DateTimeOffset now)
    {
        if (window.WindowDurationMins != 10080 || window.ResetsAt is not long reset
            || window.UsedPercent is not double used || !double.IsFinite(used)) return null;
        var left = reset - now.ToUnixTimeSeconds();
        return left > 0 && left <= 604800 ? left / 604800d * 100 : null;
    }
    public static QuotaPace? Calculate(UsageWindow window, DateTimeOffset now)
    {
        if (WeeklyReference(window, now) is not double reference) return null;
        double used = window.UsedPercent!.Value, left = window.ResetsAt!.Value - now.ToUnixTimeSeconds(), duration = 604800;
        var elapsed = duration - left;
        // A very young window does not provide a useful rate estimate.
        if (elapsed < Math.Max(300, duration * .03)) return null;
        used = Math.Clamp(used, 0, 100);
        var rate = used / elapsed;
        return new QuotaPace(reference, used + rate * left,
            rate > 0 ? (100 - used) / rate : null, (100 - used) / (left / 86400));
    }
}

internal static class UsagePresentation
{
    public static string Percent(double value) => double.IsFinite(value) ? $"{Math.Clamp(value, 0, 100):0}%" : "—";
    public static string Forecast(QuotaPace pace) => pace.ProjectedUsed > 100 && pace.SecondsToEmpty is double seconds
        ? "⚠ Límite en " + Duration(seconds)
        : $"≈{Math.Max(0, 100 - pace.ProjectedUsed):0}% al reset";
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

// All account cards use the same display calculations. Provider adapters only supply data.
internal sealed record QuotaDisplay(double? Remaining, string RemainingText, string? ResetText, double? Reference, QuotaPace? Pace)
{
    public string ForecastText => Pace is null ? "" : UsagePresentation.Forecast(Pace);
    public bool AtRisk => Pace?.ProjectedUsed > 100;
    public double FillWidth(double width) => Math.Max(0, width * Math.Clamp(Remaining ?? 0, 0, 100) / 100);
    public double MarkerOffset(double width) => Math.Max(0, (width - 2) * (Reference ?? 0) / 100);
    public static QuotaDisplay Create(UsageWindow? window, double? remaining, bool fresh, DateTimeOffset now) => new(
        remaining, remaining is double value ? UsagePresentation.Percent(value) : "Sin datos",
        window is null ? null : UsagePresentation.Countdown(window.ResetsAt, now),
        window is not null && fresh ? QuotaPace.WeeklyReference(window, now) : null,
        window is not null && fresh ? QuotaPace.Calculate(window, now) : null);
}
