using System.Runtime.Versioning;
using System.Threading.Channels;

using Avalonia.Controls;
using Avalonia.Input;

using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Terminal;

[SupportedOSPlatform("macos")]
internal sealed class DirectGhosttyTerminal : Border, ITerminalBackend
{
    private readonly TerminalLaunch launch;
    private readonly ITerminalService terminals;
    private readonly GhosttyExternalIo io = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<(ReadOnlyMemory<byte> Data, TerminalSize? Size)> input =
        Channel.CreateUnbounded<(ReadOnlyMemory<byte>, TerminalSize?)>(new() { SingleReader = true });
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GhosttyTextureView? texture;
    private ITerminalSession? session;
    private Task<ITerminalSession>? attaching;
    private SkiaTerminal? fallback;
    private Task? fallbackStart;
    private bool disposed;
    private bool agentNewline;

    internal DirectGhosttyTerminal(TerminalLaunch launch, ITerminalService terminals)
    {
        this.launch = launch;
        this.terminals = terminals;
        Focusable = true;
        io.Input += (_, data) => input.Writer.TryWrite((data, null));
        io.GridResized += (_, size) => input.Writer.TryWrite((default, size));
        Started = StartAsync();
    }

    public Control View => this;
    public Task Started { get; }
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;
    public bool Yielded { get; private set; }

    public void Write(string data)
    {
        if (fallback is not null) fallback.Write(data);
        else input.Writer.TryWrite((System.Text.Encoding.UTF8.GetBytes(data), null));
    }

    public void SetAgentNewline(bool enabled)
    {
        agentNewline = enabled;
        if (texture is not null) texture.AgentNewline = enabled;
        fallback?.SetAgentNewline(enabled);
    }

    public string ReadScreen() => fallback?.ReadScreen() ?? texture?.ReadScreen() ?? "";

    private async Task StartAsync()
    {
        try { await StartTexture(); }
        catch (OperationCanceledException) when (!disposed && fallbackStart is not null) { await fallbackStart; }
        catch (Exception error) when (!disposed && attaching is null && error is InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            await UseSkia();
        }
    }

    private async Task StartTexture()
    {
        texture = new GhosttyTextureView(new(launch.WorkspaceRoot,
            ClipboardImageDirectory: launch.ClipboardDirectory, ExternalIo: io))
        { Colors = TerminalTheme.Colors(), AgentNewline = agentNewline };
        texture.OperationFailed += TextureFailed;
        Child = texture;
        Ui.ThemeChanged += UpdateColors;
        await texture.Ready.WaitAsync(lifetime.Token);
        if (fallbackStart is not null) { await fallbackStart; return; }
        var size = texture.Size;
        var request = new TerminalAttachRequest(launch.SessionId, launch.WorkspaceRoot, launch.ClientId, size.Columns, size.Rows) { TabKey = launch.TabKey, Yield = launch.Yield };
        attaching = Task.Run(async () => await terminals.AttachAsync(request, lifetime.Token));
        var attached = await attaching;
        if (disposed || fallbackStart is not null)
        {
            await attached.DisposeAsync();
            if (disposed) throw new ObjectDisposedException(nameof(DirectGhosttyTerminal));
            await fallbackStart!;
            return;
        }
        session = attached;
        Yielded = launch.Yield && attached.Detached.IsCompleted;
        var view = texture;
        _ = Task.Run(() => Pump(attached, view));
        _ = Task.Run(() => Send(attached));
        _ = ObserveIoFailure();
    }

    private async Task ObserveIoFailure()
    {
        try { await io.Failure.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(error); }
    }

    private async Task Pump(ITerminalSession attached, GhosttyTextureView view)
    {
        try
        {
            view.WriteOutput(attached.Replay.Span);
            // A revived agent's command is typed once the shell has printed something, so it never lands before the prompt.
            var prefill = attached.Prefill;
            await foreach (var data in attached.ReadAsync(lifetime.Token))
            {
                view.WriteOutput(data.Span);
                if (prefill is not null && data.Length > 0)
                {
                    input.Writer.TryWrite((System.Text.Encoding.UTF8.GetBytes(prefill.Text + (prefill.Submit ? "\r" : "")), null));
                    prefill = null;
                }
            }
            lifetime.Token.ThrowIfCancellationRequested();
            if (attached.Detached.IsCompleted) detached.TrySetResult();
            else exited.TrySetResult(await attached.Exit);
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(error); }
    }

    private async Task Send(ITerminalSession attached)
    {
        try
        {
            await foreach (var item in input.Reader.ReadAllAsync(lifetime.Token))
            {
                if (item.Size is { } size) await attached.ResizeAsync(size.Columns, size.Rows, lifetime.Token);
                else await attached.WriteAsync(item.Data, lifetime.Token);
            }
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(error); }
    }

    private async void TextureFailed(object? sender, Exception error)
    {
        try { await UseSkia(); }
        catch (Exception failure) { if (!disposed) exited.TrySetException(failure); }
    }

    private Task UseSkia()
    {
        if (fallbackStart is not null) return fallbackStart;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Cancellation can resume startup inline; publish the fallback before cancelling the texture.
        fallbackStart = completion.Task;
        _ = CompleteSkia(completion);
        return fallbackStart;
    }

    private async Task CompleteSkia(TaskCompletionSource completion)
    {
        try { await StartSkia(); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task StartSkia()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var focused = IsKeyboardFocusWithin;
        lifetime.Cancel();
        Ui.ThemeChanged -= UpdateColors;
        if (texture is not null) texture.OperationFailed -= TextureFailed;
        texture?.Dispose();
        texture = null;
        if (attaching is not null)
        {
            try { await (await attaching).DisposeAsync(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        ObjectDisposedException.ThrowIf(disposed, this);
        fallback = new SkiaTerminal(launch, terminals);
        fallback.SetAgentNewline(agentNewline);
        Child = fallback.View;
        await fallback.Started;
        if (disposed) return;
        if (focused) fallback.FocusTerminal();
        _ = ObserveSkia();
    }

    private async Task ObserveSkia()
    {
        try
        {
            await Task.WhenAny(fallback!.Exited, fallback.Detached);
            if (disposed) return;
            if (fallback.Detached.IsCompleted) { Yielded = fallback.Yielded; detached.TrySetResult(); }
            else exited.TrySetResult(await fallback.Exited);
        }
        catch (Exception error) { if (!disposed) exited.TrySetException(error); }
    }

    private void UpdateColors() { if (texture is not null) texture.Colors = TerminalTheme.Colors(); }
    public ValueTask<bool> IsBusyAsync() => exited.Task.IsCompleted || detached.Task.IsCompleted
        ? ValueTask.FromResult(false) : terminals.IsBusyAsync(launch.SessionId);
    public ValueTask CloseAsync() => terminals.CloseAsync(launch.SessionId);
    public void FocusTerminal() { texture?.FocusTerminal(); fallback?.FocusTerminal(); }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        input.Writer.TryComplete();
        Ui.ThemeChanged -= UpdateColors;
        if (texture is not null) texture.OperationFailed -= TextureFailed;
        texture?.Dispose();
        fallback?.Dispose();
        if (session is not null) _ = session.DisposeAsync().AsTask();
    }
}