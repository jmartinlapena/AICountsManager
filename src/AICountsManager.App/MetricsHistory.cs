using System.Globalization;

namespace AICountsManager;

internal static class MetricsPeriods
{
    public static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    public static DateOnly Month(DateOnly day) => new(day.Year, day.Month, 1);
    public static DateOnly Cutoff(DateOnly today) => today.AddDays(1 - TokenHistory.RetentionDays);
    public static IEnumerable<DateOnly> Days(DateOnly start, DateOnly end)
    {
        for (var day = start; day <= end; day = day.AddDays(1)) yield return day;
    }
}

internal sealed record ProviderTokenSeries(string Provider, decimal Tokens);
internal sealed record ModelTokens(string Name, decimal Tokens);
internal sealed record MetricsDay(DateOnly Date, IReadOnlyList<ProviderTokenSeries> Series, bool Covered)
{
    public decimal Total => Series.Sum(s => s.Tokens);
}

internal sealed record MetricsPeriod(IReadOnlyList<MetricsDay> Days, IReadOnlyList<ProviderTokenSeries> Series,
    IReadOnlyList<string> MissingProviders, bool Partial, bool CatchingUp, bool Clipped)
{
    public decimal Total => Series.Sum(s => s.Tokens);
    public bool HasKnownTotal => Total > 0 || (Days.Count != 0 && Days.All(d => d.Covered));
}

// The four views read the same in-memory snapshot. Dates and totals are indexed once per refresh.
internal sealed class MetricsHistory
{
    private sealed record Source(string Provider, TokenHistorySnapshot Snapshot, Dictionary<DateOnly, TokenDay> Days, DateOnly? CheckedDate)
    {
        public bool Incomplete => Snapshot.Partial || Snapshot.CatchingUp || Snapshot.Error is not null;

        public bool Covers(DateOnly date)
        {
            // A saved Cursor event establishes activity for that date, but a missing event is not proof of zero.
            if (Provider == ProviderIds.Cursor) return Snapshot.CoveredDays?.Contains(date) == true;
            return !Incomplete && CheckedDate is { } checkedDate && date <= checkedDate;
        }
    }

    private readonly Source[] _sources;
    private readonly DateOnly _today;
    private decimal[]? _heatThresholds;
    private readonly bool _incomplete;
    private readonly bool _catchingUp;
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("es-ES");

    public MetricsHistory(TokenHistorySnapshot snapshot, DateOnly today)
    {
        _today = today;
        _catchingUp = snapshot.CatchingUp;
        _incomplete = snapshot.Partial || _catchingUp || snapshot.Error is not null;
        var sources = snapshot.Sources ?? new Dictionary<string, TokenHistorySnapshot> { [ProviderIds.Codex] = snapshot };
        _sources = sources.Select(entry =>
        {
            var days = new Dictionary<DateOnly, TokenDay>();
            foreach (var row in entry.Value.Days)
                if (DateOnly.TryParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    days[date] = row;
            DateOnly? checkedDate = DateTimeOffset.TryParse(entry.Value.UpdatedAt, out var stamp)
                ? DateOnly.FromDateTime(stamp.LocalDateTime)
                : entry.Value.UpdatedAt is not null ? today : null;
            return new Source(entry.Key, entry.Value, days, checkedDate);
        }).ToArray();
    }

    public MetricsPeriod Read(DateOnly start, DateOnly end, DateOnly today)
    {
        var cutoff = MetricsPeriods.Cutoff(today);
        var first = start < cutoff ? cutoff : start;
        var last = end > today ? today : end;
        var dates = MetricsPeriods.Days(first, last).ToArray();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var days = new List<MetricsDay>(dates.Length);
        foreach (var date in dates)
        {
            var series = new List<ProviderTokenSeries>(_sources.Length);
            var covered = _sources.Length != 0;
            foreach (var source in _sources)
            {
                if (!source.Covers(date))
                {
                    covered = false;
                    missing.Add(source.Provider);
                }
                if (source.Days.GetValueOrDefault(date) is { Total: > 0 } day) series.Add(new(source.Provider, day.Total));
            }
            days.Add(new(date, Sort(series), covered));
        }
        var totals = days.SelectMany(day => day.Series).GroupBy(s => s.Provider)
            .Select(group => new ProviderTokenSeries(group.Key, group.Sum(s => s.Tokens)));
        var clipped = start < cutoff;
        return new(days, Sort(totals), missing.OrderBy(ProviderIds.Name, StringComparer.Ordinal).ToArray(),
            clipped || missing.Count != 0 || _incomplete || _sources.Any(s => s.Incomplete),
            _catchingUp || _sources.Any(s => s.Snapshot.CatchingUp), clipped);
    }

    public IReadOnlyList<ModelTokens> Models(DateOnly date, string provider) =>
        _sources.FirstOrDefault(s => s.Provider == provider)?.Days.GetValueOrDefault(date)?.Models
            .Where(m => m.Value > 0).Select(m => new ModelTokens(m.Key, m.Value))
            .OrderByDescending(m => m.Tokens).ThenBy(m => m.Name, StringComparer.Ordinal).ToArray() ?? [];

    // One scale for every month in the snapshot. Only the calendar needs the distribution.
    public int Heat(decimal total)
    {
        if (total <= 0) return 0;
        _heatThresholds ??= HeatThresholds();
        if (_heatThresholds.Length == 0) return 2;
        for (var i = 0; i < _heatThresholds.Length; i++)
            if (total <= _heatThresholds[i]) return i + 1;
        return _heatThresholds.Length + 1;
    }

    private decimal[] HeatThresholds()
    {
        var cutoff = MetricsPeriods.Cutoff(_today);
        var totals = _sources.SelectMany(source => source.Days)
            .Where(day => day.Key >= cutoff && day.Key <= _today && day.Value.Total > 0)
            .GroupBy(day => day.Key).Select(group => group.Sum(day => (decimal)day.Value.Total))
            .Distinct().Order().ToArray();
        if (totals.Length < 2) return [];
        decimal Percentile(decimal fraction)
        {
            var position = (totals.Length - 1) * fraction;
            var lower = (int)position;
            return totals[lower] + (totals[lower + 1] - totals[lower]) * (position - lower);
        }
        // Keep a separate top band for exceptional days within the upper quartile.
        return [Percentile(.25m), Percentile(.5m), Percentile(.75m), Percentile(.9m)];
    }

    public static string Number(decimal total) => total.ToString("N0", Culture);

    private static ProviderTokenSeries[] Sort(IEnumerable<ProviderTokenSeries> series) => series.OrderByDescending(s => s.Tokens)
        .ThenBy(s => s.Provider, StringComparer.Ordinal).ToArray();
}
