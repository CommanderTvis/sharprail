using System.Diagnostics;
using System.Text;

namespace Tereditor.Plugins.ClaudeCode.Host.Chat;

/// <summary>What a bash-mode command printed (each stream cut at <see cref="ChatShell.OutputLimit"/>) and how it ended; <see cref="ExitCode"/> is null when it was stopped.</summary>
internal sealed record ChatShellResult(string Stdout, string Stderr, int? ExitCode, bool Stopped);

/// <summary>
/// One bash-mode command: the user's shell runs it in the chat's directory with the environment a terminal gets, its output is read as it
/// arrives (bounded), and cancelling ends the whole process group. Nothing here waits on the child.
/// </summary>
internal static class ChatShell
{
    /// <summary>The most characters kept of each stream; the rest is counted and named in a note.</summary>
    internal const int OutputLimit = ChatContract.OutputLimit;

    private sealed class Capture
    {
        private readonly StringBuilder text = new();
        private readonly Lock gate = new();
        private long dropped;

        public void Add(ReadOnlySpan<char> chunk)
        {
            lock (gate)
            {
                var room = OutputLimit - text.Length;
                if (room > 0) text.Append(chunk[..Math.Min(room, chunk.Length)]);
                if (chunk.Length > Math.Max(room, 0)) dropped += chunk.Length - Math.Max(room, 0);
            }
        }

        public string Text()
        {
            lock (gate) return text.ToString() + (dropped > 0 ? $"\n… {dropped} more characters not shown" : "");
        }
    }

    /// <summary>The program and arguments that run <paramref name="command"/>: the login shell as <c>-l -c</c> (so the user's profile applies), cmd on Windows.</summary>
    internal static (string Program, string[] Arguments) Invocation(string command, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> leading)
    {
        if (OperatingSystem.IsWindows())
            return (environment.GetValueOrDefault("ComSpec") is { Length: > 0 } cmd ? cmd : "cmd.exe", ["/d", "/s", "/c", command]);
        var shell = environment.GetValueOrDefault("SHELL") is { Length: > 0 } configured && File.Exists(configured) ? configured : "/bin/sh";
        var fish = Path.GetFileName(shell) == "fish";
        // A login shell rebuilds PATH from the user's profile; the directories the workspace's environment put in front stay in front.
        var path = leading.Count == 0 ? "" : fish
            ? "set -gx PATH " + string.Join(' ', leading.Select(entry => "'" + entry.Replace("\\", "\\\\").Replace("'", "\\'") + "'")) + " $PATH\n"
            : "export PATH=" + string.Join(':', leading.Select(entry => "'" + entry.Replace("'", "'\\''") + "'")) + ":\"$PATH\"\n";
        return (shell, ["-l", "-c", path + command]);
    }

    /// <summary>The entries of the environment's PATH the host's own lacks, in order: what the workspace's steps added.</summary>
    internal static IReadOnlyList<string> AddedPath(IReadOnlyDictionary<string, string> environment)
    {
        var own = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return [.. (environment.GetValueOrDefault("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Where(entry => !own.Contains(entry) && !entry.Contains('\0')).Distinct()];
    }

    /// <summary>Runs the command; <paramref name="progress"/> gets the output so far after each chunk. Cancelling <paramref name="stop"/> ends the group and settles the result as stopped.</summary>
    public static async Task<ChatShellResult> RunAsync(string command, IReadOnlyDictionary<string, string> environment, string cwd, Action<string, string> progress, CancellationToken stop)
    {
        var (program, arguments) = Invocation(command, environment, OperatingSystem.IsWindows() ? [] : AddedPath(environment));
        var grouped = !OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(grouped ? ChatProcess.GroupLauncher : program)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (grouped) start.ArgumentList.Add(ChatProcess.ResolveUnixExecutable(program, cwd));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the shell.");
        // The command reads no input: `cat` ends at once instead of waiting for a user who cannot type to it.
        try { child.StandardInput.Close(); } catch (IOException) { }
        var output = new Capture();
        var errors = new Capture();
        void Report() => progress(output.Text(), errors.Text());
        async Task Pump(StreamReader reader, Capture into)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
                {
                    into.Add(buffer.AsSpan(0, read));
                    Report();
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
        var pumps = Task.WhenAll(Pump(child.StandardOutput, output), Pump(child.StandardError, errors));
        var stopped = false;
        using (stop.Register(() => { stopped = true; _ = Terminate(child); }))
        {
            await child.WaitForExitAsync(CancellationToken.None);
            // A grandchild that kept the pipes open must not hold the card open once the shell is gone.
            await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        return new ChatShellResult(output.Text(), errors.Text(), stopped ? null : child.ExitCode, stopped);
    }

    private static async Task Terminate(Process child)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { child.Kill(entireProcessTree: true); return; }
            var group = -child.Id;
            _ = ChatProcess.Kill(group, ChatProcess.SigTerm);
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline && ChatProcess.Kill(group, 0) == 0) await Task.Delay(50);
            _ = ChatProcess.Kill(group, ChatProcess.SigKill);
            if (!child.HasExited) child.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}