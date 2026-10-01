using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ghostty.Avalonia;
using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Terminal;

// The Skia renderer: libghostty-vt in the app, attached straight to the host session without a relay.
internal sealed class SkiaTerminal : Border, ITerminalBackend
{
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource arranged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    // Input keeps its order across the asynchronous writes of a remote session.
    private readonly Channel<ReadOnlyMemory<byte>> input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new() { SingleReader = true });
    private readonly ITerminalService terminals;
    private readonly TerminalLaunch launch;
    private readonly GhosttySkiaView terminal;
    private ITerminalSession? session;
    private bool disposed;

    internal SkiaTerminal(TerminalLaunch launch, ITerminalService terminals)
    {
        this.launch = launch; this.terminals = terminals;
        Focusable = true;
        try
        {
            terminal = new GhosttySkiaView
            {
                Typeface = TerminalTheme.Typeface.Value,
                Colors = TerminalTheme.Colors(),
                ClipboardImageDirectory = launch.ClipboardDirectory,
                Name = "TerminalSkiaView"
            };
        }
        catch (DllNotFoundException)
        {
            throw new TerminalStartException("The Skia terminal renderer is not available on this platform.");
        }
        terminal.Input += (_, data) => input.Writer.TryWrite(data);
        terminal.GridResized += (_, size) => { if (session is { } attached) _ = Resize(attached, size); };
        terminal.LayoutUpdated += Arranged;
        Child = terminal;
        Ui.ThemeChanged += UpdateColors;
        Started = StartAsync();
    }

    public Control View => this;
    public Task Started { get; }
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;
    internal GhosttySkiaView Terminal => terminal;

    // The shell starts at the grid's real size, so its first prompt and the replay fit the view.
    private void Arranged(object? sender, EventArgs e)
    {
        if (terminal.Bounds.Width <= 0) return;
        terminal.LayoutUpdated -= Arranged;
        arranged.TrySetResult();
    }

    private async Task StartAsync()
    {
        await arranged.Task.WaitAsync(lifetime.Token);
        var size = terminal.Size;
        ITerminalSession attached;
        try { attached = await terminals.AttachAsync(new(launch.SessionId, launch.WorkspaceRoot, launch.ClientId, size.Columns, size.Rows), lifetime.Token); }
        catch (Grpc.Core.RpcException error) { throw new TerminalStartException(error.Status.Detail); }
        if (disposed) { await attached.DisposeAsync(); throw new ObjectDisposedException(nameof(SkiaTerminal)); }
        session = attached;
        terminal.Write(attached.Replay.Span);
        if (terminal.Size != size) _ = Resize(attached, terminal.Size);
        _ = Task.Run(() => Pump(attached));
        _ = Task.Run(() => Send(attached));
    }

    private async Task Pump(ITerminalSession attached)
    {
        try
        {
            await foreach (var chunk in attached.ReadAsync(lifetime.Token))
            {
                var bytes = chunk.ToArray();
                Dispatcher.UIThread.Post(() => { if (!disposed) terminal.Write(bytes); });
            }
            if (attached.Detached.IsCompleted) { detached.TrySetResult(); return; }
            exited.TrySetResult(await attached.Exit);
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            var message = error is Grpc.Core.RpcException rpc ? "The terminal connection was lost: " + rpc.Status.Detail : error.Message;
            exited.TrySetException(new TerminalStartException(message));
        }
    }

    private async Task Send(ITerminalSession attached)
    {
        try
        {
            await foreach (var data in input.Reader.ReadAllAsync(lifetime.Token)) await attached.WriteAsync(data, lifetime.Token);
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(new TerminalStartException("Terminal input failed: " + error.Message)); }
    }

    private async Task Resize(ITerminalSession attached, TerminalSize size)
    {
        try { await attached.ResizeAsync(size.Columns, size.Rows, lifetime.Token); }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(new TerminalStartException("Terminal resize failed: " + error.Message)); }
    }

    private void UpdateColors() => terminal.Colors = TerminalTheme.Colors();

    public ValueTask<bool> IsBusyAsync() =>
        exited.Task.IsCompleted || detached.Task.IsCompleted || session is null ? ValueTask.FromResult(false) : terminals.IsBusyAsync(launch.SessionId);

    public ValueTask CloseAsync() => terminals.CloseAsync(launch.SessionId);

    public void FocusTerminal() => terminal.FocusTerminal();

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ui.ThemeChanged -= UpdateColors;
        lifetime.Cancel();
        input.Writer.TryComplete();
        if (session is not null) _ = session.DisposeAsync().AsTask();
        terminal.Dispose();
    }
}
