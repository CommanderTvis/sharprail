// Runs one Ghostty benchmark (three trials, one for attribution) under AC power and records the trials.
// Run from the checkout root: dotnet run --file benchmarks/ghostty/run.cs -- presentation|memory|attribution native|texture .bench/NEW_DIRECTORY
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

var executables = new Dictionary<string, string> { ["presentation"] = "GhosttyRendererBench", ["memory"] = "GhosttyMemoryBench", ["attribution"] = "GhosttyAttribution" };
if (args.Length < 3 || !executables.ContainsKey(args[0]) || args[1] is not ("native" or "texture"))
{
    Console.Error.WriteLine("Usage: dotnet run --file benchmarks/ghostty/run.cs -- presentation|memory|attribution native|texture .bench/NEW_DIRECTORY");
    return 1;
}
var (kind, mode) = (args[0], args[1]);
var batch = Path.GetFullPath(args[2]);
if (Directory.Exists(batch) || !Directory.Exists(Path.GetDirectoryName(batch))) throw new IOException($"Cannot create {batch}: it exists or its parent is missing.");
Directory.CreateDirectory(batch);
var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var compact = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var trials = new JsonArray();
var metadata = new JsonObject { ["started"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["kind"] = kind, ["mode"] = mode, ["power"] = Power(), ["trials"] = trials };
var dotnetRoot = Run("sh", Path.GetFullPath("scripts/tool"), "dotnet").Trim();
for (var i = 1; i <= (kind == "attribution" ? 1 : 3); i++)
{
    if (!Power().Contains("'AC Power'")) throw new InvalidOperationException("AC power required");
    var output = Path.Combine(batch, i.ToString());
    var info = new ProcessStartInfo(Path.GetFullPath($"benchmarks/ghostty/{kind}/bin/Release/net10.0/{executables[kind]}")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    info.ArgumentList.Add(mode);
    info.ArgumentList.Add(output);
    info.Environment["DOTNET_ROOT"] = dotnetRoot;
    using var child = Process.Start(info)!;
    var log = new StringBuilder();
    void Collect(StreamReader reader) => Task.Run(() =>
    {
        var buffer = new char[4096];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) lock (log) log.Append(buffer, 0, count);
    });
    Collect(child.StandardOutput);
    Collect(child.StandardError);
    var readings = new JsonArray();
    Exception? failure = null;
    using var monitor = new Timer(_ =>
    {
        try
        {
            var value = Power();
            lock (readings) readings.Add(new JsonObject { ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["power"] = value });
            if (!value.Contains("'AC Power'")) throw new InvalidOperationException("Power changed");
        }
        catch (Exception error) { failure = error; Terminate(child); }
    }, null, 1000, 1000);
    using var timeout = new Timer(_ => { failure = new InvalidOperationException("Trial exceeded 120 seconds"); Terminate(child); }, null, 120000, Timeout.Infinite);
    try { await child.WaitForExitAsync(); }
    finally
    {
        monitor.Dispose();
        timeout.Dispose();
        await Task.Delay(100); // let the readers drain the pipes
        lock (log) File.WriteAllText(output + ".log", log.ToString());
        lock (readings) File.WriteAllText(output + ".power.json", readings.ToJsonString(compact));
    }
    if (failure is not null || child.ExitCode != 0) throw failure ?? new InvalidOperationException($"Trial failed: {child.ExitCode}\n{log}");
    var result = JsonNode.Parse(File.ReadAllText(output + "/result.json"))!;
    if ((double)result["scale"]! != 2) throw new InvalidOperationException("Expected 2x backing scale");
    trials.Add(result);
    File.WriteAllText(batch + "/metadata.json", metadata.ToJsonString(options));
    Console.WriteLine($"{kind} {mode} {i} [{string.Join(", ", result["phases"]!.AsArray().Select(phase => $"[ '{phase!["name"]}', {phase["footprintMiB"]} ]"))}]");
}
return 0;

static string Power() => Run("pmset", "-g", "batt");

static string Run(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var text = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {process.ExitCode}");
    return text;
}

static void Terminate(Process child)
{
    try { if (!child.HasExited) Kill(child.Id, 15); } catch (InvalidOperationException) { }
}

[DllImport("libc", EntryPoint = "kill")] static extern int Kill(int pid, int signal);
