using System.Text.Json;

namespace SharpRail.Performance;

internal sealed record FixtureManifest(string Commit, string Tree, string BundleSha256, Dictionary<string, string> Files);
internal sealed record Artifact(string Commit, string GitStatus, string Rid, string Sdk, string BuiltAt, Dictionary<string, string> Sha256, string Runtime = "");
internal sealed record Machine(string Model, string Os, string Architecture, string Cpu, string MemoryBytes);
internal sealed record Milestone(string MilestoneName, long Timestamp, long Frequency, string? Status, string? Detail);
internal sealed record MemoryProcess(int Pid, int ParentPid, ulong PhysicalFootprintBytes, ulong ResidentBytes, int Error);
internal sealed record MemorySnapshot(long Timestamp, long Frequency, MemoryProcess[] Processes, int[]? UnclassifiedPids = null, long StartedTimestamp = 0, long CompletedTimestamp = 0, int EnumerationError = 0);
internal sealed record Sample(int Run, string Mode, bool Warmup, bool Success, string? Error,
    Dictionary<string, double> MilestonesMs, double? SampleAfterReadyMs, MemorySnapshot? Memory, int UiPid, int? HostPid, string PowerBefore, string PowerAfter, Milestone[]? ObservedMilestones = null);
internal sealed record Result(int Schema, string Contract, string RecordedAt, Machine Machine, Artifact Artifact,
    string FixtureHash, int Warmups, int Repetitions, int MemoryDelayMs, Sample[] Samples);
internal sealed record Statistics(int Count, double Mean, double? SampleStandardDeviation, double Median, double Minimum, double Maximum)
{
    public static Statistics Of(IEnumerable<double> input)
    {
        var values = input.Order().ToArray();
        if (values.Length == 0) throw new ArgumentException("No observations.");
        var mean = values.Average();
        return new(values.Length, mean, values.Length < 2 ? null : Math.Sqrt(values.Sum(value => Math.Pow(value - mean, 2)) / (values.Length - 1)),
            (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2, values[0], values[^1]);
    }
}
internal static class Data
{
    internal static readonly (string Label, string From, string To, string Color)[] StartupSegments =
    [
        ("OS/.NET loader and managed entry", "ui-launch", "managed-entry", "#5946af"),
        ("Avalonia/platform bootstrap", "managed-entry", "app-initialize-start", "#a54c00"),
        ("App resources and theme", "app-initialize-start", "app-initialize-end", "#187643"),
        ("Framework completion", "app-initialize-end", "framework-ready", "#245db3"),
        ("Profile, host and plugin setup", "framework-ready", "app-services-created", "#8c427e"),
        ("Application menu and routing", "app-services-created", "window-create-start", "#146c77"),
        ("Project session, native window and controls", "window-create-start", "window-create-end", "#9b4039"),
        ("Window showing and OS observation", "window-create-end", "first-window", "#5a6270")
    ];

    internal static double[] IntervalValues(Result result, string mode, string from, string to) => result.Samples
        .Where(sample => sample.Mode == mode && !sample.Warmup && sample.Success &&
            sample.MilestonesMs.ContainsKey(from) && sample.MilestonesMs.ContainsKey(to) &&
            sample.ObservedMilestones?.Any(mark => (mark.MilestoneName == from || mark.MilestoneName == to) && mark.Status is "error" or "failed" or "unavailable") != true)
        .Select(sample => sample.MilestonesMs[to] - sample.MilestonesMs[from]).Where(value => double.IsFinite(value) && value >= 0).ToArray();

    public const string Contract = "macos-startup-v2:buffered-startup-phases:window-onscreen-300x200:workspace-ready-files-git:phys-footprint-ready-plus-delay:fresh-profile:warm-filesystem";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n");
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(path);
    public static void Compatible(Result current, Result baseline)
    {
        if (current.Schema != baseline.Schema || current.Contract != baseline.Contract || current.Machine != baseline.Machine ||
            current.Artifact.Runtime != baseline.Artifact.Runtime || current.Artifact.Sdk != baseline.Artifact.Sdk || current.Artifact.Rid != baseline.Artifact.Rid || current.FixtureHash != baseline.FixtureHash || current.MemoryDelayMs != baseline.MemoryDelayMs ||
            !current.Samples.Select(sample => sample.Mode).Distinct().Order().SequenceEqual(baseline.Samples.Select(sample => sample.Mode).Distinct().Order()))
            throw new InvalidDataException("Baseline contract, machine, fixture, modes or memory sampling delay differs.");
    }
    public static bool OnTime(double delay, int expected) => delay >= expected && delay <= expected + 500;
    public static Dictionary<string, Statistics> Metrics(Result result, string mode) => result.Samples.Where(sample => sample.Mode == mode && !sample.Warmup && sample.Success)
        .SelectMany(sample => sample.MilestonesMs.Where(pair => double.IsFinite(pair.Value) && pair.Value >= 0 && sample.ObservedMilestones?.Any(mark => mark.MilestoneName == pair.Key && mark.Status is "error" or "failed" or "unavailable") != true).Select(pair => (pair.Key, Value: pair.Value)).Concat(sample.Memory is { } memory
            ? [("physical-footprint-mib", memory.Processes.Sum(process => (double)process.PhysicalFootprintBytes) / 1048576),
               ("rss-mib", memory.Processes.Sum(process => (double)process.ResidentBytes) / 1048576)] : []))
        .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => Statistics.Of(group.Select(pair => pair.Value)));
}