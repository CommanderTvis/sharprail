using Avalonia.Controls;

namespace SharpRail.UI.Terminal;

public sealed record TerminalLaunch(string WorkspaceRoot, string SessionId, string ClipboardDirectory);

// One running terminal session and the control that presents it.
public interface ITerminalBackend : IDisposable
{
    Control View { get; }
    // Faults when the shell cannot start.
    Task Started { get; }
    // The shell's exit code; faults with a start failure reported only after launch.
    Task<int> Exited { get; }
    ValueTask<bool> IsBusyAsync();
    void FocusTerminal();
}

public delegate ITerminalBackend TerminalFactory(TerminalLaunch launch);

public sealed class TerminalStartException(string message) : IOException(message);

public static class TerminalBackends
{
    public static ITerminalBackend Unavailable(string reason) => throw new TerminalStartException(reason);

    // Local sessions run in Ghostty's own PTY; remote sessions run a relay against the host.
    public static TerminalFactory Ghostty(RemoteTerminalConnection? remote = null) => launch =>
    {
        if (!OperatingSystem.IsMacOS()) return Unavailable("Embedded terminals currently require macOS.");
        return remote is null ? GhosttyTerminal.Local(launch) : GhosttyTerminal.Remote(launch, remote);
    };
}
