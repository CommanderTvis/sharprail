using System.Diagnostics;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>What starts an agent: the program, its arguments and environment, and the directory it runs in.</summary>
internal sealed record AcpLaunch(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, string Cwd);

/// <summary>The two byte streams of one agent connection and the ability to end it.</summary>
internal interface IAcpTransport : IAsyncDisposable
{
    /// <summary>What the agent writes (its stdout).</summary>
    Stream FromAgent { get; }
    /// <summary>What the client writes (the agent's stdin).</summary>
    Stream ToAgent { get; }
    /// <summary>The last lines the agent wrote to its error output, for a failure message.</summary>
    string ErrorTail { get; }
}

/// <summary>An agent child process speaking ACP on stdio. The agent is untrusted code: it runs in the workspace directory with its configured environment only added to the host's.</summary>
internal sealed class ProcessAcpTransport : IAcpTransport
{
    private readonly Process process;
    private readonly Queue<string> errors = new();

    private ProcessAcpTransport(Process process)
    {
        this.process = process;
        process.ErrorDataReceived += (_, data) =>
        {
            if (data.Data is not { Length: > 0 } text) return;
            lock (errors)
            {
                errors.Enqueue(text);
                while (errors.Count > 8) errors.Dequeue();
            }
        };
        process.BeginErrorReadLine();
    }

    public static async Task<ProcessAcpTransport> StartAsync(AcpLaunch launch)
    {
        var start = new ProcessStartInfo(launch.Command, launch.Args)
        {
            WorkingDirectory = launch.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (key, value) in launch.Env) start.Environment[key] = value;
        var process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException($"{launch.Command} could not be started.");
        return new ProcessAcpTransport(process);
    }

    public Stream FromAgent => process.StandardOutput.BaseStream;
    public Stream ToAgent => process.StandardInput.BaseStream;

    public string ErrorTail
    {
        get { lock (errors) return string.Join(" ", errors); }
    }

    public async ValueTask DisposeAsync()
    {
        try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1.5)); }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}