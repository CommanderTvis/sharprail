using System.Runtime.Versioning;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;

using SkiaSharp;

namespace Ghostty.Avalonia;

/// <summary>
/// Ghostty's Metal renderer sampled directly by Avalonia's Skia compositor. Its
/// platform view stays unparented; clipping, overlays and input belong to Avalonia.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class GhosttyTextureView : Control, IDisposable
{
    private readonly GhosttyView terminal;
    private readonly Native.Callback frameReady;
    private readonly Action frameNotified;
    private readonly Action firstFrameDrawn;
    private readonly object frameLock = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint source;
    private TopLevel? topLevel;
    // Set while a frame notification is posted and its composition has not yet sampled the texture: further
    // Ghostty frames in that window are covered by the draw that follows, so one post serves a display frame.
    private int notificationPending;
    private int unavailableFrames;
    private int uiNotifications;
    private int frames;
    private int textureWidth, textureHeight;
    // Whether frame notifications may post: cleared while this view is hidden (nothing would draw); the next
    // composition of the view, or showing it, sets it again.
    private volatile bool shown = true;
    private bool active;
    private bool disposed;

    /// <summary>Scrolls the previous (negative) or next prompt to the top of the view through Ghostty's jump_to_prompt.</summary>
    public void JumpToPrompt(int delta)
    {
        if (!disposed) Native.Action(terminal.Handle, "jump_to_prompt:" + delta.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public GhosttyTextureView(GhosttyLaunch launch)
    {
        Focusable = true;
        ClipToBounds = true;
        settle = new(Resize);
        terminal = new GhosttyView(launch);
        source = Native.Enable(terminal.Handle);
        terminal.Exited += (_, code) => Exited?.Invoke(this, code);
        terminal.OpenUrl += (_, url) => _ = OpenUrlAsync(url);
        frameReady = NotifyFrame;
        frameNotified = FrameNotified;
        firstFrameDrawn = FirstFrameDrawn;
        highlightTimer.Tick += (_, _) =>
        {
            // A hidden view keeps its highlights dirty until it is shown.
            if (!highlightDirty || !IsEffectivelyVisible) return;
            highlightDirty = false;
            RefreshHighlights();
        };
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = InputMethod);
    }

    /// <summary>
    /// Points added to Ghostty's configured font size for this surface alone, applied with its
    /// <c>increase_font_size</c>, <c>decrease_font_size</c> and <c>reset_font_size</c> actions; Ghostty resizes the grid.
    /// </summary>
    public double FontSizeOffset
    {
        get;
        set
        {
            if (disposed || value == field) return;
            var change = value - field;
            field = value;
            Native.Action(terminal.Handle, value == 0 ? "reset_font_size"
                : FormattableString.Invariant($"{(change > 0 ? "increase" : "decrease")}_font_size:{Math.Abs(change):0.###}"));
        }
    }


    /// <summary>Completes after Skia's first GPU read of a completed Ghostty frame.</summary>
    public Task Ready => ready.Task;
    public event EventHandler<int>? Exited;
    public event EventHandler<Exception>? OperationFailed;
    /// <summary>GPU draws of a Ghostty frame so far.</summary>
    public int Frames => Volatile.Read(ref frames);
    internal int UnavailableFrames => Volatile.Read(ref unavailableFrames);
    /// <summary>Dispatcher posts made for frame notifications and frame bookkeeping.</summary>
    internal int UiNotifications => Volatile.Read(ref uiNotifications);
    internal int RetainedTextureCount
    {
        get { lock (frameLock) return source == 0 ? 0 : Native.RetainedCount(source); }
    }
    public PixelSize TextureSize => new(Volatile.Read(ref textureWidth), Volatile.Read(ref textureHeight));
    public bool IsBusy => terminal.IsBusy;
    public TerminalSize Size => terminal.Size;
    public void WriteOutput(ReadOnlySpan<byte> data) => terminal.WriteOutput(data);
    public TerminalColors Colors { get => terminal.Colors; set => terminal.Colors = value; }
    public string ReadScreen() => terminal.ReadScreen();
    public string ReadViewport() => terminal.ReadViewport();
    /// <summary>User input, excluding automatic terminal-query replies.</summary>
    public event EventHandler? UserInput;
    public void Type(string text) { if (text.Length > 0) UserInput?.Invoke(this, EventArgs.Empty); terminal.Type(text); }
    /// <inheritdoc cref="GhosttyView.AgentNewline"/>
    public bool AgentNewline { get; set; }
    public void FocusTerminal() => Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (disposed) return;
        topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null) topLevel.ScalingChanged += ScalingChanged;
        if (topLevel is Window window) window.PropertyChanged += WindowChanged;
        settle.ApplyNext();
        Resize();
        lock (frameLock) active = true;
        shown = true;
        Volatile.Write(ref notificationPending, 0);
        highlightDirty = true;
        if (highlights?.Resolve is not null) highlightTimer.Start();
        Native.Notify(source, frameReady);
        Native.Visible(terminal.Handle, true);
        InvalidateVisual();
    }

    // Ghostty's renderer thread, once per completed frame and under its frame lock: no allocation, and at most one
    // pending post. A hidden view does not post at all; it samples the latest frame when it is next composed.
    private void NotifyFrame()
    {
        if (!shown || Interlocked.Exchange(ref notificationPending, 1) != 0) return;
        Interlocked.Increment(ref uiNotifications);
        Dispatcher.UIThread.Post(frameNotified);
    }

    private void FrameNotified()
    {
        if (!active || disposed || !IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            // Nothing will compose this view, so the next frame must be able to post once it is shown again.
            if (active && !disposed && !IsEffectivelyVisible) shown = false;
            Volatile.Write(ref notificationPending, 0);
            return;
        }
        // The flag stays set until the composition below samples the texture (DrawFrame clears it first).
        highlightDirty = true;
        RefreshFileLink();
        RefreshFailedCommands();
        inputMethod?.NotifyCursor();
        InvalidateVisual();
    }

    private void FirstFrameDrawn()
    {
        if (disposed) return;
        ready.TrySetResult();
        inputMethod?.NotifyCursor();
    }

    private void DrawFrame(ImmediateDrawingContext context, Rect bounds)
    {
        nint texture, lease;
        int width, height;
        // Being composed means being shown. Clear the pending notification before sampling, so a frame completing
        // after this acquire posts again and the last frame of a burst is never left undrawn.
        shown = true;
        Volatile.Write(ref notificationPending, 0);
        lock (frameLock)
        {
            if (!active || disposed) return;
            texture = Native.Acquire(source, out width, out height, out lease);
        }
        if (texture == 0) { Interlocked.Increment(ref unavailableFrames); return; }
        try
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                throw new NotSupportedException("Ghostty textures require Avalonia's Metal Skia backend.");
            using var drawing = feature.Lease();
            if (drawing.GrContext is not { Backend: GRBackend.Metal } gpu)
                throw new NotSupportedException("Ghostty textures require Avalonia's Metal Skia backend.");
            using var backend = new GRBackendTexture(width, height, false, new GRMtlTextureInfo(texture));
            using var image = SKImage.FromTexture(gpu, backend, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888, SKAlphaType.Premul)
                ?? throw new InvalidOperationException("Skia could not wrap Ghostty's Metal texture.");
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(drawing.CurrentOpacity * 255, 0, 255)) };
            try
            {
                drawing.SkCanvas.DrawImage(image, new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height), new SKSamplingOptions(SKFilterMode.Linear), paint);
            }
            finally
            {
                // The renderer can reuse the source as soon as this lease is returned.
                gpu.Flush(submit: true, synchronous: true);
            }
            // Frame bookkeeping stays on the render thread; only the first draw posts, to complete Ready.
            Volatile.Write(ref textureWidth, width);
            Volatile.Write(ref textureHeight, height);
            if (Interlocked.Increment(ref frames) == 1)
            {
                Interlocked.Increment(ref uiNotifications);
                Dispatcher.UIThread.Post(firstFrameDrawn);
            }
        }
        catch (Exception error)
        {
            Dispatcher.UIThread.Post(() => Fail(error));
        }
        finally { Native.Return(lease); }
    }

    private void Fail(Exception error)
    {
        if (disposed || !active) return;
        highlightTimer.Stop();
        highlights?.Clear();
        ClearHighlights();
        lock (frameLock) active = false;
        Native.Notify(source, null);
        ready.TrySetException(error);
        OperationFailed?.Invoke(this, error);
    }

    private void Resize()
    {
        if (disposed || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        // A drag resizes the control many times: the native grid, and so the program's terminal, waits for it to settle.
        var target = (Bounds.Width, Bounds.Height, Scale: PixelScale.Of(this));
        // Re-attaching at the size already applied (a tab switch) changes nothing: no hold, no native resize or redraw.
        if (appliedScale == target.Scale && applied == new Size(target.Width, target.Height)) { settle.Cancel(); settle.Applied(); return; }
        if (settle.Hold(target)) return;
        settle.Applied();
        applied = new Size(target.Width, target.Height);
        appliedScale = target.Scale;
        Native.Resize(terminal.Handle, target.Width, target.Height, target.Scale);
    }

    private readonly ResizeSettle<(double Width, double Height, double Scale)> settle;
    private Size applied;
    private double appliedScale;

    /// <summary>How long the control size must hold still before the grid follows it; null restores the default.</summary>
    internal TimeSpan? ResizeQuiet { set => settle.Quiet = value ?? ResizeSettle<(double, double, double)>.DefaultQuiet; }

    // Maximize and full screen are single jumps, not a gesture.
    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) settle.ApplyNext();
    }

    private void ScalingChanged(object? sender, EventArgs e) { settle.ApplyNext(); Resize(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) Resize();
        else if (change.Property == IsVisibleProperty && change.GetNewValue<bool>()) { shown = true; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        // Link underlines are part of Ghostty's frame (see PushUnderlines), never an overlay that could lag the text.
        context.Custom(new TextureOperation(this, new Rect(applied.Width > 0 ? applied : Bounds.Size)));
        // Failed-command badges sit in the left gutter, outside the text, so a frame of lag after a scroll moves no glyph.
        if (failedRows.Length == 0 || Size.Rows <= 0) return;
        var height = Bounds.Height / Size.Rows;
        var brush = new SolidColorBrush(Colors.Ansi.Count > 1 ? Colors.Ansi[1] : global::Avalonia.Media.Colors.IndianRed);
        foreach (var row in failedRows) context.FillRectangle(brush, new Rect(0, row * height + height * 0.15, FailedBadgeWidth, height * 0.7), 1);
    }

    internal const double FailedBadgeWidth = 3;
    private int[] failedRows = [];
    private readonly ushort[] failedBuffer = new ushort[1024];

    /// <summary>Viewport rows whose command exited non-zero (OSC 133 D), badged in the gutter.</summary>
    public IReadOnlyList<int> FailedCommandRows => failedRows;

    private void RefreshFailedCommands()
    {
        var count = (int)Native.FailedRows(terminal.Handle, failedBuffer, (nuint)failedBuffer.Length);
        if (failedBuffer.AsSpan(0, count).ToArray().Select(row => (int)row).SequenceEqual(failedRows)) return;
        failedRows = [.. failedBuffer.AsSpan(0, count).ToArray().Select(row => (int)row)];
    }

    private sealed class TextureOperation(GhosttyTextureView owner, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context) => owner.DrawFrame(context, bounds);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        highlightTimer.Stop();
        highlights?.Clear();
        ClearHighlights();
        lock (frameLock) active = false;
        if (!disposed) Native.Notify(source, null);
        if (topLevel is not null) topLevel.ScalingChanged -= ScalingChanged;
        if (topLevel is Window window) window.PropertyChanged -= WindowChanged;
        settle.Cancel();
        topLevel = null;
        if (!disposed) Native.Visible(terminal.Handle, false);
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        lock (frameLock)
        {
            if (disposed) return;
            disposed = true;
            fileHover?.Dispose();
            highlights?.Dispose();
            highlightTimer.Stop();
            active = false;
            Native.Stop(source);
            Native.Release(source);
            source = 0;
        }
        if (topLevel is not null) topLevel.ScalingChanged -= ScalingChanged;
        if (topLevel is Window window) window.PropertyChanged -= WindowChanged;
        settle.Cancel();
        topLevel = null;
        settle.Dispose();
        terminal.Dispose();
        ready.TrySetCanceled();
        GC.KeepAlive(frameReady);
    }
}