using System.Text.Json;

namespace SharpRail.Performance;

internal static class SelfTests
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run()
    {
        Require(Data.OnTime(5000, 5000) && Data.OnTime(5500, 5000) && !Data.OnTime(5501, 5000) && !Data.OnTime(4999, 5000), "RAM deadline tolerance is explicit and bounded.");
        var values = Statistics.Of([1, 2, 3]);
        Require(values.Mean == 2 && values.Median == 2 && values.SampleStandardDeviation == 1, "Sample statistics must use n-1.");
        Require(Statistics.Of([4]).SampleStandardDeviation is null && Statistics.Of([1, 2, 3, 4]).Median == 2.5, "Single-run uncertainty and even medians.");
        var memory = new MemorySnapshot(10, 1, [new(1, 0, 1048576, 2097152, 0), new(2, 1, 1048576, 1048576, 0), new(3, 0, 3145728, 4194304, 0)]);
        var sample = new Sample(1, "embedded", false, true, null, new() { ["workspace-ready"] = 30, ["first-window"] = 40 }, 5000, memory, 1, null, "AC", "AC");
        var artifact = new Artifact("<unsafe>", "dirty", "osx-arm64", "10", "today", [], "runtimepack/10");
        var result = new Result(1, Data.Contract, "today", new("model", "macOS", "arm64", "CPU", "1"), artifact, "fixture", 1, 1, 5000,
            [sample, sample with { Warmup = true, MilestonesMs = new() { ["workspace-ready"] = 999 } }, sample with { Success = false, Error = "failure" }]);
        Require(Data.Metrics(result, "embedded")["workspace-ready"].Mean == 30 && Data.Metrics(result, "embedded")["physical-footprint-mib"].Mean == 5, "Exclude warm-ups/failures; totals count every PID once.");
        Require(Report.TreeBytes(sample, 1) == 2097152, "Descendant attribution must not include an unrelated root.");
        var copy = JsonSerializer.Deserialize<Result>(JsonSerializer.Serialize(result, Data.Json), Data.Json)!;
        Require(copy.Samples[0].Memory!.Processes[0].PhysicalFootprintBytes == 1048576, "Raw JSON round-trip preserves native byte counts.");
        Data.Compatible(result, copy);
        Require(Data.Metrics(result with { Samples = [sample with { ObservedMilestones = [new("first-window", 1, 1, "unavailable", "no rendering")] }] }, "embedded").ContainsKey("first-window") == false, "Unavailable milestones must not appear as successful timings.");
        try { Data.Compatible(result, copy with { MemoryDelayMs = 0 }); throw new InvalidOperationException("Accepted mismatched baseline."); }
        catch (InvalidDataException) { }
        var complete = result with { Samples = [sample, sample with { Warmup = true }] };
        Require(Gates.Evaluate(complete, complete, 0, 0).All(gate => gate.Status == "pass"), "Identical complete runs meet zero-change limits.");
        Require(Gates.Evaluate(complete with { Samples = [sample with { MilestonesMs = new() { ["workspace-ready"] = 60 } }, sample with { Warmup = true }] }, complete, 10, null).Single().Status == "fail", "Startup limit violations are explicit.");
        Require(Gates.Evaluate(result, complete, 10, null).Single().Status == "invalid", "A partially failed run cannot silently pass a regression gate.");
        Require(Gates.Evaluate(complete with { Samples = [] }, complete with { Samples = [] }, 10, null).Single().Status == "invalid", "Empty runs cannot pass a configured gate.");
        var previousOverride = Environment.GetEnvironmentVariable("COMPlus_ReadyToRun");
        try
        {
            Environment.SetEnvironmentVariable("COMPlus_ReadyToRun", "0");
            try { Runner.RejectRuntimeOverrides(); throw new InvalidOperationException("Accepted runtime override."); }
            catch (InvalidOperationException error) when (error.Message.Contains("values withheld", StringComparison.Ordinal)) { }
        }
        finally { Environment.SetEnvironmentVariable("COMPlus_ReadyToRun", previousOverride); }
        var temporary = Path.Combine(Path.GetTempPath(), "sharprail-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var report = Path.Combine(temporary, "report.html");
            Report.Write(result, report, copy);
            var html = File.ReadAllText(report);
            Require(html.Contains("&lt;unsafe&gt;", StringComparison.Ordinal) && !html.Contains("<unsafe>", StringComparison.Ordinal), "Report must escape raw metadata.");
            Require(html.Contains("data:application/json;base64", StringComparison.Ordinal) && html.Contains("<svg", StringComparison.Ordinal) && html.Contains("failure", StringComparison.Ordinal), "Standalone reports retain raw observations, charts and failures.");
            Require(html.Contains("segmented startup timeline", StringComparison.Ordinal) && !html.Contains("width=-", StringComparison.Ordinal), "Independent milestones may precede first-window without producing negative segments.");
            Require(html.Contains("Pure CLR cost and total Skia/GPU cost are not isolated", StringComparison.Ordinal) && html.Contains("These experiments have not been run", StringComparison.Ordinal), "Elapsed timeline intervals cannot imply measured CLR/Skia attribution.");
            var checkpoints = Data.StartupSegments.Select(phase => phase.From).Append("first-window").Distinct().Select((name, index) => (name, value: index * 10.0)).ToDictionary(pair => pair.name, pair => pair.value);
            checkpoints["skia-init-start"] = 11;
            checkpoints["skia-init-end"] = 12;
            var phased = result with { Warmups = 0, Repetitions = 1, Samples = [sample with { MilestonesMs = checkpoints }] };
            Require(Data.IntervalValues(phased, "embedded", "skia-init-start", "skia-init-end").Single() == 1, "Callback costs come from paired timestamps within each launch.");
            Report.Write(phased, report);
            Require(File.ReadAllText(report).Contains("embedded first-window phase costs", StringComparison.Ordinal), "Complete ordered checkpoints produce a measured first-window chart.");
            var backwards = phased with { Samples = [phased.Samples[0] with { MilestonesMs = new(checkpoints) { ["first-window"] = -1 } }] };
            Require(Data.IntervalValues(backwards, "embedded", "ui-launch", "first-window").Length == 0, "Reordered boundaries cannot become negative cost segments.");
            Report.Write(backwards, report);
            Require(File.ReadAllText(report).Contains("component timeline is withheld", StringComparison.Ordinal), "A partial or reordered chain cannot invent component attribution.");
            var speedscope = Path.Combine(temporary, "sample.json");
            File.WriteAllText(speedscope, """{"shared":{"frames":[{"name":"root"},{"name":"leaf"}]},"profiles":[{"type":"sampled","unit":"none","samples":[[0,1],[0]],"weights":[1,2]}]}""");
            Require(Runner.Flamegraph(speedscope).Contains("leaf", StringComparison.Ordinal), "Managed stack report consumes real Speedscope sample structure.");
            File.WriteAllText(speedscope, """{"shared":{"frames":[{"name":"root"},{"name":"evented-leaf"}]},"profiles":[{"type":"evented","unit":"milliseconds","startValue":0,"endValue":4,"events":[{"type":"O","at":0,"frame":0},{"type":"O","at":1,"frame":1},{"type":"C","at":3,"frame":1},{"type":"C","at":4,"frame":0}]}]}""");
            Require(Runner.Flamegraph(speedscope).Contains("evented-leaf", StringComparison.Ordinal), "The .NET converter's evented Speedscope format must produce stacks too.");
        }
        finally { Directory.Delete(temporary, true); }
        Console.WriteLine("PASS performance statistics, filtering, process attribution, JSON, baseline compatibility, HTML escaping and managed stack rendering");
    }
}