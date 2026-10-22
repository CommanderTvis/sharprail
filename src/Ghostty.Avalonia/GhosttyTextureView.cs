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
    private readonly object frameLock = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint source;
    private TopLevel? topLevel;
    private int notificationPending;
    private int unavailableFrames;
    private bool active;
    private bool disposed;

    public GhosttyTextureView(GhosttyLaunch launch)
    {
        Focusable = true;
        ClipToBounds = true;
        terminal = new GhosttyView(launch);
        source = Native.Enable(terminal.Handle);
        terminal.Exited += (_, code) => Exited?.Invoke(this, code);
        terminal.OpenUrl += (_, url) => _ = OpenUrlAsync(url);
        frameReady = NotifyFrame;
        highlightTimer.Tick += (_, _) =>
        {
            if (!highlightDirty) return;
            highlightDirty = false;
            RefreshHighlights();
        };
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = InputMethod);
    }

    /// <summary>Completes after Skia's first GPU read of a completed Ghostty frame.</summary>
    public Task Ready => ready.Task;
    public event EventHandler<int>? Exited;
    public event EventHandler<Exception>? OperationFailed;
    public int Frames { get; private set; }
    internal int UnavailableFrames => Volatile.Read(ref unavailableFrames);
    internal int RetainedTextureCount
    {
        get { lock (frameLock) return source == 0 ? 0 : Native.RetainedCount(source); }
    }
    public PixelSize TextureSize { get; private set; }
    public bool IsBusy => terminal.IsBusy;
    public TerminalSize Size => terminal.Size;
    public void WriteOutput(ReadOnlySpan<byte> data) => terminal.WriteOutput(data);
    public TerminalColors Colors { get => terminal.Colors; set => terminal.Colors = value; }
    public string ReadScreen() => terminal.ReadScreen();
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
        Resize();
        lock (frameLock) active = true;
        highlightDirty = true;
        if (highlights?.Resolve is not null) highlightTimer.Start();
        Native.Notify(source, frameReady);
        Native.Visible(terminal.Handle, true);
        InvalidateVisual();
    }

    private void NotifyFrame()
    {
        if (Interlocked.Exchange(ref notificationPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref notificationPending, 0);
            if (active && !disposed) { highlightDirty = true; RefreshFileLink(); InvalidateVisual(); }
        });
    }

    private void DrawFrame(ImmediateDrawingContext context, Rect bounds)
    {
        nint texture, lease;
        int width, height;
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
            Dispatcher.UIThread.Post(() =>
            {
                if (disposed) return;
                TextureSize = new(width, height);
                Frames++;
                ready.TrySetResult();
                inputMethod?.NotifyCursor();
            });
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
        Native.Resize(terminal.Handle, Bounds.Width, Bounds.Height, PixelScale.Of(this));
    }

    private void ScalingChanged(object? sender, EventArgs e) => Resize();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) Resize();
    }

    public override void Render(DrawingContext context)
    {
        // Link underlines are part of Ghostty's frame (see PushUnderlines), never an overlay that could lag the text.
        context.Custom(new TextureOperation(this, new Rect(Bounds.Size)));
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
        topLevel = null;
        terminal.Dispose();
        ready.TrySetCanceled();
        GC.KeepAlive(frameReady);
    }
}