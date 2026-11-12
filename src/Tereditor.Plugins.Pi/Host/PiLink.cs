using System.Diagnostics;
using System.Text;

namespace SharpRail.Plugins.Pi.Host;

/// <summary>The two ends of one Pi RPC conversation: LF-delimited JSON lines in each direction.</summary>
public interface IPiLink : IDisposable
{
    bool Alive { get; }
    /// <summary>The next line Pi wrote, or null once it has ended.</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);
    /// <summary>Ends the conversation, giving Pi a moment to finish before it is forced.</summary>
    ValueTask CloseAsync();
}

/// <summary>What a host without processes lends the Pi plugin: where Pi runs instead of a child process.</summary>
public static class PiLinks
{
    /// <summary>
    /// Starts Pi inside the host's own page (working directory, session to continue, model, thinking level), or null
    /// to run the installed <c>pi</c> program. A browser tab's host sets this.
    /// </summary>
    public static Func<string, string?, string?, string?, CancellationToken, Task<IPiLink>>? InTab { get; set; }

    /// <summary>The version of the Pi that <see cref="InTab"/> runs, for the plugin's pane.</summary>
    public static Func<Task<string?>>? InTabVersion { get; set; }
}

/// <summary>A <c>pi --mode rpc</c> child process.</summary>
internal sealed class ProcessPiLink : IPiLink
{
    private readonly Process process;

    private ProcessPiLink(Process process) => this.process = process;

    public static async Task<ProcessPiLink> StartAsync(PiSpawn spawn, Action<string> error)
    {
        var arguments = new List<string> { "--mode", "rpc" };
        if (spawn.Session is { Length: > 0 } session) arguments.AddRange(["--session", session]);
        if (spawn.Model is { Length: > 0 } model) arguments.AddRange(["--model", model]);
        var start = new ProcessStartInfo(spawn.Command, arguments)
        {
            WorkingDirectory = spawn.Cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false)
        };
        var process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException("Pi could not be started.");
        process.ErrorDataReceived += (_, data) => { if (data.Data is { Length: > 0 } text) error(text); };
        process.BeginErrorReadLine();
        return new(process);
    }

    public bool Alive => !process.HasExited;

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => await process.StandardOutput.ReadLineAsync(cancellationToken);

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteAsync((line + "\n").AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    public async ValueTask CloseAsync()
    {
        try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1.5)); }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    public void Dispose() => process.Dispose();
}