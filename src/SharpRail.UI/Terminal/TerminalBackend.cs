using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

// SessionId names the host session a tab shows; ClientId names the window showing it.
public sealed record TerminalLaunch(string WorkspaceRoot, string SessionId, string ClipboardDirectory, string ClientId)
{
    // The tab's layout id; with the workspace root it is the TerminalRef plugins see.
    public string TabKey { get; init; } = "";
    // Attach only when no other client holds the session; a tab created here, or taken over, attaches plainly.
    public bool Yield { get; init; }

    // A tab's session is the same for every window and client of the host, so each reaches the same shell.
    public static string SessionFor(string workspaceRoot, string tabId) => TerminalTab.SessionFor(workspaceRoot, tabId);
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
    // True when Detached completed because a yielding attach found the session held by another client.
    bool Yielded { get; }
    ValueTask<bool> IsBusyAsync();
    // Ends the host session, as closing its tab does.
    ValueTask CloseAsync();
    void FocusTerminal();
    // Types into the shell as a user would; plugin accessories write through it.
    void Write(string data);
    // The screen and scrollback as plain text.
    string ReadScreen();
    // Agent Shift+Enter fallback; negotiated keyboard protocols remain the renderer's own.
    void SetAgentNewline(bool enabled);
}

public delegate ITerminalBackend TerminalFactory(TerminalLaunch launch);

public sealed class TerminalStartException(string message) : IOException(message);

// Ghostty's texture composed by Avalonia, or libghostty-vt cells drawn with Skia. Android has only Skia.
public static class TerminalRenderers
{
    public const string Texture = "texture";
    public const string Skia = "skia";
}

public static class TerminalBackends
{
    public static ITerminalBackend Unavailable(string reason) => throw new TerminalStartException(reason);

    private static bool Skia(Func<string>? renderer) => OperatingSystem.IsAndroid() || renderer?.Invoke() == TerminalRenderers.Skia;

    // This overload has no endpoint or relay factory: both renderers call the supplied host directly.
    public static TerminalFactory Ghostty(ITerminalService terminals, Func<string>? renderer = null) => launch =>
    {
        if (Skia(renderer)) return new SkiaTerminal(launch, terminals);
        if (!OperatingSystem.IsMacOS()) return Unavailable("Embedded terminals currently require macOS.");
        return new DirectGhosttyTerminal(launch, terminals);
    };

    public static TerminalFactory Ghostty(RemoteTerminalConnection connection, Func<string>? renderer = null) => launch =>
    {
        if (Skia(renderer)) return new SkiaTerminal(launch, connection.Terminals);
        if (!OperatingSystem.IsMacOS()) return Unavailable("Embedded terminals currently require macOS.");
        return new GhosttyTerminal(launch, Task.FromResult(connection));
    };
}