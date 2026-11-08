using System.Text;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.Isolation;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The client's control for one surface of an external plugin (Security.SPEC.md, UI halves): the plugin's control
/// renders in its process on the host, and this shows its pixels. Frames are decoded off the UI thread and drawn 1:1 at
/// device pixels; the surface's size, scale and the kit theme go to the plugin, and pointer, wheel, key, text and
/// focus come back in. The plugin's popups (menus, flyouts, tooltips) show in a layer of their own beside the layer
/// that holds this control, so they reach past its slot but stay under the app's dialogs and Settings above it; a
/// press elsewhere in the app dismisses them. Detached for longer than a moment, the surface closes; attached again,
/// it opens a new one, for which the plugin makes its control again.
/// </summary>
internal sealed class RemoteSurface : Decorator
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly Func<IPluginSurface> open;
    private readonly Lock gate = new();
    private readonly DispatcherTimer closing;
    private IPluginSurface surface = null!;
    private readonly FrameImage main = new();
    // Under the gate: the pixels of each open popup, by its number.
    private readonly Dictionary<int, FrameImage> images = [];
    // On the UI thread: each open popup's control.
    private readonly Dictionary<int, RemotePopup> popups = [];
    private Canvas? layer;
    private TopLevel? top;
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
    /// <summary>The scale of the last frame applied, for the checks.</summary>
    internal double FrameScale { get; private set; }

    /// <summary>The decoded pixels (premultiplied BGRA) and their size, for the checks.</summary>
    internal (int Width, int Height, byte[] Pixels) Snapshot()
    {
        lock (gate) return (main.Width, main.Height, [.. main.Pixels]);
    }

    /// <summary>The popups shown now, for the checks.</summary>
    internal IReadOnlyCollection<RemotePopup> Popups => popups.Values;

    /// <summary>A popup's decoded pixels and their size, for the checks; empty once it closed.</summary>
    internal (int Width, int Height, byte[] Pixels) Snapshot(int popup)
    {
        lock (gate) return images.TryGetValue(popup, out var image) ? (image.Width, image.Height, [.. image.Pixels]) : (0, 0, []);
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
            main.Reset();
            images.Clear();
        }
        ClearPopups();
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
                if (message is SurfacePopupChanged { Placement: var placement })
                {
                    lock (gate)
                    {
                        if (!ReferenceEquals(mine, surface)) break;
                        if (!placement.Open) images.Remove(placement.Popup);
                        else if (!images.ContainsKey(placement.Popup)) images[placement.Popup] = new FrameImage();
                    }
                    Dispatcher.UIThread.Post(() => { if (ReferenceEquals(mine, surface) && !closed) Place(placement); });
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
                mine.Acknowledge(header.Sequence, header.Popup);
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
    // False once a newer surface replaced this one. A frame of a popup that closed meanwhile is dropped.
    private bool Apply(IPluginSurface mine, SurfaceFrameHeader header, byte[] data)
    {
        lock (gate)
        {
            if (!ReferenceEquals(mine, surface)) return false;
            var image = header.Popup == 0 ? main : images.GetValueOrDefault(header.Popup);
            if (image?.Apply(header, data) == true && header.Popup == 0) Keyframes++;
        }
        return true;
    }

    private bool Present(IPluginSurface mine, SurfaceFrameHeader header, int bytes, int moves, int tiles)
    {
        if (closed || !ReferenceEquals(mine, surface)) return false;
        if (header.Popup != 0)
        {
            lock (gate)
                if (images.TryGetValue(header.Popup, out var image)) image.Present(header.Scale);
            if (popups.TryGetValue(header.Popup, out var popup)) popup.InvalidateVisual();
            return true;
        }
        lock (gate) main.Present(header.Scale);
        FrameScale = header.Scale;
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
        lock (gate)
        {
            main.Reset();
            images.Clear();
        }
        ClearPopups();
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
        main.Draw(context);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        closing.Stop();
        if (closed && Child is null) Reopen();
        SharpRail.Plugins.UI.Kit.Ui.ThemeChanged += PushTheme;
        PushTheme();
        top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        // A window moved to a monitor of another scale re-renders the surface at it.
        top.ScalingChanged += Rescaled;
        top.AddHandler(PointerPressedEvent, PressedAnywhere, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (top is WindowBase window) window.Deactivated += Deactivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SharpRail.Plugins.UI.Kit.Ui.ThemeChanged -= PushTheme;
        if (top is not null)
        {
            top.ScalingChanged -= Rescaled;
            top.RemoveHandler(PointerPressedEvent, PressedAnywhere);
            if (top is WindowBase window) window.Deactivated -= Deactivated;
            top = null;
        }
        // Popups do not outlive the surface's place in the app. The layer leaves its panel after the tree is detached,
        // whose children are being walked now.
        Dismiss();
        RemoveLayer(later: true);
        if (!closed) closing.Start();
        base.OnDetachedFromVisualTree(e);
    }

    private void Rescaled(object? sender, EventArgs e) => InvalidateArrange();

    private void Deactivated(object? sender, EventArgs e) => Dismiss();

    // A press anywhere in the app outside this surface and its popups dismisses them, and still reaches what it pressed.
    private void PressedAnywhere(object? sender, PointerPressedEventArgs e)
    {
        if (popups.Count == 0 || e.Source is not Visual pressed) return;
        if (pressed == this || this.IsVisualAncestorOf(pressed) || popups.Values.Any(popup => popup == pressed || popup.IsVisualAncestorOf(pressed))) return;
        Dismiss();
    }

    private void Dismiss()
    {
        if (popups.Count > 0 && !closed && Child is null) surface.Dismiss();
    }

    // A popup opened, moved or closed: its control shows in the popup layer at its place from this surface's corner.
    private void Place(SurfacePopup placement)
    {
        if (!placement.Open)
        {
            if (popups.Remove(placement.Popup, out var gone)) layer?.Children.Remove(gone);
            if (popups.Count == 0) RemoveLayer();
            return;
        }
        if (!popups.TryGetValue(placement.Popup, out var popup))
        {
            FrameImage? image;
            lock (gate) image = images.GetValueOrDefault(placement.Popup);
            if (image is null) return;
            popups[placement.Popup] = popup = new RemotePopup(this, placement.Popup, image);
        }
        popup.Placement = placement;
        (popup.Width, popup.Height, popup.IsHitTestVisible) = (placement.Width, placement.Height, placement.Input);
        if (Layer() is { } shown && popup.Parent is null) shown.Children.Add(popup);
        Reposition();
    }

    private void ClearPopups()
    {
        popups.Clear();
        RemoveLayer();
    }

    /// <summary>
    /// The layer the popups show in, made on demand: a canvas in the panel that holds the app's overlays (the outermost
    /// panel when there is none), at the z-order of the child that holds this surface and after it, so the popups draw
    /// over that child and under anything the app raises above it.
    /// </summary>
    private Canvas? Layer()
    {
        if (layer is not null) return layer;
        if (TopLevel.GetTopLevel(this) is not { } shown) return null;
        (Panel Panel, Control Child)? overlays = null, outermost = null;
        Visual child = this;
        for (var parent = this.GetVisualParent(); parent is not null && parent != shown; child = parent, parent = parent.GetVisualParent())
        {
            if (parent is not Panel { IsItemsHost: false } panel || child is not Control holder) continue;
            outermost = (panel, holder);
            if (overlays is null && panel.Children.OfType<OverlayHost>().Any()) overlays = (panel, holder);
        }
        if ((overlays ?? outermost) is not { } found) return null;
        var (host, holding) = found;
        layer = new Canvas { Name = "PluginPopups", ZIndex = holding.ZIndex };
        if (host is Grid grid)
        {
            Grid.SetRowSpan(layer, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(layer, Math.Max(1, grid.ColumnDefinitions.Count));
        }
        host.Children.Add(layer);
        LayoutUpdated += Repositioned;
        return layer;
    }

    private void RemoveLayer(bool later = false)
    {
        if (layer is not { } gone) return;
        LayoutUpdated -= Repositioned;
        layer = null;
        gone.Children.Clear();
        if (later) Dispatcher.UIThread.Post(() => (gone.Parent as Panel)?.Children.Remove(gone));
        else (gone.Parent as Panel)?.Children.Remove(gone);
    }

    private void Repositioned(object? sender, EventArgs e) => Reposition();

    // Each popup at its place from this surface's corner, kept inside the layer when it would reach past it.
    private void Reposition()
    {
        if (layer is null || this.TranslatePoint(default, layer) is not { } origin) return;
        foreach (var popup in popups.Values)
        {
            if (popup.Placement is not { } placement) continue;
            var (x, y) = (origin.X + placement.X, origin.Y + placement.Y);
            if (layer.Bounds.Width > 0) x = Math.Max(0, Math.Min(x, layer.Bounds.Width - placement.Width));
            if (layer.Bounds.Height > 0) y = Math.Max(0, Math.Min(y, layer.Bounds.Height - placement.Height));
            if (Canvas.GetLeft(popup) != x) Canvas.SetLeft(popup, x);
            if (Canvas.GetTop(popup) != y) Canvas.SetTop(popup, y);
        }
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
        lock (gate)
        {
            main.Reset();
            images.Clear();
        }
        ClearPopups();
    }

    private void Send(SurfaceInputKind kind, Point at = default, int button = 0, int modifiers = 0, Vector delta = default, KeyEventArgs? key = null, string text = "", int popup = 0)
    {
        if (closed || Child is not null) return;
        surface.Input(new SurfaceInput(0, kind, at.X, at.Y, button, modifiers, delta.X, delta.Y, key is null ? 0 : (int)key.Key,
            key is null ? 0 : (int)key.PhysicalKey, key?.KeySymbol ?? text, popup));
    }

    private static int Modifiers(PointerEventArgs e, Visual relativeTo)
    {
        var point = e.GetCurrentPoint(relativeTo).Properties;
        return (int)e.KeyModifiers | (point.IsLeftButtonPressed ? 16 : 0) | (point.IsRightButtonPressed ? 32 : 0) | (point.IsMiddleButtonPressed ? 64 : 0);
    }

    private static MouseButton Pressed(PointerPressedEventArgs e, Visual relativeTo) => e.GetCurrentPoint(relativeTo).Properties.PointerUpdateKind switch
    {
        PointerUpdateKind.LeftButtonPressed => MouseButton.Left,
        PointerUpdateKind.RightButtonPressed => MouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
        _ => MouseButton.None,
    };

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Send(SurfaceInputKind.PointerMove, e.GetPosition(this), modifiers: Modifiers(e, this));
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
        e.Pointer.Capture(this);
        Send(SurfaceInputKind.PointerDown, e.GetPosition(this), (int)Pressed(e, this), Modifiers(e, this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Send(SurfaceInputKind.PointerUp, e.GetPosition(this), (int)e.InitialPressMouseButton, Modifiers(e, this));
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Send(SurfaceInputKind.Wheel, e.GetPosition(this), modifiers: Modifiers(e, this), delta: e.Delta);
        e.Handled = true;
    }

    // Escape stays with the plugin while one of its popups is open, which it closes; otherwise the app hears it too.
    private bool Keeps(KeyEventArgs e) => (e.Key != Key.Escape || popups.Count > 0) && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;

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
        // Focus moving elsewhere in the app (Tab, a dialog opening) dismisses the popups, as a press there would.
        Dismiss();
        if (!closed) surface.Focus(false, false, false);
    }

    // Tab left the plugin's last focusable element: focus moves on from this surface as it would from the plugin's own.
    private void Leave(bool backward)
    {
        if (!IsFocused) return;
        TopLevel.GetTopLevel(this)?.FocusManager?.TryMoveFocus(backward ? NavigationDirection.Previous : NavigationDirection.Next);
    }

    /// <summary>
    /// The pixels of one top-level the plugin shows (the surface or a popup) as the client holds them, and the bitmap the
    /// UI thread draws them from. Its owner's gate guards the pixels; the bitmap is the UI thread's.
    /// </summary>
    internal sealed class FrameImage
    {
        private long keyframe = -1;
        private WriteableBitmap? bitmap;

        public byte[] Pixels { get; private set; } = [];
        public int Width { get; private set; }
        public int Height { get; private set; }

        public void Reset()
        {
            (Width, Height, Pixels) = (0, 0, []);
            bitmap?.Dispose();
            bitmap = null;
        }

        /// <summary>Applies a frame message's moves and tiles; true when it started a keyframe.</summary>
        public bool Apply(SurfaceFrameHeader header, byte[] data)
        {
            if (header.Width != Width || header.Height != Height)
            {
                (Width, Height) = (header.Width, header.Height);
                Pixels = new byte[Width * Height * 4];
            }
            // A keyframe repaints from transparent, once, at its first message.
            var started = header.Keyframe && header.Sequence != keyframe;
            if (started)
            {
                keyframe = header.Sequence;
                Array.Clear(Pixels);
            }
            var stride = Width * 4;
            foreach (var move in header.Moves) SurfacePixels.Move(Pixels, stride, move);
            var offset = 0;
            foreach (var tile in header.Tiles)
            {
                TileCodec.Decode(tile.Codec, data.AsSpan(offset, tile.Length), Pixels, stride, tile.X, tile.Y, tile.Width, tile.Height);
                offset += tile.Length;
            }
            return started;
        }

        /// <summary>Copies the pixels into the bitmap drawn at <paramref name="scale"/>.</summary>
        public void Present(double scale)
        {
            if (Width == 0 || Height == 0)
            {
                bitmap?.Dispose();
                bitmap = null;
                return;
            }
            if (bitmap is null || bitmap.PixelSize != new PixelSize(Width, Height) || bitmap.Dpi.X != 96 * scale)
            {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            using var target = bitmap.Lock();
            for (var row = 0; row < Height; row++)
                System.Runtime.InteropServices.Marshal.Copy(Pixels, row * Width * 4, target.Address + row * target.RowBytes, Width * 4);
        }

        /// <summary>Draws the bitmap 1:1 at device pixels from the corner.</summary>
        public void Draw(DrawingContext context)
        {
            if (bitmap is not { } shown) return;
            context.DrawImage(shown, new Rect(0, 0, shown.PixelSize.Width, shown.PixelSize.Height), new Rect(shown.Size));
        }
    }

    /// <summary>
    /// One popup of the surface, shown in the popup layer: it draws the popup's pixels and sends the pointer to it. It
    /// never takes focus, so keys keep going to the surface, which hands them to the element focused in the plugin.
    /// </summary>
    internal sealed class RemotePopup : Control
    {
        private readonly RemoteSurface owner;
        private readonly FrameImage image;

        internal RemotePopup(RemoteSurface owner, int number, FrameImage image)
        {
            (this.owner, this.image, Number) = (owner, image, number);
            Name = "PluginPopup";
            Focusable = false;
            ClipToBounds = true;
            RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        }

        /// <summary>The number the plugin gave the popup within its surface.</summary>
        internal int Number { get; }
        internal SurfacePopup? Placement { get; set; }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            image.Draw(context);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            owner.Send(SurfaceInputKind.PointerMove, e.GetPosition(this), modifiers: Modifiers(e, this), popup: Number);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            owner.Send(SurfaceInputKind.PointerLeave, modifiers: (int)e.KeyModifiers, popup: Number);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Pointer.Capture(this);
            owner.Send(SurfaceInputKind.PointerDown, e.GetPosition(this), (int)Pressed(e, this), Modifiers(e, this), popup: Number);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            owner.Send(SurfaceInputKind.PointerUp, e.GetPosition(this), (int)e.InitialPressMouseButton, Modifiers(e, this), popup: Number);
            e.Handled = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            owner.Send(SurfaceInputKind.Wheel, e.GetPosition(this), modifiers: Modifiers(e, this), delta: e.Delta, popup: Number);
            e.Handled = true;
        }
    }
}