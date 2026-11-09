// Measures published app startup (first window, workspace layout, host readiness) and process-tree RSS on macOS.
// Run from the checkout root: dotnet run scripts/benchmark.cs -- [embedded|remote] [rounds] [workspace]
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var mode = args.Length > 0 ? args[0] : "embedded";
var rounds = args.Length > 1 && int.TryParse(args[1], out var parsedRounds) ? parsedRounds : args.Length > 1 ? 0 : 10;
var root = Path.GetFullPath(args.Length > 2 ? args[2] : ".");
if (mode is not ("embedded" or "remote") || rounds < 1)
{
    Console.Error.WriteLine("Usage: dotnet run scripts/benchmark.cs -- [embedded|remote] [rounds] [workspace]");
    return 1;
}
Directory.CreateDirectory(".bench");
Run("clang", "-O2", "scripts/window-probe.m", "-framework", "CoreGraphics", "-framework", "CoreFoundation", "-o", ".bench/window-probe");

Console.WriteLine("Filesystem warm-up (excluded)");
await Measure();
var samples = new List<Sample>();
for (var i = 0; i < rounds; i++)
{
    var sample = await Measure();
    samples.Add(sample);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        run = i + 1, sample.FirstWindowMs, sample.WorkspaceLayoutMs, sample.HostReadyMs, sample.ClientRssMiB, sample.HostRssMiB, sample.TotalRssMiB,
        powerReadings = sample.PowerReadings.Count,
    }, Json()));
}
var summary = new Dictionary<string, object?>();
foreach (var (key, select) in new (string, Func<Sample, double?>)[]
{
    ("firstWindowMs", s => s.FirstWindowMs), ("workspaceLayoutMs", s => s.WorkspaceLayoutMs), ("hostReadyMs", s => s.HostReadyMs),
    ("clientRssMiB", s => s.ClientRssMiB), ("hostRssMiB", s => s.HostRssMiB), ("totalRssMiB", s => s.TotalRssMiB),
})
{
    var values = samples.Select(select).Where(value => value is not null).Select(value => value!.Value).ToList();
    if (values.Count == 0) { summary[key] = null; continue; }
    var mean = values.Average();
    summary[key] = new { mean, sampleStandardDeviation = values.Count < 2 ? (double?)null : Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1)) };
}
var report = new
{
    mode, workload = "Two-method C# host; static Thinkrail workspace maquette; no Pi", root,
    recordedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), rounds, summary, samples,
};
var path = $".bench/results-{mode}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.json";
File.WriteAllText(path, JsonSerializer.Serialize(report, JsonIndented()) + "\n");
Console.WriteLine(JsonSerializer.Serialize(new { path, summary }, JsonIndented()));
return 0;

static JsonSerializerOptions Json() => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
static JsonSerializerOptions JsonIndented() => new(Json()) { WriteIndented = true };
static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

static string Run(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {process.ExitCode}");
    return output;
}

static Power ReadPower()
{
    var output = Run("pmset", "-g", "batt").Trim();
    if (!output.Contains("'AC Power'")) throw new InvalidOperationException("Benchmark requires AC power: " + output);
    return new(Now(), output);
}

static double Rss(int pid)
{
    var rows = Run("ps", "-axo", "pid=,ppid=,rss=").Trim().Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToArray()).ToList();
    var ids = new HashSet<double> { pid };
    for (var changed = true; changed;)
    {
        changed = false;
        foreach (var row in rows)
            if (ids.Contains(row[1]) && !ids.Contains(row[0])) { ids.Add(row[0]); changed = true; }
    }
    return rows.Where(row => ids.Contains(row[0])).Sum(row => row[2]) / 1024;
}

static Launched Launch(string binary, Dictionary<string, string> env, string marker)
{
    var info = new ProcessStartInfo(Path.GetFullPath(binary)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var (key, value) in env) info.Environment[key] = value;
    var child = Process.Start(info)!;
    var output = new StringBuilder();
    var ready = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
    var pattern = new Regex(marker + @" (\d+)");
    var timer = new Timer(_ => ready.TrySetException(new InvalidOperationException("Readiness timeout: " + output)), null, 30000, Timeout.Infinite);
    void Receive(StreamReader reader) => Task.Run(() =>
    {
        var buffer = new char[4096];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            string text;
            lock (output) { output.Append(buffer, 0, count); text = output.ToString(); }
            if (pattern.Match(text) is { Success: true } match) { timer.Dispose(); ready.TrySetResult(double.Parse(match.Groups[1].Value)); }
        }
    });
    Receive(child.StandardOutput);
    Receive(child.StandardError);
    child.EnableRaisingEvents = true;
    child.Exited += (_, _) => { timer.Dispose(); ready.TrySetException(new InvalidOperationException($"Process exited: {child.ExitCode}\n{output}")); };
    return new(child, ready.Task, () => { lock (output) return output.ToString(); });
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task Stop(Process? child)
{
    if (child is null || child.HasExited) return;
    Kill(child.Id, 15);
    if (!child.WaitForExit(3000)) Kill(child.Id, 9);
    await child.WaitForExitAsync();
}

[DllImport("libc", EntryPoint = "kill")] static extern int Kill(int pid, int signal);

async Task<Sample> Measure()
{
    var powerReadings = new List<Power> { ReadPower() };
    Exception? powerFailure = null;
    using var monitor = new Timer(_ =>
    {
        try { var reading = ReadPower(); lock (powerReadings) powerReadings.Add(reading); }
        catch (Exception error) { powerFailure = error; }
    }, null, 1000, 1000);
    Launched? host = null, client = null;
    Process? probe = null;
    try
    {
        var port = FreePort();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        env["SHARPRAIL_ROOT"] = root; env["SHARPRAIL_REMOTE"] = ""; env["SHARPRAIL_TOKEN"] = token;
        var launchedAt = Now();
        if (mode == "remote")
        {
            host = Launch("artifacts/host/SharpRail.Host.Remote", new(env) { ["SHARPRAIL_PORT"] = port.ToString(), ["SHARPRAIL_BIND"] = "127.0.0.1" }, "SHARPRAIL_HOST_READY");
            env["SHARPRAIL_REMOTE"] = "http://127.0.0.1:" + port;
            // Both runtimes are fresh. Wait for bind before connecting; host time stays in total startup.
            await host.Ready;
        }
        client = Launch("artifacts/SharpRail.app/Contents/MacOS/SharpRail.UI", env, "SHARPRAIL_WORKSPACE_LAYOUT");
        var probeInfo = new ProcessStartInfo(Path.GetFullPath(".bench/window-probe")) { RedirectStandardOutput = true, UseShellExecute = false };
        probeInfo.ArgumentList.Add(client.Child.Id.ToString());
        probe = Process.Start(probeInfo)!;
        var firstWindow = Task.Run(async () =>
        {
            var output = await probe.StandardOutput.ReadToEndAsync();
            await probe.WaitForExitAsync();
            return probe.ExitCode == 0 ? double.Parse(output.Trim()) : throw new InvalidOperationException("CoreGraphics probe failed");
        });
        var layoutAt = await client.Ready;
        var windowAt = await firstWindow;
        var remaining = Math.Max(0, launchedAt + 5000 - Now());
        await Task.Delay((int)remaining);
        if (Now() - launchedAt > 5500) throw new InvalidOperationException("Startup missed the five-second RSS sampling interval.");
        foreach (var launched in new[] { client, host }.OfType<Launched>())
            if (launched.Child.HasExited) throw new InvalidOperationException("Process exited before RSS sampling: " + launched.Output());
        var clientRss = Rss(client.Child.Id);
        var hostRss = host is null ? 0 : Rss(host.Child.Id);
        lock (powerReadings) powerReadings.Add(ReadPower());
        if (powerFailure is not null) throw powerFailure;
        return new(windowAt - launchedAt, layoutAt - launchedAt, host is null ? null : await host.Ready - launchedAt,
            clientRss, hostRss, clientRss + hostRss, powerReadings.ToList(), client.Output(), host?.Output() ?? "");
    }
    finally
    {
        monitor.Dispose();
        await Stop(probe);
        await Stop(client?.Child);
        await Stop(host?.Child);
    }
}

record Power(double At, string Output);
record Sample(double FirstWindowMs, double WorkspaceLayoutMs, double? HostReadyMs, double ClientRssMiB, double HostRssMiB, double TotalRssMiB, List<Power> PowerReadings, string ClientOutput, string HostOutput);
record Launched(Process Child, Task<double> Ready, Func<string> Output);
