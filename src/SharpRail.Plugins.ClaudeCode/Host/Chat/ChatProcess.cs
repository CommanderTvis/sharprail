using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>
/// One headless Claude Code child: a stdout line reader, a serialized stdin writer, captured stderr and a kill that takes
/// the whole process tree. Everything is asynchronous; nothing waits on the child.
/// </summary>
internal sealed class ChatProcess
{
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const int MaxLine = 64 * 1024 * 1024;

    private readonly Process child;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private readonly Task reading;
    private int closing;

    /// <summary>Completes with the exit code once stdout has ended and the child is reaped.</summary>
    public Task<int> Exited { get; }

    private ChatProcess(Process child, Func<string, Task> onLine, Action<string> onError)
    {
        this.child = child;
        child.ErrorDataReceived += (_, data) => { if (data.Data is { Length: > 0 } text) onError(text); };
        child.BeginErrorReadLine();
        reading = Task.Run(async () =>
        {
            try
            {
                while (await child.StandardOutput.ReadLineAsync(stopped.Token) is { } line)
                {
                    if (line.Length is 0 or > MaxLine) continue;
                    await onLine(line);
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        });
        Exited = Task.Run(async () =>
        {
            await reading;
            try { await child.WaitForExitAsync(); return child.ExitCode; }
            catch (InvalidOperationException) { return -1; }
        });
    }

    /// <summary>Starts the child; the caller runs this off the UI thread.</summary>
    public static ChatProcess Start(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string cwd, Func<string, Task> onLine, Action<string> onError)
    {
        // Off Windows the child runs behind the setsid launcher, so it leads a group the whole of which can be signalled.
        var grouped = !OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(grouped ? Path.Combine(Path.GetDirectoryName(typeof(ChatProcess).Assembly.Location)!, "sharprail-claude-process-group") : executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        if (grouped) start.ArgumentList.Add(ResolveUnixExecutable(executable, cwd));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start Claude Code.");
        return new ChatProcess(child, onLine, onError);
    }

    public async Task WriteAsync(string line, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            await child.StandardInput.WriteAsync((line + "\n").AsMemory(), cancellationToken);
            await child.StandardInput.FlushAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    /// <summary>Closes stdin, gives the child a moment to leave, then kills what is left of its tree.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) == 1) { await Exited; return; }
        try { child.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1.5)); }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
        await TerminateAsync();
        stopped.Cancel();
        try { await Exited.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
    }

    private async Task TerminateAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return;
        }
        // The launcher made the child a session leader, so its pid names the group and everything it started.
        var group = -child.Id;
        var deadline = DateTime.UtcNow.AddSeconds(1);
        _ = Kill(group, SigTerm);
        while (DateTime.UtcNow < deadline && Kill(group, 0) == 0) await Task.Delay(50);
        _ = Kill(group, SigKill);
        try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
    }

    private static string ResolveUnixExecutable(string executable, string workingDirectory)
    {
        var candidates = executable.Contains('/') ? new[] { Path.GetFullPath(executable, workingDirectory) }
            : (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':')
                .Select(directory => Path.GetFullPath(Path.Combine(directory.Length == 0 ? workingDirectory : directory, executable), workingDirectory));
        foreach (var candidate in candidates)
            if (!Directory.Exists(candidate) && Access(candidate, 1) == 0) return candidate;
        throw new System.ComponentModel.Win32Exception(2);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string path, int mode);
}