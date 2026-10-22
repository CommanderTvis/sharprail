using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;
using SharpRail.UI.Terminal;

namespace SharpRail.UI.Browser;

// GPU canvas surface; shared-texture composition is not implemented yet.
internal sealed class ResttyTerminal : NativeControlHost, ITerminalBackend, ITerminalResumeHint
{
    private static readonly Dictionary<int, ResttyTerminal> instances = [];
    private static int nextId;
    private readonly int id = ++nextId;
    private readonly TerminalLaunch launch;
    private readonly ITerminalService terminals;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(string? Text, int Columns, int Rows, bool Offer)> input = Channel.CreateUnbounded<(string?, int, int, bool)>(new() { SingleReader = true });
    private ITerminalSession? session;
    private JSObject? element;
    private int columns = 80, rows = 24;
    private bool disposed;
    private bool agentNewline;
    // Restty's own default, matching the bridge's configuration.
    private const double BaseFontSize = 14;
    private double fontSizeOffset;

    internal ResttyTerminal(TerminalLaunch launch, ITerminalService terminals)
    {
        this.launch = launch;
        this.terminals = terminals;
        instances.Add(id, this);
        Name = "TerminalResttyView";
        Ui.ThemeChanged += UpdateColors;
        ResumeHint.Changed += UpdateResumeHint;
        Started = Start();
    }

    public Control View => this;
    public TerminalResumeHint ResumeHint { get; } = new();
    public bool NativeResumeHint => true;
    public Task Started { get; }
    internal Task RendererReady => ready.Task;
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;
    public bool Yielded => launch.Yield && session?.Detached.IsCompleted == true;
    public bool Watching => session?.Watching == true;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (element is null)
        {
            element = ResttyInterop.Create();
            _ = Initialize();
        }
        return new JSObjectControlHandle(element);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control) { }

    private async Task Initialize()
    {
        try
        {
            var grid = (await ResttyInterop.Initialize(element!, id, ThemeJson())).Split(',');
            Resize(int.Parse(grid[0]), int.Parse(grid[1]));
            if (disposed) return;
            ResttyInterop.AgentNewline(element!, agentNewline);
            if (fontSizeOffset != 0) ResttyInterop.FontSize(element!, BaseFontSize + fontSizeOffset);
            ready.TrySetResult();
        }
        catch (Exception error) { ready.TrySetException(new TerminalStartException("Restty could not start: " + error.Message)); }
    }

    private void Resize(int width, int height)
    {
        columns = width;
        rows = height;
        if (session is { Watching: false }) input.Writer.TryWrite((null, width, height, false));
    }

    internal static void Event(string json)
    {
        using var message = JsonDocument.Parse(json);
        var value = message.RootElement;
        if (!instances.TryGetValue(value.GetProperty("id").GetInt32(), out var terminal)) return;
        if (value.TryGetProperty("focus", out _)) terminal.FindAncestorOfType<TerminalView>()?.Focus();
        else if (value.TryGetProperty("activity", out _)) { if (!terminal.Watching) terminal.ResumeHint.Dismiss(); }
        else if (value.TryGetProperty("zoom", out var zoom)) terminal.FindAncestorOfType<TerminalView>()?.ChangeFontSize(zoom.GetDouble());
        else if (value.TryGetProperty("text", out var text)) terminal.QueueInput(text.GetString()!);
        else terminal.Resize(value.GetProperty("columns").GetInt32(), value.GetProperty("rows").GetInt32());
    }

    private async Task Start()
    {
        await ready.Task.WaitAsync(lifetime.Token);
        var attached = await terminals.AttachAsync(new(launch.SessionId, launch.WorkspaceRoot, launch.ClientId, columns, rows)
        { TabKey = launch.TabKey, Yield = launch.Yield, Watch = launch.Watch, Program = launch.Program, DirectoryFrom = launch.DirectoryFrom }, lifetime.Token);
        if (disposed) { await attached.DisposeAsync(); return; }
        session = attached;
        if (attached.Watching) ResttyInterop.Watch(element!, attached.Grid.Columns, attached.Grid.Rows);
        await Output(attached.Replay);
        _ = Send();
        _ = Pump();
    }

    private async Task Output(ReadOnlyMemory<byte> data)
    {
        for (var offset = 0; offset < data.Length; offset += 128 * 1024)
        {
            var bytes = data.Slice(offset, Math.Min(128 * 1024, data.Length - offset)).ToArray();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                lifetime.Token.ThrowIfCancellationRequested();
                ResttyInterop.Output(element!, bytes);
            }, DispatcherPriority.Background);
        }
    }

    private async Task Pump()
    {
        try
        {
            var prefill = session!.Prefill;
            var grid = session.Grid;
            await foreach (var data in session.ReadAsync(lifetime.Token))
            {
                if (Watching && grid != session.Grid)
                {
                    grid = session.Grid;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        ResttyInterop.Watch(element!, grid.Columns, grid.Rows);
                    });
                }
                await Output(data);
                if (prefill is not null && !data.IsEmpty)
                {
                    QueueInput(prefill.Text + (prefill.Submit ? "\r" : ""), TerminalResumeHint.IsOffer(prefill));
                    prefill = null;
                }
            }
            if (session.Detached.IsCompleted) detached.TrySetResult();
            else exited.TrySetResult(await session.Exit);
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(new TerminalStartException("Terminal output failed: " + error.Message)); }
    }

    private async Task Send()
    {
        try
        {
            await foreach (var item in input.Reader.ReadAllAsync(lifetime.Token))
                if (item.Text is { } text)
                {
                    await session!.WriteAsync(Encoding.UTF8.GetBytes(text), lifetime.Token);
                    if (item.Offer) ResumeHint.Offer();
                }
                else await session!.ResizeAsync(item.Columns, item.Rows, lifetime.Token);
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(new TerminalStartException("Terminal input failed: " + error.Message)); }
    }

    private static string ThemeJson()
    {
        var colors = TerminalTheme.Colors();
        static object? Color(Avalonia.Media.Color? color) => color is { } c ? new { r = c.R, g = c.G, b = c.B } : null;
        return JsonSerializer.Serialize(new
        {
            raw = new { },
            colors = new
            {
                background = Color(colors.Background),
                foreground = Color(colors.Foreground),
                cursor = Color(colors.Cursor),
                selectionBackground = Color(colors.SelectionBackground),
                selectionForeground = Color(colors.SelectionForeground),
                palette = colors.Ansi.Select(c => Color(c)).ToArray()
            }
        });
    }
    private void UpdateResumeHint() => Dispatcher.UIThread.Post(() =>
    {
        if (disposed || element is null) return;
        static string Css(Avalonia.Media.Color color) => FormattableString.Invariant($"rgba({color.R},{color.G},{color.B},{color.A / 255d})");
        ResttyInterop.ResumeHint(element, ResumeHint.Visible ? "Press Enter to resume this tab’s previous session." : "", Css(Ui.Elevated.Color), Css(Ui.Muted.Color));
    });
    private void UpdateColors()
    {
        if (element is not null) ResttyInterop.Theme(element, ThemeJson());
        UpdateResumeHint();
    }
    public ValueTask<bool> IsBusyAsync() => terminals.IsBusyAsync(launch.SessionId);
    public ValueTask CloseAsync() => terminals.CloseAsync(launch.SessionId);
    public void FocusTerminal() { if (element is not null && !Watching) ResttyInterop.Focus(element); }
    private void QueueInput(string data, bool offer = false) { if (!disposed && !Watching) input.Writer.TryWrite((data, 0, 0, offer)); }
    public void Write(string data) { if (data.Length > 0) ResumeHint.Dismiss(); QueueInput(data); }
    public string ReadScreen() => element is null ? "" : ResttyInterop.Screen(element);
    public void SetAgentNewline(bool enabled)
    {
        agentNewline = enabled;
        if (element is not null) ResttyInterop.AgentNewline(element, enabled);
    }
    public void SetFontSizeOffset(double points)
    {
        fontSizeOffset = points;
        if (element is not null && ready.Task.IsCompletedSuccessfully) ResttyInterop.FontSize(element, Math.Max(1, BaseFontSize + points));
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ResumeHint.Dismiss();
        ResumeHint.Changed -= UpdateResumeHint;
        instances.Remove(id);
        Ui.ThemeChanged -= UpdateColors;
        lifetime.Cancel();
        input.Writer.TryComplete();
        if (session is not null) _ = session.DisposeAsync();
        if (element is not null) { ResttyInterop.Destroy(element); element.Dispose(); element = null; }
    }
}

internal static partial class ResttyInterop
{
    [JSImport("resumeHint", "restty-bridge")]
    internal static partial void ResumeHint(JSObject element, string text, string background, string foreground);
    [JSImport("watch", "restty-bridge")]
    internal static partial void Watch(JSObject element, int columns, int rows);
    [JSImport("create", "restty-bridge")]
    internal static partial JSObject Create();
    [JSImport("initialize", "restty-bridge")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> Initialize(JSObject element, int id, string theme);
    [JSImport("output", "restty-bridge")]
    internal static partial void Output(JSObject element, byte[] bytes);
    [JSImport("screen", "restty-bridge")]
    internal static partial string Screen(JSObject element);
    [JSImport("focus", "restty-bridge")]
    internal static partial void Focus(JSObject element);
    [JSImport("theme", "restty-bridge")]
    internal static partial void Theme(JSObject element, string theme);
    [JSImport("agentNewline", "restty-bridge")]
    internal static partial void AgentNewline(JSObject element, bool enabled);
    [JSImport("fontSize", "restty-bridge")]
    internal static partial void FontSize(JSObject element, double value);
    [JSImport("destroy", "restty-bridge")]
    internal static partial void Destroy(JSObject element);
}