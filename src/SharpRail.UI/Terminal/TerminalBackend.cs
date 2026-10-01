using System.Security.Cryptography;
using System.Text;

using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

// SessionId names the host session a tab shows; ClientId names the window showing it.
public sealed record TerminalLaunch(string WorkspaceRoot, string SessionId, string ClipboardDirectory, string ClientId)
{
    // The tab's layout id; with the workspace root it is the TerminalRef plugins see.
    public string TabKey { get; init; } = "";

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
    // Types into the shell as a user would; plugin accessories write through it.
    void Write(string data);
    // The screen and scrollback as plain text.
    string ReadScreen();
}

public delegate ITerminalBackend TerminalFactory(TerminalLaunch launch);

public sealed class TerminalStartException(string message) : IOException(message);

// Ghostty's texture composed by Avalonia, or libghostty-vt cells drawn with Skia.
public static class TerminalRenderers
{
    public const string Texture = "texture";
    public const string Skia = "skia";
}

public static class TerminalBackends
{
    public static ITerminalBackend Unavailable(string reason) => throw new TerminalStartException(reason);

    // Ghostty draws every tab, chosen per tab when it starts. The texture renderer's child process is the relay,
    // attached to a host session over gRPC: the app's own host through a private socket, or a remote host.
    // The Skia renderer attaches to the same sessions in-process.
    public static TerminalFactory Ghostty(ITerminalService terminals, Func<Task<RemoteTerminalConnection>> relay, Func<string> renderer) => launch =>
    {
        if (renderer() == TerminalRenderers.Skia) return new SkiaTerminal(launch, terminals);
        if (!OperatingSystem.IsMacOS()) return Unavailable("Embedded terminals currently require macOS.");
        return new GhosttyTerminal(launch, relay());
    };

    public static TerminalFactory Ghostty(RemoteTerminalConnection connection, Func<string>? renderer = null) =>
        Ghostty(connection.Terminals, () => Task.FromResult(connection), renderer ?? (() => TerminalRenderers.Texture));
}