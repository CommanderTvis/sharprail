using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SharpRail.Performance;

internal sealed class Child : IAsyncDisposable
{
    public Process Process { get; }
    private readonly Task stdout;
    private readonly Task stderr;
    private readonly StreamWriter log;
    private readonly Dictionary<string, TaskCompletionSource<Milestone>> marks = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly int group;
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
    public readonly long Launched = Stopwatch.GetTimestamp();

    public Child(string command, IEnumerable<string> arguments, string workingDirectory, Dictionary<string, string>? environment, string logPath, string? groupLauncher = null)
    {
        var info = new ProcessStartInfo(groupLauncher ?? command) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (groupLauncher is not null) { info.ArgumentList.Add("group"); info.ArgumentList.Add(command); }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        log = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
        Process = Process.Start(info) ?? throw new IOException("Cannot start " + command);
        group = groupLauncher is not null ? Process.Id : 0;
        stdout = Pump(Process.StandardOutput);
        stderr = Pump(Process.StandardError);
    }

    private async Task Pump(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (gate)
                {
                    log.WriteLine(line);
                    Milestone? mark = null;
                    if (line.StartsWith("SHARPRAIL_PERF ", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(line[15..]);
                            var root = document.RootElement;
                            mark = new(root.GetProperty("milestone").GetString()!, root.GetProperty("timestamp").GetInt64(), root.GetProperty("frequency").GetInt64(),
                                root.TryGetProperty("status", out var status) ? status.GetString() : null, root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
                        }
                        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { log.WriteLine("Invalid milestone: " + error.Message); }
                    }
                    else if (line.StartsWith("SHARPRAIL_HOST_READY ", StringComparison.Ordinal)) mark = new("host-ready", Stopwatch.GetTimestamp(), Stopwatch.Frequency, "ok", null);
                    if (mark is not null)
                    {
                        if (!marks.TryGetValue(mark.MilestoneName, out var completion)) marks[mark.MilestoneName] = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        completion.TrySetResult(mark);
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public async Task<Milestone> Wait(string name, CancellationToken token)
    {
        Task<Milestone> task;
        lock (gate)
        {
            if (!marks.TryGetValue(name, out var completion)) marks[name] = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
        }
        var exit = Process.WaitForExitAsync(token);
        if (await Task.WhenAny(task, exit) != task) throw new IOException($"Process {Process.Id} exited before {name}.");
        return await task.WaitAsync(token);
    }

    public Dictionary<string, double> Times(long origin)
    {
        lock (gate) return marks.Where(pair => pair.Value.Task.IsCompletedSuccessfully && pair.Value.Task.Result.Status is not ("error" or "failed" or "unavailable")).ToDictionary(pair => pair.Key,
            pair => pair.Value.Task.Result.Timestamp * 1000.0 / pair.Value.Task.Result.Frequency - origin * 1000.0 / Stopwatch.Frequency);
    }
    public Milestone[] Observed()
    {
        lock (gate) return marks.Values.Where(value => value.Task.IsCompletedSuccessfully).Select(value => value.Task.Result).ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        if (group != 0) kill(-group, 15);
        else if (!Process.HasExited) kill(Process.Id, 15);
        try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        if (group != 0 && kill(-group, 0) == 0) kill(-group, 9);
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException)
        {
            Process.StandardOutput.Dispose();
            Process.StandardError.Dispose();
        }
        log.Dispose();
        Process.Dispose();
    }
}

internal static class Commands
{
    public static async Task<string> Run(string command, IEnumerable<string> arguments, string? directory = null, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory ?? Directory.GetCurrentDirectory() };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException(command);
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new IOException($"{command} exited {process.ExitCode}: {stderr}\n{stdout}");
        return stdout.Trim();
    }
}