using System.Diagnostics;
using System.Threading.Channels;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Terminal;

// The Skia renderer: libghostty-vt in the app, attached straight to the host session without a relay. It is
// also every renderer's view of a terminal another client holds: the holder's grid at its real size, scrolled
// from the top-left when the pane is smaller, taking no input and answering no terminal query.
internal sealed class SkiaTerminal : Border, ITerminalBackend, ITerminalResumeHint, ITerminalOutput
{
    public event Action? Output;
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
    private readonly double baseFontSize;
    private double fontSizeOffset;

    internal SkiaTerminal(TerminalLaunch launch, ITerminalService terminals, TerminalResumeHint? resumeHint = null)
    {
        ResumeHint = resumeHint ?? new();
        this.launch = launch; this.terminals = terminals;
        Focusable = true;
        SizeChanged += (_, _) => { if (Watching && OperatingSystem.IsAndroid()) FitWatchedFont(); };
        try
        {
            terminal = new GhosttySkiaView
            {
                Typeface = TerminalTheme.Typeface.Value,
                Colors = TerminalTheme.Colors(),
                ClipboardImageDirectory = launch.ClipboardDirectory,
                SaveClipboardImage = OperatingSystem.IsBrowser() || OperatingSystem.IsAndroid() ? bytes => terminals.SaveClipboardImageAsync(launch.SessionId, bytes, lifetime.Token).AsTask() : null,
                Name = "TerminalSkiaView",
                ResolveFileLinks = (text, token) => TerminalFileLinks.Resolve(terminals, launch, text, token),
                ResolveHighlightedLinks = (text, token) => TerminalFileLinks.Resolve(terminals, launch, text, token),
                OpenFileLink = launch.OpenFileLink,
                ExtendContextMenu = launch.ExtendContextMenu
            };
        }
        catch (DllNotFoundException)
        {
            throw new TerminalStartException("The Skia terminal renderer is not available on this platform.");
        }
        baseFontSize = terminal.FontSize;
        terminal.Input += (_, data) => { if (!Watching) input.Writer.TryWrite(data); };
        terminal.UserInput += (_, _) => ResumeHint.Dismiss();
        terminal.GridResized += (_, size) => { if (session is { Watching: false } attached) _ = Resize(attached, size); };
        terminal.LayoutUpdated += Arranged;
        if (OperatingSystem.IsAndroid())
        {
            var panel = new DockPanel();
            var keys = new TerminalKeyBar(terminal);
            KeyBar = keys;
            DockPanel.SetDock(keys, Dock.Bottom);
            panel.Children.Add(keys);
            panel.Children.Add(terminal);
            Child = panel;
        }
        else Child = terminal;
        Ui.ThemeChanged += UpdateColors;
        Started = StartAsync();
    }

    public Control View => this;
    public TerminalResumeHint ResumeHint { get; }
    public Task Started { get; }
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;
    public bool Yielded { get; private set; }
    public bool Watching { get; private set; }
    internal GhosttySkiaView Terminal => terminal;
    internal TerminalKeyBar? KeyBar { get; }

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
            { TabKey = launch.TabKey, Yield = launch.Yield, Watch = launch.Yield, Program = launch.Program, DirectoryFrom = launch.DirectoryFrom }, lifetime.Token);
        }
        catch (Exception error) when (RemoteErrors.IsTransport(error)) { throw new TerminalStartException(error.Message); }
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
        if (Child is DockPanel panel) panel.Children.Remove(terminal);
        Child = null;
        Child = new ScrollViewer
        {
            Name = "TerminalWatched",
            Content = terminal,
            Background = Ui.Terminal,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        // Replay contains absolute cursor positions from the holder. Apply its grid before parsing any
        // bytes, rather than waiting for the next layout pass on this client's smaller viewport.
        terminal.UpdateLayout();
        if (OperatingSystem.IsAndroid()) FitWatchedFont();
    }

    internal void FitWatchedFont()
    {
        if (!Watching || Bounds.Width <= 0 || terminal.Bounds.Width <= terminal.Padding.Left + terminal.Padding.Right) return;
        var padding = terminal.Padding.Left + terminal.Padding.Right;
        var size = Math.Clamp(terminal.FontSize * Math.Max(0, Bounds.Width - padding) / (terminal.Bounds.Width - padding), 10, 13);
        if (Math.Abs(size - terminal.FontSize) < .05) return;
        terminal.FontSize = size;
        terminal.UpdateLayout();
    }

    private async Task Pump(ITerminalSession attached)
    {
        try
        {
            // A revived agent's command is typed once the shell has printed something, so it never lands before the prompt.
            var prefill = attached.Prefill;
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            // Read ahead independently of UI dispatches. Bounded pieces keep at most 1 MiB queued,
            // with no dropped bytes; a slow renderer backpressures the host through the reader.
            var output = Channel.CreateBounded<(ReadOnlyMemory<byte> Data, (int Columns, int Rows) Grid)>(
                new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true });
            var grid = attached.Grid;
            var reader = ReadOutput();
            try
            {
                while (await output.Reader.WaitToReadAsync(lifetime.Token))
                {
                    var received = false;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        var started = Stopwatch.GetTimestamp();
                        var bytes = 0;
                        while (output.Reader.TryRead(out var item))
                        {
                            // Resizes stay ordered with the bytes they describe, including empty updates.
                            if (Watching && item.Grid != grid)
                            {
                                grid = item.Grid;
                                terminal.FixedGrid = new(grid.Columns, grid.Rows);
                                terminal.UpdateLayout();
                                if (OperatingSystem.IsAndroid()) FitWatchedFont();
                            }
                            terminal.Write(item.Data.Span);
                            received |= !item.Data.IsEmpty;
                            bytes += item.Data.Length;
                            if (bytes >= 256 * 1024 || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) break;
                        }
                        if (received) Output?.Invoke();
                    }, DispatcherPriority.Background);
                    if (prefill is not null && received && !Watching)
                    {
                        await attached.WriteAsync(System.Text.Encoding.UTF8.GetBytes(prefill.Text + (prefill.Submit ? "\r" : "")), lifetime.Token);
                        ResumeHint.Delivered(prefill);
                        prefill = null;
                    }
                }
            }
            finally { reading.Cancel(); await reader; }

            async Task ReadOutput()
            {
                try
                {
                    await foreach (var pooled in attached.ReadAsync(reading.Token))
                    {
                        // The queue outlives the chunk, which is valid only until the next read.
                        ReadOnlyMemory<byte> chunk = pooled.ToArray();
                        var size = attached.Grid;
                        if (chunk.IsEmpty) await output.Writer.WriteAsync((chunk, size), reading.Token);
                        for (var offset = 0; offset < chunk.Length; offset += 8 * 1024)
                            await output.Writer.WriteAsync((chunk.Slice(offset, Math.Min(8 * 1024, chunk.Length - offset)), size), reading.Token);
                    }
                    output.Writer.TryComplete();
                }
                catch (Exception error) { output.Writer.TryComplete(error); }
            }
            if (attached.Detached.IsCompleted) { detached.TrySetResult(); return; }
            exited.TrySetResult(await attached.Exit);
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            var message = RemoteErrors.IsTransport(error) ? "The terminal connection was lost: " + error.Message : error.Message;
            exited.TrySetException(new TerminalStartException(message));
        }
    }

    // RoyalTerminal's bounded output slices inspired this drain. See Ghostty.Avalonia/README.md.
    // Replay yields between slices; live output is coalesced by the bounded read-ahead queue above.
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

    public ValueTask<TerminalShellStatus?> ShellStatusAsync() =>
        session is null ? ValueTask.FromResult<TerminalShellStatus?>(null) : terminals.ShellStatusAsync(launch.SessionId);

    public ValueTask<bool> IsBusyAsync() =>
        exited.Task.IsCompleted || detached.Task.IsCompleted || session is null ? ValueTask.FromResult(false) : terminals.IsBusyAsync(launch.SessionId);
    public ValueTask<bool> IsPasswordInputAsync() =>
        exited.Task.IsCompleted || detached.Task.IsCompleted || session is null || Watching ? ValueTask.FromResult(false) : terminals.IsPasswordInputAsync(launch.SessionId);

    public ValueTask CloseAsync() => terminals.CloseAsync(launch.SessionId);

    public void FocusTerminal() => terminal.FocusTerminal();

    public void Write(string data) { if (!Watching) { if (data.Length > 0) ResumeHint.Dismiss(); input.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(data)); } }

    public string ReadScreen() => terminal.ReadScreen();

    public string ReadScreen(bool omitFaint) => omitFaint ? terminal.ReadViewport(omitFaint: true) : terminal.ReadScreen();

    public void SetAgentNewline(bool enabled) => terminal.AgentNewline = enabled;

    // A watched grid keeps the holder's size: the cells grow or shrink and the scroller takes the rest. On Android the
    // watched font is fitted to the pane instead, so an offset there waits until this view holds the terminal.
    public void SetFontSizeOffset(double points)
    {
        if (fontSizeOffset == points) return;
        fontSizeOffset = points;
        if (Watching && OperatingSystem.IsAndroid()) return;
        terminal.FontSize = Math.Max(1, baseFontSize + points);
    }

    public void SetMenuExtras(Func<IEnumerable<Control>>? items) => terminal.MenuExtras = items;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ResumeHint.Dismiss();
        Ui.ThemeChanged -= UpdateColors;
        lifetime.Cancel();
        input.Writer.TryComplete();
        if (session is not null) _ = session.DisposeAsync().AsTask();
        terminal.Dispose();
    }
}