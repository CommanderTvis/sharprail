// Summarizes a batch written by run.cs into summary.json (footprints, CPU and display latency).
// Run: dotnet run benchmarks/ghostty/summarize.cs -- .bench/BATCH_DIRECTORY
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 1) { Console.Error.WriteLine("Usage: dotnet run benchmarks/ghostty/summarize.cs -- .bench/BATCH_DIRECTORY"); return 1; }
var batch = Path.GetFullPath(args[0]);
var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(batch, "metadata.json")))!;
var trials = metadata["trials"]!.AsArray().Select(trial => trial!).ToList();
static JsonObject Stats(IEnumerable<double> sequence)
{
    var values = sequence.ToList();
    return new() { ["n"] = values.Count, ["mean"] = values.Sum() / values.Count, ["min"] = values.Min(), ["max"] = values.Max() };
}
var phases = trials.SelectMany(trial => trial["phases"]!.AsArray().Select(phase => (string)phase!["name"]!)).Distinct().ToList();
var summaryPhases = new JsonObject();
var summary = new JsonObject { ["kind"] = (string)metadata["kind"]!, ["mode"] = (string)metadata["mode"]!, ["phases"] = summaryPhases };
foreach (var name in phases)
{
    var rows = trials.Select(trial => trial["phases"]!.AsArray().First(phase => (string)phase!["name"]! == name)!).ToList();
    var entry = new JsonObject
    {
        ["footprintMiB"] = Stats(rows.Select(row => (double)row["footprintMiB"]!)),
        ["peakFootprintMiB"] = Stats(rows.Select(row => (double)row["sampledPeakFootprintMiB"]!)),
    };
    if (name is "idle" or "paced") entry["cpuPercent"] = Stats(rows.Select(row => 100 * (double)row["cpuSeconds"]! / (double)row["seconds"]!));
    summaryPhases[name] = entry;
}
var latency = trials.SelectMany(trial => trial["latency"]?.AsArray().Select(sample => (double)sample!["displayMs"]!) ?? []).Order().ToList();
if (latency.Count > 0)
{
    double Percentile(double q)
    {
        var i = (latency.Count - 1) * q;
        return latency[(int)Math.Floor(i)] + (latency[(int)Math.Ceiling(i)] - latency[(int)Math.Floor(i)]) * (i % 1);
    }
    summary["latencyMs"] = new JsonObject { ["n"] = latency.Count, ["median"] = Percentile(.5), ["p95"] = Percentile(.95), ["max"] = latency.Max() };
}
var text = summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
File.WriteAllText(Path.Combine(batch, "summary.json"), text);
Console.WriteLine(text);
return 0;
