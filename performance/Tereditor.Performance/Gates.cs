namespace SharpRail.Performance;

internal sealed record GateOutcome(string Mode, string Metric, string Status, double? BaselineMean, double? CurrentMean, double? ChangePercent, double LimitPercent, string Reason);
internal static class Gates
{
    public static GateOutcome[] Evaluate(Result current, Result baseline, double? startupLimit, double? memoryLimit)
    {
        Data.Compatible(current, baseline);
        var outcomes = new List<GateOutcome>();
        if (current.Samples.Length == 0 || baseline.Samples.Length == 0)
            return startupLimit is not null || memoryLimit is not null
                ? [new("all", "observations", "invalid", null, null, null, startupLimit ?? memoryLimit ?? 0, "No observations are available for a regression verdict.")]
                : [];
        foreach (var mode in current.Samples.Select(sample => sample.Mode).Distinct())
            foreach (var (metric, limit) in new[] { ("workspace-ready", startupLimit), ("physical-footprint-mib", memoryLimit) })
            {
                if (limit is null) continue;
                var rows = current.Samples.Where(sample => sample.Mode == mode).ToArray();
                var oldRows = baseline.Samples.Where(sample => sample.Mode == mode).ToArray();
                var complete = rows.All(sample => sample.Success) && oldRows.All(sample => sample.Success) &&
                    rows.Count(sample => !sample.Warmup) == current.Repetitions && rows.Count(sample => sample.Warmup) == current.Warmups &&
                    oldRows.Count(sample => !sample.Warmup) == baseline.Repetitions && oldRows.Count(sample => sample.Warmup) == baseline.Warmups;
                var values = Data.Metrics(current, mode);
                var previous = Data.Metrics(baseline, mode);
                if (!complete || !values.TryGetValue(metric, out var value) || !previous.TryGetValue(metric, out var old) || old.Mean <= 0)
                {
                    outcomes.Add(new(mode, metric, "invalid", null, null, null, limit.Value, "All requested measured launches and warm-ups in both runs must succeed, with comparable observations."));
                    continue;
                }
                var change = (value.Mean / old.Mean - 1) * 100;
                outcomes.Add(new(mode, metric, change > limit ? "fail" : "pass", old.Mean, value.Mean, change, limit.Value, "Descriptive mean-change limit; not a significance test."));
            }
        return [.. outcomes];
    }
}