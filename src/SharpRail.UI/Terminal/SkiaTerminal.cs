using System.Diagnostics;
using System.Threading.Channels;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Terminal;

// The Skia renderer: libghostty-vt in the app, attached straight to the host session without a relay. It is
// also every renderer's view of a terminal another client holds: the holder's grid at its real size, scrolled
// from the top-left when the pane is smaller, taking no input and answering no terminal query.
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
        terminal.Input += (_, data) => { if (!Watching) input.Writer.TryWrite(data); };
        terminal.GridResized += (_, size) => { if (session is { Watching: false } attached) _ = Resize(attached, size); };
        terminal.LayoutUpdated += Arranged;
        Child = terminal;
        Ui.ThemeChanged += UpdateColors;
        Started = StartAsync();
    }

    public Control View => this;
    public Task Started { get; }
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;
    public bool Yielded { get; private set; }
    public bool Watching { get; private set; }
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
        try
        {
            attached = await terminals.AttachAsync(new(launch.SessionId, launch.WorkspaceRoot, launch.ClientId, size.Columns, size.Rows)
            { TabKey = launch.TabKey, Yield = launch.Yield, Watch = launch.Yield }, lifetime.Token);
        }
        catch (Grpc.Core.RpcException error) { throw new TerminalStartException(error.Status.Detail); }
        if (disposed) { await attached.DisposeAsync(); throw new ObjectDisposedException(nameof(SkiaTerminal)); }
        session = attached;
        Yielded = launch.Yield && attached.Detached.IsCompleted;
        if (attached.Watching) Watch(attached.Grid);
        await WriteOutput(attached.Replay);
        if (!Watching && terminal.Size != size) _ = Resize(attached, terminal.Size);
        _ = Task.Run(() => Pump(attached));
        _ = Task.Run(() => Send(attached));
    }

    // The surface leaves the keyboard and the pointer alone, so nothing reaches the shell and no soft keyboard
    // opens for it; the scroller around it takes the pan and wheel gestures.
    private void Watch((int Columns, int Rows) grid)
    {
        Watching = true;
        terminal.FixedGrid = new(grid.Columns, grid.Rows);
        terminal.Focusable = false; terminal.IsHitTestVisible = false;
        terminal.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        terminal.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Focusable = false;
        Child = null;
        Child = new ScrollViewer
        {
            Name = "TerminalWatched",
            Content = terminal,
            Background = Ui.Terminal,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
    }

    private async Task Pump(ITerminalSession attached)
    {
        try
        {
            // A revived agent's command is typed once the shell has printed something, so it never lands before the prompt.
            var prefill = attached.Prefill;
            var grid = attached.Grid;
            await foreach (var chunk in attached.ReadAsync(lifetime.Token))
            {
                // The holder's resize reaches a watcher in order with the output drawn for it.
                if (Watching && attached.Grid != grid)
                {
                    grid = attached.Grid;
                    await Dispatcher.UIThread.InvokeAsync(() => { if (!disposed) terminal.FixedGrid = new(grid.Columns, grid.Rows); }, DispatcherPriority.Background);
                }
                await WriteOutput(chunk);
                if (prefill is not null && chunk.Length > 0)
                {
                    await attached.WriteAsync(System.Text.Encoding.UTF8.GetBytes(prefill.Text + (prefill.Submit ? "\r" : "")), lifetime.Token);
                    prefill = null;
                }
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

    // RoyalTerminal's bounded output slices inspired this drain. See Ghostty.Avalonia/README.md.
    // Await each dispatch so a flood cannot create an unbounded queue of UI callbacks.
    private async Task WriteOutput(ReadOnlyMemory<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var started = Stopwatch.GetTimestamp();
                var end = Math.Min(bytes.Length, offset + 8 * 1024);
                do
                {
                    var length = Math.Min(1024, end - offset);
                    terminal.Write(bytes.Span.Slice(offset, length));
                    offset += length;
                }
                while (offset < end && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2);
            }, DispatcherPriority.Background);
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

    public void Write(string data) { if (!Watching) input.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(data)); }

    public string ReadScreen() => terminal.ReadScreen();

    public void SetAgentNewline(bool enabled) => terminal.AgentNewline = enabled;

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