using System.Text;
using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks.E2E;

// Headless terminal tabs run real host PTY sessions; Ghostty needs a native window. Sessions belong to
// the host, as in the app, so windows of one E2E app share an instance and reattach to its sessions.
internal sealed class E2eTerminals(ITerminalService? inner = null) : ITerminalService
{
    private static readonly PtyTerminalService Pty = new("/bin/sh");
    private readonly ITerminalService inner = inner ?? Pty;
    internal PtyTerminalService? HostService => inner as PtyTerminalService;
    private readonly Queue<string> failures = [];
    private readonly HashSet<string> sessions = [];
    private TaskCompletionSource? gate;

    internal static TerminalFactory Plain => new E2eTerminals().Factory;
    internal TerminalFactory Factory => launch =>
    {
        var terminal = new HostTerminal(this, launch);
        Views.Add(terminal);
        return terminal;
    };
    // Every terminal body this app's windows created, with everything each received.
    internal List<HostTerminal> Views { get; } = [];
    // Attaches that started a shell.
    internal List<TerminalAttachRequest> Started { get; } = [];
    internal List<TerminalAttachRequest> Attaches { get; } = [];
    internal int Attempts { get; private set; }

    internal void Fail(string message, int times = 1)
    {
        for (var i = 0; i < times; i++) failures.Enqueue(message);
    }

    // Holds the next attach until the returned source completes, like a delayed attach reply.
    internal TaskCompletionSource Hold() => gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int StartedIn(string workspace) => Started.Count(request => request.WorkspaceRoot == workspace);

    public async ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        Attempts++;
        var held = gate; gate = null;
        if (held is not null) await held.Task.WaitAsync(cancellationToken);
        if (failures.TryDequeue(out var failure)) throw new IOException(failure);
        var session = await inner.AttachAsync(request, cancellationToken);
        lock (sessions)
        {
            Attaches.Add(request);
            if (session.Created) Started.Add(request);
            sessions.Add(request.SessionId);
        }
        return session;
    }

    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => inner.IsBusyAsync(sessionId, cancellationToken);

    public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => inner.CloseAsync(sessionId, cancellationToken);

    // Ends every session this app attached, as quitting the app does.
    internal void Quit()
    {
        string[] all;
        lock (sessions) { all = [.. sessions]; sessions.Clear(); }
        // Off the UI thread: the host awaits process exit, and a blocked dispatcher would never resume it.
        Task.Run(async () => { foreach (var id in all) await inner.CloseAsync(id); }).GetAwaiter().GetResult();
    }
}

internal sealed partial class HostTerminal : ITerminalBackend
{
    private readonly ITerminalService service;
    private readonly TerminalLaunch launch;
    private readonly StringBuilder text = new();
    private readonly SelectableTextBlock screen = new() { Name = "TerminalScreen", FontFamily = new FontFamily("Menlo"), TextWrapping = TextWrapping.Wrap };
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private ITerminalSession? session;

    internal HostTerminal(ITerminalService service, TerminalLaunch launch)
    {
        this.service = service; this.launch = launch;
        View = new ScrollViewer { Content = screen, Focusable = true, Name = "TerminalScreenViewport" };
        Started = StartAsync();
    }

    public Control View { get; }
    public Task Started { get; }
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;
    internal string Text => text.ToString();
    internal string SessionId => launch.SessionId;

    private async Task StartAsync()
    {
        session = await service.AttachAsync(new(launch.SessionId, launch.WorkspaceRoot, launch.ClientId, 120, 30) { TabKey = launch.TabKey }, lifetime.Token);
        if (lifetime.IsCancellationRequested) { await session.DisposeAsync(); return; }
        Show(session.Replay);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in session.ReadAsync(lifetime.Token)) Show(chunk);
                if (session.Detached.IsCompleted) { Dispatcher.UIThread.Post(() => detached.TrySetResult()); return; }
                var code = await session.Exit;
                Dispatcher.UIThread.Post(() => exited.TrySetResult(code));
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Dispatcher.UIThread.Post(() => exited.TrySetException(error)); }
        });
    }

    private void Show(ReadOnlyMemory<byte> chunk)
    {
        if (chunk.IsEmpty) return;
        var decoded = Encoding.UTF8.GetString(chunk.Span);
        Dispatcher.UIThread.Post(() => { text.Append(Plain(decoded)); screen.Text = text.ToString(); });
    }

    // Removes control sequences so assertions read the screen's text.
    private static string Plain(string output) => Controls().Replace(output, "").Replace("\r", "");

    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b\[[0-?]*[ -/]*[@-~]|\x1b[()][0-9A-Za-z]|\x1b[=>78]|[\x00-\x08\x0b\x0c\x0e-\x1f]")]
    private static partial Regex Controls();

    internal void Run(string command) => Send(command + "\r");

    internal void Send(string input)
    {
        if (session is null) throw new InvalidOperationException("The terminal has not started.");
        session.WriteAsync(Encoding.UTF8.GetBytes(input)).AsTask().GetAwaiter().GetResult();
    }

    public ValueTask<bool> IsBusyAsync() => service.IsBusyAsync(launch.SessionId);

    public ValueTask CloseAsync() => service.CloseAsync(launch.SessionId);

    public void FocusTerminal() => View.Focus();

    public void Write(string data) => Send(data);

    public string ReadScreen() => Text;
    public bool AgentNewline { get; private set; }
    public void SetAgentNewline(bool enabled) => AgentNewline = enabled;

    public void Dispose()
    {
        lifetime.Cancel();
        if (session is not null) _ = session.DisposeAsync().AsTask();
    }
}