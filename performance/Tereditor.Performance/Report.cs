using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SharpRail.Performance;

internal static class Report
{
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "—");
    private static string N(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    public static void Write(Result result, string path, Result? baseline = null)
    {
        if (baseline is not null) Data.Compatible(result, baseline);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>SharpRail startup and initial RAM</title><style>
            :root{color-scheme:light dark;--bg:#fff;--fg:#202024;--muted:#666674;--line:#dddde4}
            @media(prefers-color-scheme:dark){:root{--bg:#17171b;--fg:#eeeef2;--muted:#aaaab6;--line:#393941}}
            body{background:var(--bg);color:var(--fg);font:14px/1.5 -apple-system,BlinkMacSystemFont,sans-serif;margin:24px auto;padding:0 20px;max-width:1200px}h1{font-size:24px}h2{font-size:19px;margin-top:30px}p{max-width:1000px}small,.muted{color:var(--muted)}svg{width:100%;height:auto}svg text{fill:var(--fg);font-size:13px}svg .axis{stroke:var(--line)}svg .error{stroke:var(--fg);stroke-width:2}table{width:100%;border-collapse:collapse;font-size:12px}td,th{border-bottom:1px solid var(--line);padding:6px;text-align:left;vertical-align:top}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:11px}a{color:inherit}.failure{color:#cc3650}
            </style><h1>SharpRail startup and initial RAM</h1>
            """);
        html.Append($"<p>{E(result.Machine.Cpu)} · {E(result.Machine.Model)} · macOS {E(result.Machine.Os)} · {E(result.Machine.Architecture)}. {result.Repetitions} fresh launches per mode after {result.Warmups} excluded warm-ups. Fresh profiles, warmed filesystem, AC power required.</p>");
        html.Append($"<p>RAM is macOS physical footprint at <strong>{N(result.MemoryDelayMs / 1000.0)} seconds after initial workspace readiness</strong>, not five seconds after launch. RSS appears separately. Values show mean ± one sample standard deviation; outliers are retained.</p>");
        StartupCosts(result, html);
        html.Append("<h2>Startup milestones</h2><p class=muted>Independent milestones from launch, ordered by their observed means. WindowServer listing is not pixel presentation. First content observes a completed render batch, including scheduling latency; it is not a display measurement. Missing markers remain missing.</p>");
        var modes = result.Samples.Select(sample => sample.Mode).Distinct().ToArray();
        var extent = modes.SelectMany(mode => Data.Metrics(result, mode))
            .Where(pair => !pair.Key.EndsWith("mib", StringComparison.Ordinal))
            .Select(pair => Math.Max(pair.Value.Maximum, pair.Value.Mean + (pair.Value.SampleStandardDeviation ?? 0))).DefaultIfEmpty(1).Max();
        var scale = 850 / Math.Max(1, extent);
        foreach (var mode in modes)
        {
            var metrics = Data.Metrics(result, mode);
            var times = metrics.Where(pair => pair.Key is "host-ready" or "first-window" or "workspace-layout" or "first-content-rendered" or "workspace-ready").OrderBy(pair => pair.Value.Mean).ToArray();
            var measured = result.Samples.Where(sample => sample.Mode == mode && !sample.Warmup).ToArray();
            html.Append($"<h3>{E(mode)}</h3><p>{measured.Count(sample => sample.Success)} successful / {measured.Length} measured launches; {measured.Count(sample => !sample.Success)} failed. Warm-ups excluded.</p>");
            if (times.Length == 0) { html.Append("<p class=failure>No successful startup samples.</p>"); continue; }
            html.Append($"<svg viewBox=\"0 0 1200 {times.Length * 48 + 80}\" role=img aria-label=\"{E(mode)} segmented startup timeline\">");
            var colors = new Dictionary<string, string>
            {
                ["host-ready"] = "#5946af",
                ["first-window"] = "#a54c00",
                ["workspace-layout"] = "#187643",
                ["first-content-rendered"] = "#245db3",
                ["files-ready"] = "#8c427e",
                ["git-ready"] = "#146c77",
                ["workspace-ready"] = "#5a6270"
            };
            var previousMean = 0.0;
            var row = 0;
            html.Append("<text x=10 y=35>Launch → workspace</text>");
            foreach (var (metric, statistic) in times)
            {
                var x = 260 + statistic.Mean * scale;
                var width = (statistic.Mean - previousMean) * scale;
                var deviation = (statistic.SampleStandardDeviation ?? 0) * scale;
                var y = 85 + row++ * 48;
                var color = colors.GetValueOrDefault(metric, "#5a6270");
                html.Append($"<g><title>{E(metric)}: {N(statistic.Mean)} ms from launch; preceding interval {N(statistic.Mean - previousMean)} ms; endpoint SD {(statistic.SampleStandardDeviation is { } sd ? N(sd) : "unavailable")} ms; n={statistic.Count}</title><rect x={N(260 + previousMean * scale)} y=12 width={N(width)} height=32 fill=\"{color}\" stroke=\"var(--bg)\" stroke-width=\"1\"/>");
                if (width >= 75) html.Append($"<text x={N(260 + (previousMean + statistic.Mean) / 2 * scale)} y=34 text-anchor=middle style=\"fill:white\">{N(statistic.Mean - previousMean)} ms</text>");
                html.Append($"<path class=error d=\"M{N(x - deviation)} 28H{N(x + deviation)} M{N(x - deviation)} 20V36 M{N(x + deviation)} 20V36\"/><path class=axis stroke-dasharray=\"3 3\" d=\"M{N(x)} 45V{y - 20}\"/><circle cx={N(x)} cy={y - 18} r=3 fill=\"{color}\"/><text x={N(Math.Clamp(x, 270, 1110))} y={y} text-anchor=end>{E(metric)}</text><text x={N(Math.Clamp(x, 270, 1110))} y={y + 17} text-anchor=end>{N(statistic.Mean)} ms (n={statistic.Count})</text></g>");
                previousMean = statistic.Mean;
            }
            html.Append("</svg><p class=muted>Segments are differences between observed milestone means, ordered by mean time; they are not measured CLR, Skia or application costs and do not imply a dependency chain. Error bars show each endpoint’s sample SD, not interval uncertainty. All modes share the same time scale. Short intervals remain visible through endpoint labels and hover details.</p><table><tr><th>Milestone</th><th>Mean ± SD (ms)</th><th>Median (ms)</th><th>Range (ms)</th><th>n</th></tr>");
            foreach (var (metric, value) in times)
                html.Append($"<tr><td>{E(metric)}</td><td>{N(value.Mean)} ± {(value.SampleStandardDeviation is { } sd ? N(sd) : "unavailable")}</td><td>{N(value.Median)}</td><td>{N(value.Minimum)}–{N(value.Maximum)}</td><td>{value.Count}</td></tr>");
            html.Append("</table>");
            html.Append("<h2>Initial memory</h2><table><tr><th>Metric</th><th>Mean ± SD</th><th>Median</th><th>Range</th><th>n</th></tr>");
            foreach (var metric in new[] { "physical-footprint-mib", "rss-mib" })
                if (metrics.TryGetValue(metric, out var value)) html.Append($"<tr><td>{metric}</td><td>{N(value.Mean)} ± {(value.SampleStandardDeviation is { } sd ? N(sd) : "unavailable")} MiB</td><td>{N(value.Median)}</td><td>{N(value.Minimum)}–{N(value.Maximum)}</td><td>{value.Count}</td></tr>");
            html.Append("</table>");
            var memory = metrics.GetValueOrDefault("physical-footprint-mib");
            if (memory is not null)
            {
                var max = Math.Max(1, memory.Maximum);
                var successful = result.Samples.Where(sample => sample.Mode == mode && !sample.Warmup && sample.Success).ToArray();
                var ui = Statistics.Of(successful.Select(sample => TreeBytes(sample, sample.UiPid) / 1048576));
                var host = Statistics.Of(successful.Select(sample => sample.HostPid is { } pid ? TreeBytes(sample, pid) / 1048576 : 0));
                html.Append($"<svg viewBox=\"0 0 1200 100\" role=img aria-label=\"{mode} process-tree physical footprint\"><text x=10 y=35>UI tree</text><rect x=260 y=10 width={N(ui.Mean / max * 850)} height=26 fill=\"#4b8cdb\"/><text x=10 y=78>Separate host tree</text><rect x=260 y=53 width={N(host.Mean / max * 850)} height=26 fill=\"#38a88b\"/><text x=270 y=95>{N(ui.Mean)} + {N(host.Mean)} MiB; embedded host is included in the UI.</text></svg>");
            }
            if (baseline is not null)
            {
                var previous = Data.Metrics(baseline, mode);
                html.Append("<h3>Compatible baseline comparison</h3><table><tr><th>Metric</th><th>Baseline mean</th><th>Current mean</th><th>Change</th></tr>");
                foreach (var (metric, value) in metrics)
                    if (previous.TryGetValue(metric, out var before)) html.Append($"<tr><td>{E(metric)}</td><td>{N(before.Mean)}</td><td>{N(value.Mean)}</td><td>{(before.Mean == 0 ? "unavailable" : N((value.Mean / before.Mean - 1) * 100) + "%")}</td></tr>");
                html.Append("</table><p class=muted>These are descriptive differences, not statistical significance or a regression verdict.</p>");
            }
        }
        html.Append("""
            <h2>Attribution boundaries and controlled baselines</h2>
            <p>The phase table measures elapsed boundary intervals. The OS/.NET loader envelope includes process/group-launch overhead, OS dynamic loading, CLR startup, entry-point preparation and the first timestamp hook; it is not pure VM overhead. The Skia callback measures platform registration/initialization only: Skia library loading, font setup, GPU contexts and drawing can also occur in other phases. Pure CLR cost and total Skia/GPU cost are not isolated.</p>
            <p>For a controlled comparison, use a minimal .NET executable (runtime entry floor), an empty Avalonia window (framework/windowing floor), an empty window with a small Skia drawing (incremental rendering work), and full SharpRail. These experiments have not been run. They need matching SDK/runtime, publishing settings, machine and harness; differences are marginal costs rather than additive components. The console baseline has no window, so only shared runtime/entry boundaries can be compared.</p>
            <p>The longer diagnostic trace includes waiting threads; its stack weights are not a first-window CPU breakdown. Startup-window filtering and runtime events help managed attribution. Native stacks alongside EventPipe are needed to investigate Skia, Metal and AppKit within an expensive phase. Process RAM totals likewise do not separate CLR, Skia, application allocations or physical GPU residency.</p>
            """);
        html.Append("<h2>Samples and process accounting</h2><table><tr><th>Mode/run</th><th>Status</th><th>Actual RAM delay</th><th>Milestones (ms)</th><th>Physical footprint / RSS by PID (MiB)</th></tr>");
        foreach (var sample in result.Samples)
        {
            html.Append($"<tr><td>{E(sample.Mode)} / {sample.Run}{(sample.Warmup ? " (warm-up, excluded)" : "")}</td><td class=\"{(sample.Success ? "" : "failure")}\">{(sample.Success ? "ok" : "failed")}<details><summary>Details</summary><pre>{E(sample.Error)}\n{E(sample.PowerBefore)}\n{E(sample.PowerAfter)}</pre></details></td><td>{(sample.SampleAfterReadyMs is { } delay ? N(delay) + " ms" : "unavailable")}</td><td>{string.Join("<br>", sample.MilestonesMs.Select(pair => E(pair.Key) + ": " + N(pair.Value)))}</td><td>");
            foreach (var process in sample.Memory?.Processes ?? []) html.Append($"PID {process.Pid} (parent {process.ParentPid}): {N(process.PhysicalFootprintBytes / 1048576.0)} / {N(process.ResidentBytes / 1048576.0)}; errno {process.Error}<br>");
            if (sample.Memory is { } snapshot) html.Append($"Snapshot duration: {N((snapshot.CompletedTimestamp - snapshot.StartedTimestamp) * 1000.0 / snapshot.Frequency)} ms; enumeration errno {snapshot.EnumerationError}<br>");
            if (sample.Memory?.UnclassifiedPids is { Length: > 0 } unknown) html.Append("Unclassified/excluded PIDs: " + string.Join(", ", unknown));
            html.Append("</td></tr>");
        }
        html.Append("</table><h2>Method and boundaries</h2><p>t0 is the harness monotonic timestamp before launching the first target process. Remote mode starts a fresh local host, waits for its readiness log, then starts the UI. Embedded mode has no separate host timer. Layout can precede WindowServer detection. Workspace-ready includes initial files and Git snapshot/branches; background statistics and spec scanning are excluded. Native window probing polls every 10 ms and its own process startup may delay detection.</p><p>RAM sums the main process and live descendants discovered at the snapshot, without double counting roots. Exited children are not retained; unreadable or missing target processes invalidate a sample. Physical footprint and RSS are different OS accounting measures; totals can include shared charges. Neither provides physical GPU residency. No forced GC, EventPipe tracing or counter collection is used in these headline runs. Lightweight milestone logging and one extra composition-batch request remain enabled and can add overhead. Process-tree snapshots are not atomic across PIDs. Membership is independently discovered through recursive child-PID enumeration. Unreadable known members invalidate the sample. Other unclassified PIDs are preserved in raw snapshots and excluded from totals; exited short-lived descendants may still be missed. Samples more than 500 ms late for their RAM deadline are invalid.</p><p>Failures and warm-ups are preserved but excluded from aggregates. Filesystem caches are not flushed; this is not a cold boot. CPU affinity, desktop quiescence and thermal isolation are not enforced; this is an exploratory baseline on a shared development machine. Fresh profile means no restored sessions or agents; the fixed fixture is intentionally small and does not represent a large user workspace.</p>");
        html.Append($"<h2>Provenance</h2><p>Commit {E(result.Artifact.Commit)} · {E(result.Artifact.Rid)} · SDK {E(result.Artifact.Sdk)} · runtime {E(result.Artifact.Runtime)} · built {E(result.Artifact.BuiltAt)}. Recorded {E(result.RecordedAt)}.</p><details><summary>Build manifest, dirty state and all raw observations</summary><pre>{E(JsonSerializer.Serialize(result, Data.Json))}</pre></details>");
        var json = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, Data.Json)));
        html.Append($"<p><a download=results.json href=\"data:application/json;base64,{json}\">Download raw JSON</a></p></html>");
        File.WriteAllText(path, html.ToString());
    }
    private static void StartupCosts(Result result, StringBuilder html)
    {
        html.Append("<h2>First-window cost breakdown</h2><p>Measured intervals from the UI process launch, excluding separate remote-host startup. Segments partition elapsed wall time; they include scheduling and background work, not just CPU time. Initial timestamps are buffered until later workspace markers so JSON logging is deferred beyond these initialization calls.</p>");
        var modes = result.Samples.Select(sample => sample.Mode).Distinct().ToArray();
        var totals = modes.Select(mode => Data.IntervalValues(result, mode, "ui-launch", "first-window")).Where(values => values.Length > 0).Select(Statistics.Of).ToArray();
        var extent = totals.Select(value => value.Mean + (value.SampleStandardDeviation ?? 0)).DefaultIfEmpty(1).Max();
        foreach (var mode in modes)
        {
            var n = result.Samples.Count(sample => sample.Mode == mode && !sample.Warmup && sample.Success);
            var phases = Data.StartupSegments.Select(phase => (Phase: phase, Values: Data.IntervalValues(result, mode, phase.From, phase.To))).ToArray();
            html.Append($"<h3>{E(mode)} UI process</h3>");
            if (n == 0 || phases.Any(phase => phase.Values.Length != n))
            {
                html.Append("<p>Complete ordered startup checkpoints are unavailable for this run. The component timeline is withheld; regenerate measurements with the phase-instrumented build.</p>");
                continue;
            }
            var total = Statistics.Of(Data.IntervalValues(result, mode, "ui-launch", "first-window"));
            var ranked = phases.Select(phase => (phase.Phase, Stats: Statistics.Of(phase.Values))).OrderByDescending(phase => phase.Stats.Mean).Take(3).ToArray();
            html.Append("<p>Largest measured intervals: " + string.Join("; ", ranked.Select(phase => $"{E(phase.Phase.Label)} — {N(phase.Stats.Mean)} ms ({N(100 * phase.Stats.Mean / Math.Max(1, total.Mean))}% of first-window time)")) + ". These identify where to investigate, not proven removable overhead.</p>");
            var scale = 850 / Math.Max(1, extent);
            html.Append($"<svg viewBox=\"0 0 1200 95\" role=img aria-label=\"{E(mode)} first-window phase costs\"><text x=10 y=35>UI launch → first window</text>");
            var elapsed = 0.0;
            foreach (var (phase, values) in phases)
            {
                var stats = Statistics.Of(values);
                var width = stats.Mean * scale;
                html.Append($"<g><title>{E(phase.Label)}: {N(stats.Mean)} ms, SD {(stats.SampleStandardDeviation is { } sd ? N(sd) : "unavailable")} ms, n={stats.Count}</title><rect x={N(260 + elapsed * scale)} y=12 width={N(width)} height=32 fill=\"{phase.Color}\" stroke=\"var(--bg)\" stroke-width=\"1\"/>");
                if (width >= 75) html.Append($"<text x={N(260 + (elapsed + stats.Mean / 2) * scale)} y=34 text-anchor=middle style=\"fill:white\">{N(stats.Mean)} ms</text>");
                html.Append("</g>");
                elapsed += stats.Mean;
            }
            var end = 260 + total.Mean * scale;
            var error = (total.SampleStandardDeviation ?? 0) * scale;
            html.Append($"<path class=error d=\"M{N(end - error)} 28H{N(end + error)} M{N(end - error)} 20V36 M{N(end + error)} 20V36\"/><text x=1110 y=75 text-anchor=end>first window: {N(total.Mean)} ms ± {(total.SampleStandardDeviation is { } deviation ? N(deviation) : "unavailable")} (n={total.Count})</text></svg>");
            html.Append("<table><tr><th>Measured interval</th><th>Mean ± SD (ms)</th><th>n</th></tr>");
            foreach (var (phase, values) in phases)
            {
                var stats = Statistics.Of(values);
                html.Append($"<tr><td><span style=\"display:inline-block;width:10px;height:10px;background:{phase.Color}\"></span> {E(phase.Label)}</td><td>{N(stats.Mean)} ± {(stats.SampleStandardDeviation is { } sd ? N(sd) : "unavailable")}</td><td>{stats.Count}</td></tr>");
            }
            html.Append("</table><h4>Inside Avalonia/platform bootstrap</h4><p class=muted>These callback timings are contained in the bootstrap interval above; do not add them again. They do not cover all later native rendering work.</p><table><tr><th>Initialization callback</th><th>Mean ± SD (ms)</th><th>n</th></tr>");
            foreach (var (label, key) in new[] { ("Platform services", "platform-services-init"), ("Native windowing", "windowing-init"), ("Skia platform", "skia-init"), ("Text shaping", "text-shaping-init") })
            {
                var values = Data.IntervalValues(result, mode, key + "-start", key + "-end");
                if (values.Length == 0) { html.Append($"<tr><td>{E(label)}</td><td>Not observed</td><td>0</td></tr>"); continue; }
                var stats = Statistics.Of(values);
                html.Append($"<tr><td>{E(label)}</td><td>{N(stats.Mean)} ± {(stats.SampleStandardDeviation is { } sd ? N(sd) : "unavailable")}</td><td>{stats.Count}</td></tr>");
            }
            html.Append("</table>");
            html.Append("<h4>What to investigate next</h4><p>Profile window construction around native Window creation, compiled XAML loading, docking construction and appearance/plugin wiring. Check which work can safely wait until the first window is visible. Separately investigate window showing and the first layout/render path; WindowServer polling adds observation latency, so this interval is not all application work. Compare an empty Avalonia window before attributing that cost to SharpRail.</p><table><tr><th>Showing boundary</th><th>Mean ± SD (ms)</th><th>n</th></tr>");
            foreach (var (label, from, to) in new[] { ("Construction complete → Opened event", "window-create-end", "window-opened"), ("Opened event → WindowServer observation", "window-opened", "first-window") })
            {
                var values = Data.IntervalValues(result, mode, from, to);
                if (values.Length == 0) continue;
                var stats = Statistics.Of(values);
                html.Append($"<tr><td>{E(label)}</td><td>{N(stats.Mean)} ± {(stats.SampleStandardDeviation is { } sd ? N(sd) : "unavailable")}</td><td>{stats.Count}</td></tr>");
            }
            html.Append("</table><p class=muted>Showing subintervals include only observed nonnegative pairs; their sample counts can differ when WindowServer sees a window before its Opened event.</p>");
        }
    }

    internal static double TreeBytes(Sample sample, int root)
    {
        var ids = new HashSet<int> { root };
        var rows = sample.Memory?.Processes ?? [];
        while (true)
        {
            var before = ids.Count;
            foreach (var row in rows) if (ids.Contains(row.ParentPid)) ids.Add(row.Pid);
            if (ids.Count == before) break;
        }
        return rows.Where(row => ids.Contains(row.Pid)).Sum(row => (double)row.PhysicalFootprintBytes);
    }
}