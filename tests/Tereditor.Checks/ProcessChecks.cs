using System.ComponentModel;
using System.Diagnostics;

using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>The bounded child-process runner and the Git calls built on it, against real children.</summary>
internal static class ProcessChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Task<ChildProcess.Result> Sh(string directory, string script, double seconds = 20, CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        ChildProcess.RunAsync("sh", directory, ["-c", script], TimeSpan.FromSeconds(seconds), ct, environment);

    private static bool Alive(string pidFile)
    {
        try { using var process = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim())); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task RequireGone(string pidFile, string message)
    {
        for (var attempt = 0; attempt < 100 && Alive(pidFile); attempt++) await Task.Delay(20);
        Require(!Alive(pidFile), message);
    }

    public static Task Run(string fixture) => OperatingSystem.IsWindows() ? Task.CompletedTask : RunPosix(fixture);

    private static async Task RunPosix(string fixture)
    {
        var directory = Path.Combine(fixture, "process-checks");
        Directory.CreateDirectory(directory);
        await Runner(directory);
        await Git(directory);
        Console.WriteLine("PASS bounded child runner: exit-driven completion, group kill on expiry and cancel, detached prompt-free Git with budgets");
    }

    private static async Task Runner(string directory)
    {
        var plain = await Sh(directory, "cat; printf 'out\\n'; printf 'err\\n' >&2; pwd -P; exit 3");
        Require(plain.ExitCode == 3 && plain.Error == "err\n", $"Exit status or stderr was lost: {plain.ExitCode} {plain.Error}");
        Require(plain.Output.StartsWith("out\n/", StringComparison.Ordinal) && plain.Output.EndsWith("/process-checks\n", StringComparison.Ordinal),
            "Standard output or the working directory was wrong: " + plain.Output);

        var bytes = await Sh(directory, "printf '\\377\\000\\376'");
        Require(bytes.Bytes.SequenceEqual(new byte[] { 0xff, 0x00, 0xfe }), "Opaque output was decoded.");

        var large = await Sh(directory, "head -c 3000000 /dev/zero; head -c 300000 /dev/zero >&2");
        Require(large.Bytes.Length == 3_000_000 && large.Error.Length == 300_000, "Both streams must be read from spawn, whatever their size.");

        // A grandchild that inherits the pipes outlives the child; success must not wait for it.
        var pidFile = Path.Combine(directory, "grandchild.pid");
        var clock = Stopwatch.StartNew();
        var held = await Sh(directory, $"(sleep 20) & echo $! > '{pidFile}'; echo done");
        Require(held.ExitCode == 0 && held.Output == "done\n", "A grandchild holding the pipes changed the result.");
        Require(clock.Elapsed < TimeSpan.FromSeconds(5), $"Completion waited for the pipes rather than the exit: {clock.Elapsed}.");
        Require(Alive(pidFile), "A successful child's descendants must not be killed.");
        Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))).Kill();

        var group = await Sh(directory, "echo $$; ps -o pgid= -p $$; ( : </dev/tty ) 2>/dev/null && echo tty");
        var lines = group.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(lines.Length == 2 && lines[0] == lines[1], "The child must lead its own session, with no terminal to prompt on: " + group.Output);

        clock.Restart();
        try
        {
            await Sh(directory, $"echo partial >&2; sleep 20 & echo $! > '{pidFile}'; wait", 0.4);
            throw new InvalidOperationException("An expired child completed.");
        }
        catch (ChildProcess.ExpiredException error)
        {
            Require(error.Error == "partial\n", "Expiry dropped what the child had written.");
            Require(error.Waited >= TimeSpan.FromSeconds(0.4) && clock.Elapsed < TimeSpan.FromSeconds(5), $"Expiry was not prompt: {clock.Elapsed}.");
        }
        await RequireGone(pidFile, "Expiry left the child's process group running.");

        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            try
            {
                await Sh(directory, $"sleep 20 & echo $! > '{pidFile}'; wait", 20, cancel.Token);
                throw new InvalidOperationException("A cancelled child completed.");
            }
            catch (OperationCanceledException) { }
            await RequireGone(pidFile, "Cancellation left the child's process group running.");
        }

        // The environment is read at each spawn, so a repair made after startup reaches later children.
        Environment.SetEnvironmentVariable("SHARPRAIL_CHECK_LIVE", "late");
        Environment.SetEnvironmentVariable("SHARPRAIL_CHECK_DROPPED", "present");
        try
        {
            var live = await Sh(directory, "echo \"$SHARPRAIL_CHECK_LIVE/${SHARPRAIL_CHECK_DROPPED-unset}/$SHARPRAIL_CHECK_ADDED\"",
                environment: new Dictionary<string, string?> { ["SHARPRAIL_CHECK_DROPPED"] = null, ["SHARPRAIL_CHECK_ADDED"] = "added" });
            Require(live.Output == "late/unset/added\n", "The child did not see the live environment with its overrides: " + live.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPRAIL_CHECK_LIVE", null);
            Environment.SetEnvironmentVariable("SHARPRAIL_CHECK_DROPPED", null);
        }

        try
        {
            await ChildProcess.RunAsync("sharprail-no-such-program", directory, [], TimeSpan.FromSeconds(5), CancellationToken.None);
            throw new InvalidOperationException("A missing program started.");
        }
        catch (Win32Exception) { }

        var zero = Stopwatch.StartNew();
        try { await Sh(directory, "sleep 20", -5); throw new InvalidOperationException("A negative budget ran to completion."); }
        catch (ChildProcess.ExpiredException) { }
        Require(zero.Elapsed < TimeSpan.FromSeconds(5), "A negative budget must clamp to an immediate expiry.");
    }

    private static async Task Git(string directory)
    {
        var repo = Path.Combine(directory, "repo");
        Directory.CreateDirectory(repo);
        await GitRepository.RunAsync(repo, CancellationToken.None, "init", "-q", "-b", "main");

        var env = await GitRepository.RunAsync(repo, CancellationToken.None, "-c", "alias.env=!env", "env");
        Require(env.Split('\n').Contains("GIT_TERMINAL_PROMPT=0"), "Git children must run with terminal prompts disabled.");

        var slow = TimeSpan.FromSeconds(0.5);
        try
        {
            await GitRepository.ExecuteAsync(repo, ["-c", "alias.hang=!sleep 20", "hang"], slow, CancellationToken.None);
            throw new InvalidOperationException("A stalled Git call completed.");
        }
        catch (IOException error)
        {
            Require(error.Message == "timed out after 1s — git did not exit", "A silent local stall must name only what was observed: " + error.Message);
        }

        try
        {
            await GitRepository.ExecuteAsync(repo, ["-c", "alias.hang=!echo 'remote: busy' >&2; sleep 20", "hang"], slow, CancellationToken.None);
            throw new InvalidOperationException("A stalled Git call completed.");
        }
        catch (IOException error)
        {
            Require(error.Message == "timed out after 1s — remote: busy", "A timeout must keep what Git wrote: " + error.Message);
        }

        try
        {
            await GitRepository.ExecuteAsync(repo, ["fetch", "--upload-pack=sleep 20;", repo], slow, CancellationToken.None);
            throw new InvalidOperationException("A stalled fetch completed.");
        }
        catch (IOException error)
        {
            Require(error.Message.StartsWith("timed out after 1s — the remote never answered", StringComparison.Ordinal) &&
                error.Message.Contains("ssh-add", StringComparison.Ordinal), "A silent network stall must carry the SSH hint: " + error.Message);
        }

        try { await GitRepository.RunAsync(repo, CancellationToken.None, "rev-parse", "--verify", "no-such-ref"); throw new InvalidOperationException("A bad ref resolved."); }
        catch (IOException error) { Require(error.Message.Contains("fatal", StringComparison.Ordinal), "Git's own failure text must survive: " + error.Message); }
    }
}