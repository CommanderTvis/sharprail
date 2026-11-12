namespace Tereditor.Host.Core;

/// <summary>One finished Git command: its exit code, undecoded standard output and the error text.</summary>
public sealed record GitBackendResult(int ExitCode, byte[] Output, string Error);

/// <summary>Answers Git command lines for a host that cannot start a process, such as one running in a browser tab.</summary>
public interface IGitBackend
{
    /// <summary>Runs <paramref name="arguments"/> as <c>git</c> would in <paramref name="workingDirectory"/>, with <paramref name="input"/> on standard input.</summary>
    Task<GitBackendResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, byte[]? input, CancellationToken cancellationToken);
}

/// <summary>The host-wide Git backend; null runs the installed <c>git</c> program.</summary>
public static class GitBackends
{
    public static IGitBackend? Current { get; set; }
}