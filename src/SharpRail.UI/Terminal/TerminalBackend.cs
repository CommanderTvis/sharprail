using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;

namespace SharpRail.UI.Terminal;

// SessionId names the host session a tab shows; ClientId names the window showing it.
public sealed record TerminalLaunch(string WorkspaceRoot, string SessionId, string ClipboardDirectory, string ClientId)
{
    // A tab's session is stable across windows, so another window of the app reattaches to the same shell.
    public static string SessionFor(string workspaceRoot, string tabId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot + "\n" + tabId)).AsSpan(0, 16));
}

// One attachment to a host terminal session and the control that presents it. Disposing detaches
// without ending the shell.
public interface ITerminalBackend : IDisposable
{
    Control View { get; }
    // Faults when the shell cannot start.
    Task Started { get; }
    // The shell's exit code; faults with a start failure reported only after launch or a lost connection.
    Task<int> Exited { get; }
    // Completes when another window or client took the session over.
    Task Detached { get; }
    ValueTask<bool> IsBusyAsync();
    // Ends the host session, as closing its tab does.
    ValueTask CloseAsync();
    void FocusTerminal();
}

public delegate ITerminalBackend TerminalFactory(TerminalLaunch launch);

public sealed class TerminalStartException(string message) : IOException(message);

public static class TerminalBackends
{
    public static ITerminalBackend Unavailable(string reason) => throw new TerminalStartException(reason);

    // Ghostty renders every tab; its child process is the relay, attached to a host session over gRPC:
    // the app's own host through a private socket, or a remote host.
    public static TerminalFactory Ghostty(Func<Task<RemoteTerminalConnection>> connection) => launch =>
    {
        if (!OperatingSystem.IsMacOS()) return Unavailable("Embedded terminals currently require macOS.");
        return new GhosttyTerminal(launch, connection());
    };

    public static TerminalFactory Ghostty(RemoteTerminalConnection connection) => Ghostty(() => Task.FromResult(connection));
}
