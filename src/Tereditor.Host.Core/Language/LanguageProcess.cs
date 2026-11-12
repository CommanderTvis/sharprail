using System.Diagnostics;

namespace SharpRail.Host.Core.Language;

/// <summary>What a language connection needs of whatever runs a server: framed bytes in and out, and whether it is still alive.</summary>
public interface ILanguageProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync();
    void Kill();
    /// <summary>Where the client writes the server's input: JSON-RPC messages framed with <c>Content-Length</c>.</summary>
    Stream Input { get; }
    /// <summary>The server's output, framed the same way.</summary>
    Stream Output { get; }
    /// <summary>Its diagnostic text; empty for a server with none.</summary>
    TextReader Errors { get; }
}

/// <summary>What a host without processes lends the language service: a place for a server to run instead of a child process.</summary>
public static class LanguageLinks
{
    /// <summary>
    /// Starts the server with this catalog id for a workspace inside the host's own page, or answers null for a server it cannot
    /// run, which then fails to start. A browser tab's host sets this.
    /// </summary>
    public static Func<string, string, CancellationToken, Task<ILanguageProcess?>>? InTab { get; set; }
}

/// <summary>A child process of the host's operating system.</summary>
internal sealed class OsLanguageProcess(Process process) : ILanguageProcess
{
    internal Process Process => process;
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public Task WaitForExitAsync() => process.WaitForExitAsync();
    public void Kill() => process.Kill(true);
    public Stream Input => process.StandardInput.BaseStream;
    public Stream Output => process.StandardOutput.BaseStream;
    public TextReader Errors => process.StandardError;
    public void Dispose() => process.Dispose();
}