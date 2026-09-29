using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks.E2E;

// Headless terminal tabs run real host PTY sessions; Ghostty needs a native window.
internal sealed class E2eTerminals(ITerminalService? inner = null) : ITerminalService
{
    private static readonly PtyTerminalService Pty = new("/bin/sh");
    private readonly ITerminalService inner = inner ?? Pty;
    private readonly Queue<string> failures = [];
    private TaskCompletionSource? gate;

    internal static TerminalFactory Plain => new E2eTerminals().Factory;
    internal TerminalFactory Factory => launch => new HostTerminal(this, launch);
    internal List<TerminalStartRequest> Started { get; } = [];
    internal int Attempts { get; private set; }

    internal void Fail(string message, int times = 1)
    {
        for (var i = 0; i < times; i++) failures.Enqueue(message);
    }

    // Holds the next start until the returned source completes, like a delayed attach reply.
    internal TaskCompletionSource Hold() => gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int StartedIn(string workspace) => Started.Count(request => request.WorkspaceRoot == workspace);

    public async ValueTask<ITerminalSession> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default)
    {
        Attempts++;
        var held = gate; gate = null;
        if (held is not null) await held.Task.WaitAsync(cancellationToken);
        if (failures.TryDequeue(out var failure)) throw new IOException(failure);
        var session = await inner.StartAsync(request, cancellationToken);
        Started.Add(request);
        return session;
    }

    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => inner.IsBusyAsync(sessionId, cancellationToken);
}

internal sealed partial class HostTerminal : ITerminalBackend
{
    private readonly ITerminalService service;
    private readonly TerminalLaunch launch;
    private readonly StringBuilder text = new();
    private readonly SelectableTextBlock screen = new() { Name = "TerminalScreen", FontFamily = new FontFamily("Menlo"), TextWrapping = TextWrapping.Wrap };
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    internal string Text => text.ToString();
    internal string SessionId => launch.SessionId;

    private async Task StartAsync()
    {
        session = await service.StartAsync(new(launch.SessionId, launch.WorkspaceRoot, 120, 30), lifetime.Token);
        if (lifetime.IsCancellationRequested) { await session.DisposeAsync(); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in session.ReadAsync(lifetime.Token))
                {
                    var decoded = Encoding.UTF8.GetString(chunk.Span);
                    Dispatcher.UIThread.Post(() => { text.Append(Plain(decoded)); screen.Text = text.ToString(); });
                }
                var code = await session.Exit;
                Dispatcher.UIThread.Post(() => exited.TrySetResult(code));
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        });
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

    public void FocusTerminal() => View.Focus();

    public void Dispose()
    {
        lifetime.Cancel();
        if (session is not null) _ = session.DisposeAsync().AsTask();
    }
}
