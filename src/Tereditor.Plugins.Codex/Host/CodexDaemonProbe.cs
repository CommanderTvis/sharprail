using System.Diagnostics;
using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// Asks <c>codex app-server daemon version</c> whether the shared daemon runs the CLI's own version. It only reads:
/// the daemon is never started. Each read is bounded by a timeout, runs off the caller's thread, and is shared and
/// cached briefly so many launches do not each spawn a process.
/// </summary>
public sealed class CodexDaemonProbe(Func<string> command, TimeSpan? timeout = null, TimeSpan? lifetime = null)
{
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan lifetime = lifetime ?? TimeSpan.FromSeconds(10);
    private readonly Lock gate = new();
    private Task<CodexDaemonState>? current;
    private CodexDaemonState? last;
    private DateTimeOffset readAt;

    /// <summary>The newest finished answer, never waiting; null before the first read finishes. Starts a refresh when it is stale.</summary>
    public CodexDaemonState? Peek()
    {
        lock (gate)
        {
            if (last is null || DateTimeOffset.UtcNow - readAt >= lifetime) _ = ReadAsync();
            return last;
        }
    }

    public Task<CodexDaemonState> ReadAsync()
    {
        lock (gate)
        {
            if (current is { } running) return running;
            if (last is { } fresh && DateTimeOffset.UtcNow - readAt < lifetime) return Task.FromResult(fresh);
            return current = Task.Run(RunAsync);
        }

        async Task<CodexDaemonState> RunAsync()
        {
            var state = await ReadOnceAsync();
            lock (gate) { last = state; readAt = DateTimeOffset.UtcNow; current = null; }
            return state;
        }
    }

    /// <summary>Forgets the answer, for a changed command.</summary>
    public void Invalidate()
    {
        lock (gate) { last = null; }
    }

    public static CodexDaemonState Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Text("status") != "running") return new(false, $"the Codex daemon is {Text("status") ?? "not running"}");
            var (cli, server) = (Text("cliVersion"), Text("appServerVersion"));
            return !string.IsNullOrEmpty(cli) && cli == server ? new(true) : new(false, $"the Codex daemon runs {server ?? "an unknown version"}, not {cli ?? "this CLI's version"}");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return new(false, "the Codex daemon's state was unreadable"); }
    }

    private async Task<CodexDaemonState> ReadOnceAsync()
    {
        try
        {
            var start = new ProcessStartInfo(CodexAppServer.Executable(command()), ["app-server", "daemon", "version"])
            {
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start Codex");
            process.StandardInput.Close();
            using var cancel = new CancellationTokenSource(timeout);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
                _ = process.StandardError.ReadToEndAsync(cancel.Token);
                await process.WaitForExitAsync(cancel.Token);
                var text = await output;
                return process.ExitCode == 0 ? Parse(text) : new(false, "the Codex daemon is not running");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return new(false, "the Codex daemon did not answer in time");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new(false, "the Codex daemon could not be queried");
        }
    }
}