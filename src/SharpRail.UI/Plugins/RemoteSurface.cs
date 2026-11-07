using System.Text;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using SharpRail.Plugins.Isolation;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The client's control for one surface of an external plugin (Security.SPEC.md, UI halves): the plugin's control
/// renders in its process on the host, and this shows its pixels. Frames are decoded off the UI thread and drawn 1:1 at
/// device pixels; the surface's size, scale and the kit theme go to the plugin, and pointer, wheel, key, text and
/// focus come back in. Detached for longer than a moment, the surface closes; attached again, it opens a new one, for
/// which the plugin makes its control again.
/// </summary>
internal sealed class RemoteSurface : Decorator
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly Func<IPluginSurface> open;
    private readonly Lock gate = new();
    private readonly DispatcherTimer closing;
    private IPluginSurface surface = null!;
    private byte[] pixels = [];
    private int width, height;
    private long keyframe = -1;
    private WriteableBitmap? bitmap;
    private Size wanted, sent;
    private double sentScale;
    private bool closed;

    public RemoteSurface(Func<IPluginSurface> open)
    {
        this.open = open;
        Name = "PluginSurface";
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        closing = new DispatcherTimer(CloseGrace, DispatcherPriority.Background, (_, _) => Close());
        Open();
    }

    /// <summary>The surface shown now; a new one after the last closed while detached.</summary>
    internal IPluginSurface Surface => surface;
    /// <summary>Frames applied, moves applied and the tile bytes of the last frame that moved pixels, for the checks.</summary>
    internal int Frames { get; private set; }
    internal int Moves { get; private set; }
    internal int MovedFrameBytes { get; private set; }
    internal int MovedFrameTiles { get; private set; }
    /// <summary>Keyframes applied, for the checks.</summary>
    internal int Keyframes { get; private set; }

    /// <summary>The decoded pixels (premultiplied BGRA) and their size, for the checks.</summary>
    internal (int Width, int Height, byte[] Pixels) Snapshot()
    {
        lock (gate) return (width, height, [.. pixels]);
    }

    /// <summary>The kit theme a plugin's surfaces render under, as JSON with colours as <c>#aarrggbb</c>.</summary>
    internal static string ThemeJson()
    {
        var theme = SharpRail.Plugins.UI.Kit.Ui.Theme;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("id", theme.Id);
            json.WriteString("label", theme.Label);
            json.WriteNumber("order", theme.Order);
            json.WriteString("appearance", theme.Appearance);
            json.WriteString("contrast", theme.Contrast);
            json.WriteStartObject("colors");
            foreach (var (key, color) in theme.Colors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (color is { } value) json.WriteString(key, value.ToString());
                else json.WriteNull(key);
            json.WriteEndObject();
            json.WriteStartArray("ansi");
            foreach (var color in theme.Ansi) json.WriteStringValue(color.ToString());
            json.WriteEndArray();
            json.WriteStartObject("syntax");
            foreach (var (key, color) in theme.Syntax.OrderBy(pair => pair.Key, StringComparer.Ordinal)) json.WriteString(key, color.ToString());
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // A new surface from the plugin's factory, starting from no pixels; its own pump reads it.
    private void Open()
    {
        var opened = open();
        lock (gate)
        {
            surface = opened;
            (width, height, pixels) = (0, 0, []);
        }
        _ = Task.Run(() => PumpAsync(opened));
    }

    private async Task PumpAsync(IPluginSurface mine)
    {
        string? failure;
        try
        {
            var bytes = 0;
            var moves = 0;
            var tiles = 0;
            await foreach (var message in mine.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (message is SurfaceLeft left)
                {
                    Dispatcher.UIThread.Post(() => { if (ReferenceEquals(mine, surface)) Leave(left.Backward); });
                    continue;
                }
                var (header, data) = (SurfaceFrame)message;
                if (!Apply(mine, header, data)) break;
                bytes += data.Length;
                moves += header.Moves.Count;
                tiles += header.Tiles.Count;
                if (!header.Last) continue;
                var (frameBytes, frameMoves, frameTiles) = (bytes, moves, tiles);
                (bytes, moves, tiles) = (0, 0, 0);
                if (!await Dispatcher.UIThread.InvokeAsync(() => Present(mine, header, frameBytes, frameMoves, frameTiles))) break;
                mine.Acknowledge(header.Sequence);
            }
            failure = null;
        }
        catch (InvalidDataException error)
        {
            failure = "The plugin sent a view the app cannot read.";
            Console.Error.WriteLine("Plugin surface: " + error.Message);
            await mine.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            failure = error.Message;
        }
        Dispatcher.UIThread.Post(() =>
        {
            // A surface that closed while detached, or one a newer surface replaced, ends without a message.
            if (!ReferenceEquals(mine, surface) || closed) return;
            Fail(failure ?? "This view closed.");
        });
    }

    // Moves first, then tiles, on the pump's thread; the UI thread copies the result once the frame's last message is in.
    // False once a newer surface replaced this one.
    private bool Apply(IPluginSurface mine, SurfaceFrameHeader header, byte[] data)
    {
        lock (gate)
        {
            if (!ReferenceEquals(mine, surface)) return false;
            if (header.Width != width || header.Height != height)
            {
                (width, height) = (header.Width, header.Height);
                pixels = new byte[width * height * 4];
            }
            // A keyframe repaints from transparent, once, at its first message.
            if (header.Keyframe && header.Sequence != keyframe)
            {
                keyframe = header.Sequence;
                Array.Clear(pixels);
                Keyframes++;
            }
            var stride = width * 4;
            foreach (var move in header.Moves) SurfacePixels.Move(pixels, stride, move);
            var offset = 0;
            foreach (var tile in header.Tiles)
            {
                TileCodec.Decode(tile.Codec, data.AsSpan(offset, tile.Length), pixels, stride, tile.X, tile.Y, tile.Width, tile.Height);
                offset += tile.Length;
            }
        }
        return true;
    }

    private bool Present(IPluginSurface mine, SurfaceFrameHeader header, int bytes, int moves, int tiles)
    {
        if (closed || !ReferenceEquals(mine, surface)) return false;
        lock (gate)
        {
            if (width == 0 || height == 0)
            {
                bitmap?.Dispose();
                bitmap = null;
            }
            else
            {
                if (bitmap is null || bitmap.PixelSize != new PixelSize(width, height) || bitmap.Dpi.X != 96 * header.Scale)
                {
                    bitmap?.Dispose();
                    bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * header.Scale, 96 * header.Scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
                }
                using var target = bitmap.Lock();
                for (var row = 0; row < height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(pixels, row * width * 4, target.Address + row * target.RowBytes, width * 4);
            }
        }
        Frames++;
        Moves += moves;
        if (moves > 0) (MovedFrameBytes, MovedFrameTiles) = (bytes, tiles);
        var desired = new Size(header.DesiredWidth, header.DesiredHeight);
        if (desired != wanted)
        {
            wanted = desired;
            InvalidateMeasure();
        }
        InvalidateVisual();
        return true;
    }

    private void Fail(string text)
    {
        if (Child is not null) return;
        Child = new TextBlock { Name = "PluginSurfaceFailure", Text = text, Margin = new(12), TextWrapping = TextWrapping.Wrap, Foreground = SharpRail.Plugins.UI.Kit.Ui.Muted };
        bitmap?.Dispose();
        bitmap = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not null) return base.MeasureOverride(availableSize);
        return new Size(Math.Min(wanted.Width, availableSize.Width), Math.Min(wanted.Height, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (!closed && Child is null && (finalSize != sent || scale != sentScale))
        {
            (sent, sentScale) = (finalSize, scale);
            surface.Resize(finalSize.Width, finalSize.Height, scale);
        }
        return arranged;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (bitmap is not { } shown) return;
        context.DrawImage(shown, new Rect(0, 0, shown.PixelSize.Width, shown.PixelSize.Height), new Rect(shown.Size));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        closing.Stop();
        if (closed && Child is null) Reopen();
        SharpRail.Plugins.UI.Kit.Ui.ThemeChanged += PushTheme;
        PushTheme();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SharpRail.Plugins.UI.Kit.Ui.ThemeChanged -= PushTheme;
        if (!closed) closing.Start();
        base.OnDetachedFromVisualTree(e);
    }

    private void PushTheme()
    {
        if (!closed) surface.Theme(ThemeJson());
    }

    // Shown again after its surface closed: the plugin makes its control anew, at the size this control has.
    private void Reopen()
    {
        closed = false;
        (sent, sentScale) = (default, 0);
        Open();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void Close()
    {
        closing.Stop();
        if (closed) return;
        closed = true;
        _ = surface.DisposeAsync().AsTask();
        bitmap?.Dispose();
        bitmap = null;
    }

    private void Send(SurfaceInputKind kind, Point at = default, int button = 0, int modifiers = 0, Vector delta = default, KeyEventArgs? key = null, string text = "")
    {
        if (closed || Child is not null) return;
        surface.Input(new SurfaceInput(0, kind, at.X, at.Y, button, modifiers, delta.X, delta.Y, key is null ? 0 : (int)key.Key,
            key is null ? 0 : (int)key.PhysicalKey, key?.KeySymbol ?? text));
    }

    private int Modifiers(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Properties;
        return (int)e.KeyModifiers | (point.IsLeftButtonPressed ? 16 : 0) | (point.IsRightButtonPressed ? 32 : 0) | (point.IsMiddleButtonPressed ? 64 : 0);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Send(SurfaceInputKind.PointerMove, e.GetPosition(this), modifiers: Modifiers(e));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Send(SurfaceInputKind.PointerLeave, modifiers: (int)e.KeyModifiers);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(NavigationMethod.Pointer);
        var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            _ => MouseButton.None,
        };
        e.Pointer.Capture(this);
        Send(SurfaceInputKind.PointerDown, e.GetPosition(this), (int)button, Modifiers(e));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Send(SurfaceInputKind.PointerUp, e.GetPosition(this), (int)e.InitialPressMouseButton, Modifiers(e));
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Send(SurfaceInputKind.Wheel, e.GetPosition(this), modifiers: Modifiers(e), delta: e.Delta);
        e.Handled = true;
    }

    private static bool Keeps(KeyEventArgs e) => e.Key != Key.Escape && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Send(SurfaceInputKind.KeyDown, modifiers: (int)e.KeyModifiers, key: e);
        e.Handled = Keeps(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        Send(SurfaceInputKind.KeyUp, modifiers: (int)e.KeyModifiers, key: e);
        e.Handled = Keeps(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Text is { Length: > 0 } text) Send(SurfaceInputKind.Text, text: text);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (!closed) surface.Focus(true, e.NavigationMethod == NavigationMethod.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.NavigationMethod == NavigationMethod.Pointer);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!closed) surface.Focus(false, false, false);
    }

    // Tab left the plugin's last focusable element: focus moves on from this surface as it would from the plugin's own.
    private void Leave(bool backward)
    {
        if (!IsFocused) return;
        TopLevel.GetTopLevel(this)?.FocusManager?.TryMoveFocus(backward ? NavigationDirection.Previous : NavigationDirection.Next);
    }
}