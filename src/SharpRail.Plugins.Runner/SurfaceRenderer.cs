using System.Buffers;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// The plugin's mounted controls, each in its own headless top-level per client and mount. On the UI thread only. A
/// timer renders each surface whose last frame the client acknowledged, every 16 ms while anything happened in the last
/// half second and every 200 ms otherwise, diffs it against what the client holds in 64-pixel tiles and sends the
/// changed ones; scrolling recorded since the last frame goes first as moves.
/// </summary>
internal sealed class SurfaceRenderer
{
    private static readonly TimeSpan Busy = TimeSpan.FromMilliseconds(16), Idle = TimeSpan.FromMilliseconds(200);
    private const long HotMilliseconds = 500;

    private readonly IsolationPeer peer;
    private readonly Dictionary<int, SurfaceView> views = [];
    private readonly DispatcherTimer timer;
    private long hotUntil;

    private static SurfaceRenderer? current;

    static SurfaceRenderer()
    {
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>((viewer, args) => (TopLevel.GetTopLevel(viewer) as SurfaceWindow)?.View.Scrolled(viewer, args));
        // A popup opening or closing on its own (a tooltip after its delay) is rendered without waiting for the idle pace.
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((_, _) => current?.Heat());
    }

    public SurfaceRenderer(IsolationPeer peer)
    {
        this.peer = peer;
        timer = new DispatcherTimer(Idle, DispatcherPriority.Background, (_, _) => Tick());
        current = this;
    }

    /// <summary>Shows a factory's control as surface <paramref name="id"/> of a client's context.</summary>
    public void Mount(long context, int id, Control control)
    {
        if (views.ContainsKey(id)) throw new IsoCallException(IsoError.InvalidParams, $"Surface {id} is already open.");
        views[id] = new SurfaceView(this, context, id, control);
        Heat();
        timer.Start();
    }

    private readonly Queue<Action> inbox = new();
    private bool draining;

    /// <summary>
    /// Queues a client's message for the UI thread. Messages run in arrival order, one at a time: applying input runs
    /// the dispatcher's pending jobs, so a message is never applied inside another.
    /// </summary>
    public void Enqueue(Action message)
    {
        lock (inbox) inbox.Enqueue(message);
        Dispatcher.UIThread.Post(Drain);
    }

    private void Drain()
    {
        if (draining) return;
        draining = true;
        try
        {
            while (true)
            {
                Action next;
                lock (inbox)
                    if (!inbox.TryDequeue(out next!)) return;
                try { next(); }
                catch (IsoCallException error) { Console.Error.WriteLine("[error] A surface message was refused: " + error.Message); }
            }
        }
        finally { draining = false; }
    }

    public void Resize(SurfaceResize resize) => View(resize.Surface)?.Resize(resize.Width, resize.Height, resize.Scale);
    public void Input(SurfaceInput input) => View(input.Surface)?.Input(input);
    public void Acknowledge(SurfaceAck ack) => View(ack.Surface)?.Acknowledge(ack.Sequence, ack.Popup);
    public void Dismiss(SurfaceDismiss dismiss) => View(dismiss.Surface)?.Dismiss();
    public void Focus(SurfaceFocus focus) => View(focus.Surface)?.Focus(focus.Enter, focus.Backward, focus.Pointer);
    public void Keyframe(SurfaceKeyframe keyframe) => View(keyframe.Surface)?.Keyframe();

    public void Close(int id)
    {
        if (!views.Remove(id, out var view)) return;
        view.Close();
        if (views.Count == 0) timer.Stop();
    }

    /// <summary>Closes every surface of a client's context, as its activation ends.</summary>
    public void CloseContext(long context)
    {
        foreach (var id in views.Values.Where(view => view.Context == context).Select(view => view.Id).ToArray()) Close(id);
    }

    /// <summary>A fresh render of a surface and its named controls' bounds, for the checks.</summary>
    public SurfaceCapture Capture(SurfaceCaptureRequest request) =>
        View(request.Surface)?.Capture(request.Names, request.Popup) ?? throw new IsoCallException(IsoError.InvalidParams, $"Surface {request.Surface} is not open.");

    private SurfaceView? View(int id) => views.GetValueOrDefault(id);

    private void Heat()
    {
        hotUntil = Environment.TickCount64 + HotMilliseconds;
        timer.Interval = Busy;
    }

    private void Tick()
    {
        foreach (var view in views.Values.ToArray())
        {
            try { view.Tick(); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[error] Surface {view.Id} stopped: {error.Message}");
                Close(view.Id);
            }
        }
        if (Environment.TickCount64 > hotUntil) timer.Interval = Idle;
    }

    private sealed class SurfaceWindow(SurfaceView view) : Window
    {
        public SurfaceView View { get; } = view;
    }

    /// <summary>
    /// Lays the plugin's control out at the surface's width (without a limit while the client has given it none) and
    /// records the size it asks for with no height limit, which the client sizes its slot by, then arranges it in the
    /// surface.
    /// </summary>
    private sealed class SurfaceRoot : Decorator
    {
        public Size Wanted { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Child is not { } child) return default;
            child.Measure(new Size(availableSize.Width <= 1 ? double.PositiveInfinity : availableSize.Width, double.PositiveInfinity));
            Wanted = child.DesiredSize;
            return availableSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Child?.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }

    /// <summary>
    /// One surface: its top-level and its open popups, each a headless top-level of its own that the client places, and
    /// the frames of each.
    /// </summary>
    private sealed class SurfaceView
    {
        private readonly SurfaceRenderer renderer;
        private readonly SurfaceWindow window;
        private readonly SurfaceRoot root;
        private readonly FrameSource main;
        private readonly List<SurfaceMove> moves = [];
        private readonly Dictionary<PopupRoot, PopupView> popups = [];
        private double scale = 1;
        private bool sized, closed;
        private int lastPopup;

        public SurfaceView(SurfaceRenderer renderer, long context, int id, Control control)
        {
            this.renderer = renderer;
            Context = context;
            Id = id;
            root = new SurfaceRoot { Child = control };
            window = new SurfaceWindow(this)
            {
                Content = root,
                Background = Brushes.Transparent,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                FontFamily = Ui.InterfaceFont,
                Width = 1,
                Height = 1,
            };
            main = new FrameSource(this, window, 0);
            window.Show();
        }

        public long Context { get; }
        public int Id { get; }

        public void Resize(double dipWidth, double dipHeight, double newScale)
        {
            if (!(dipWidth is >= 0 and <= SurfaceLimits.MostSide) || !(dipHeight is >= 0 and <= SurfaceLimits.MostSide) ||
                !(newScale is >= SurfaceLimits.LeastScale and <= SurfaceLimits.MostScale) || dipWidth * newScale > SurfaceLimits.MostSide || dipHeight * newScale > SurfaceLimits.MostSide)
                throw new IsoCallException(IsoError.InvalidParams, "The surface size is out of range.");
            window.Width = Math.Max(1, dipWidth);
            window.Height = Math.Max(1, dipHeight);
            if (newScale != scale)
            {
                scale = newScale;
                window.SetRenderScaling(newScale);
                foreach (var popup in popups.Keys.ToArray())
                    if (popup.PlatformImpl is not null) popup.SetRenderScaling(newScale);
            }
            sized = true;
            renderer.Heat();
        }

        public void Acknowledge(long acknowledged, int popup)
        {
            Source(popup)?.Acknowledge(acknowledged);
            renderer.Heat();
        }

        // The client holds nothing it can trust: the next frame of the surface and of each popup repaints from transparent.
        public void Keyframe()
        {
            main.Keyframe();
            foreach (var popup in popups.Values) popup.Frames.Keyframe();
            moves.Clear();
            renderer.Heat();
        }

        // A press outside the surface and its popups on the client: the popups that close on such a press close.
        public void Dismiss()
        {
            foreach (var popup in window.OpenedPopups.ToArray())
                if (popup.IsLightDismissEnabled) popup.Close();
            renderer.Heat();
        }

        public void Scrolled(ScrollViewer viewer, ScrollChangedEventArgs args)
        {
            if (args.OffsetDelta == default || !sized) return;
            var presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault(candidate => candidate.TemplatedParent == viewer);
            if (presenter?.TranslatePoint(default, window) is not { } origin) return;
            static int? Whole(double value) => Math.Abs(value - Math.Round(value)) < 0.001 ? (int)Math.Round(value) : null;
            if (Whole(origin.X * scale) is not { } x || Whole(origin.Y * scale) is not { } y || Whole(presenter.Bounds.Width * scale) is not { } w ||
                Whole(presenter.Bounds.Height * scale) is not { } h || Whole(-args.OffsetDelta.X * scale) is not { } dx || Whole(-args.OffsetDelta.Y * scale) is not { } dy)
                return;
            if (moves is [.., var last] && (last.X, last.Y, last.Width, last.Height) == (x, y, w, h)) moves[^1] = last with { Dx = last.Dx + dx, Dy = last.Dy + dy };
            else moves.Add(new(x, y, w, h, dx, dy));
            renderer.Heat();
        }

        public void Input(SurfaceInput input)
        {
            // Input for a popup that closed meanwhile has nowhere to go.
            TopLevel? target = input.Popup == 0 ? window : popups.Values.FirstOrDefault(popup => popup.Id == input.Popup)?.Root;
            if (target?.PlatformImpl is null) return;
            var point = new Point(input.X, input.Y);
            var modifiers = (RawInputModifiers)(input.Modifiers & 0x7fff);
            switch (input.Kind)
            {
                case SurfaceInputKind.PointerMove: target.MouseMove(point, modifiers); break;
                case SurfaceInputKind.PointerLeave: target.MouseMove(new Point(-1, -1), modifiers); break;
                case SurfaceInputKind.PointerDown: target.MouseDown(point, Button(input.Button), modifiers); break;
                case SurfaceInputKind.PointerUp: target.MouseUp(point, Button(input.Button), modifiers); break;
                case SurfaceInputKind.Wheel: target.MouseWheel(point, new Vector(Clamp(input.DeltaX), Clamp(input.DeltaY)), modifiers); break;
                // Keys and text go to the surface's top-level: the keyboard reaches the focused element, in a popup too.
                case SurfaceInputKind.KeyDown:
                    if ((Key)input.Key == Key.Tab && Leaving(modifiers.HasFlag(RawInputModifiers.Shift))) break;
                    if ((Key)input.Key == Key.Escape && Escaped()) break;
                    window.KeyPress(KeyOf(input.Key), modifiers, PhysicalOf(input.PhysicalKey), Symbol(input.Text));
                    break;
                case SurfaceInputKind.KeyUp: window.KeyRelease(KeyOf(input.Key), modifiers, PhysicalOf(input.PhysicalKey), Symbol(input.Text)); break;
                case SurfaceInputKind.Text:
                    if (input.Text.Length is > 0 and <= 256) window.KeyTextInput(input.Text);
                    break;
                default: throw new IsoCallException(IsoError.InvalidParams, "Unknown input.");
            }
            renderer.Heat();
        }

        private static double Clamp(double delta) => double.IsFinite(delta) ? Math.Clamp(delta, -100, 100) : 0;
        private static MouseButton Button(int button) => button is >= 0 and <= 5 ? (MouseButton)button : MouseButton.None;
        private static Key KeyOf(int key) => Enum.IsDefined((Key)key) ? (Key)key : Key.None;
        private static PhysicalKey PhysicalOf(int key) => Enum.IsDefined((PhysicalKey)key) ? (PhysicalKey)key : PhysicalKey.None;
        private static string? Symbol(string text) => text.Length is > 0 and <= 16 ? text : null;

        // Escape while a popup that closes on a press elsewhere is open, and focus is not in a popup that would hear it:
        // the newest such popup closes, as a native menu would, and the key goes no further.
        private bool Escaped()
        {
            if (window.FocusManager?.GetFocusedElement() is Visual focused && TopLevel.GetTopLevel(focused) is PopupRoot) return false;
            if (popups.Values.Where(view => view.Popup.IsLightDismissEnabled && view.Popup.IsOpen).MaxBy(view => view.Id) is not { } newest) return false;
            newest.Popup.Close();
            return true;
        }

        // Tab past the last focusable element (Shift+Tab past the first) leaves the surface: the client moves focus on.
        private bool Leaving(bool backward)
        {
            if (window.FocusManager is not { } focus) return false;
            var edge = backward ? focus.FindFirstFocusableElement() : focus.FindLastFocusableElement();
            if (edge is null || !ReferenceEquals(focus.GetFocusedElement(), edge)) return false;
            renderer.peer.OneWay(SurfaceProtocol.FocusLeave, SurfaceProtocol.Encode(new SurfaceFocusLeave(Id, backward)), Context);
            window.Focus();
            return true;
        }

        public void Focus(bool enter, bool backward, bool pointer)
        {
            if (!enter)
            {
                window.Focus();
                return;
            }
            window.Activate();
            if (pointer || window.FocusManager is not { } focus) return;
            (backward ? focus.FindLastFocusableElement() : focus.FindFirstFocusableElement())?.Focus(NavigationMethod.Tab);
            renderer.Heat();
        }

        public void Tick()
        {
            if (closed || !sized) return;
            Track();
            main.Tick(scale, root.Wanted, moves);
            foreach (var popup in popups.Values.ToArray())
                if (popup.Root.PlatformImpl is not null) popup.Frames.Tick(scale, default, null);
        }

        // The popups open on the surface now (a submenu's under its menu's), numbered as they appear; each one's place is
        // sent before its first frame and again when it moves or resizes, and its closing after its last frame.
        private void Track()
        {
            var open = new List<(Popup Popup, PopupRoot Root)>();
            void Collect(IReadOnlyList<Popup> list)
            {
                foreach (var popup in list.ToArray())
                    // A popup the plugin asked to draw in its surface's overlay has no top-level of its own.
                    if (popup.Child is { } child && TopLevel.GetTopLevel(child) is PopupRoot { PlatformImpl: not null } host)
                    {
                        open.Add((popup, host));
                        Collect(popup.OpenedPopups);
                    }
            }
            Collect(window.OpenedPopups);
            foreach (var (host, gone) in popups.Where(pair => !open.Any(item => item.Root == pair.Key)).ToArray())
            {
                popups.Remove(host);
                if (gone.Placement is { } last) Place(last with { Open = false });
            }
            var origin = window.PointToScreen(default);
            foreach (var (popup, host) in open)
            {
                if (!popups.TryGetValue(host, out var view))
                {
                    if (popups.Count >= SurfaceLimits.MostPopups) continue;
                    host.SetRenderScaling(scale);
                    view = new PopupView(++lastPopup, popup, host, new FrameSource(this, host, lastPopup));
                    popups[host] = view;
                }
                var at = host.PointToScreen(default);
                var placement = new SurfacePopup(Id, view.Id, (at.X - origin.X) / scale, (at.Y - origin.Y) / scale, Math.Max(1, host.ClientSize.Width), Math.Max(1, host.ClientSize.Height),
                    true, popup.IsHitTestVisible && popup.Child is not ToolTip);
                if (placement == view.Placement) continue;
                view.Placement = placement;
                Place(placement);
            }
        }

        private void Place(SurfacePopup placement) =>
            renderer.peer.OneWay(SurfaceProtocol.Popup, SurfaceProtocol.Encode(placement), Context);

        private FrameSource? Source(int popup) => popup == 0 ? main : popups.Values.FirstOrDefault(view => view.Id == popup)?.Frames;

        private void Send(SurfaceFrameHeader header, byte[] bytes) => renderer.peer.OneWay(SurfaceProtocol.Frame, SurfaceProtocol.Encode(header), Context, bytes);

        internal static void Copy(ILockedFramebuffer locked, byte[] target)
        {
            var rowBytes = locked.Size.Width * 4;
            var swap = locked.Format == PixelFormat.Rgba8888;
            unsafe
            {
                for (var row = 0; row < locked.Size.Height; row++)
                {
                    var source = new ReadOnlySpan<byte>((byte*)locked.Address + row * locked.RowBytes, rowBytes);
                    var line = target.AsSpan(row * rowBytes, rowBytes);
                    source.CopyTo(line);
                    if (!swap) continue;
                    for (var index = 0; index < rowBytes; index += 4) (line[index], line[index + 2]) = (line[index + 2], line[index]);
                }
            }
        }

        public SurfaceCapture Capture(List<string> names, int popup)
        {
            TopLevel target = popup == 0 ? window : popups.Values.FirstOrDefault(view => view.Id == popup)?.Root ?? throw new IsoCallException(IsoError.InvalidParams, $"Popup {popup} is not open.");
            using var frame = target.CaptureRenderedFrame() ?? throw new IsoCallException(IsoError.Failed, "The surface has not rendered.");
            byte[] pixels;
            int w, h;
            using (var locked = frame.Lock())
            {
                (w, h) = (locked.Size.Width, locked.Size.Height);
                pixels = new byte[w * h * 4];
                Copy(locked, pixels);
            }
            var bounds = new Dictionary<string, double[]>();
            foreach (var control in target.GetVisualDescendants().OfType<Control>())
                if (control.Name is { } name && names.Contains(name) && !bounds.ContainsKey(name) && control.TranslatePoint(default, target) is { } at)
                    bounds[name] = [at.X, at.Y, control.Bounds.Width, control.Bounds.Height];
            return new SurfaceCapture(w, h, Convert.ToBase64String(pixels), bounds);
        }

        public void Close()
        {
            closed = true;
            window.Close();
        }

        /// <summary>An open popup: its number, the plugin's popup and its top-level, its frames and the place last sent.</summary>
        private sealed class PopupView(int id, Popup popup, PopupRoot root, FrameSource frames)
        {
            public int Id { get; } = id;
            public Popup Popup { get; } = popup;
            public PopupRoot Root { get; } = root;
            public FrameSource Frames { get; } = frames;
            public SurfacePopup? Placement { get; set; }
        }

        /// <summary>
        /// One top-level's frames: the pixels the client holds, the frame in flight, and whether the next one repaints
        /// from transparent. A popup's first frame does.
        /// </summary>
        private sealed class FrameSource(SurfaceView view, TopLevel top, int popup)
        {
            private byte[] held = [], next = [];
            private int width, height;
            private Size wanted;
            private bool inFlight, keyframe = popup != 0;
            private long sequence;

            public void Acknowledge(long acknowledged)
            {
                if (acknowledged == sequence) inFlight = false;
            }

            public void Keyframe()
            {
                inFlight = false;
                keyframe = true;
                Array.Clear(held);
            }

            /// <summary>Renders and sends what changed, the surface's <paramref name="moves"/> first; nothing while a frame is in flight.</summary>
            public void Tick(double scale, Size desired, List<SurfaceMove>? moves)
            {
                if (inFlight) return;
                using var frame = top.CaptureRenderedFrame();
                if (frame is null) return;
                var resized = false;
                using (var locked = frame.Lock())
                {
                    var (w, h) = (locked.Size.Width, locked.Size.Height);
                    if (w != width || h != height)
                    {
                        (width, height) = (w, h);
                        held = new byte[w * h * 4];
                        next = new byte[w * h * 4];
                        moves?.Clear();
                        resized = true;
                    }
                    Copy(locked, next);
                }
                var stride = width * 4;
                var sentMoves = moves?.Where(move => SurfacePixels.Fits(move, width, height)).ToList() ?? [];
                moves?.Clear();
                foreach (var move in sentMoves) SurfacePixels.Move(held, stride, move);
                var output = new ArrayBufferWriter<byte>();
                var tiles = new List<(SurfaceTile Tile, int Start)>();
                for (var y = 0; y < height; y += SurfaceLimits.TileSide)
                    for (var x = 0; x < width; x += SurfaceLimits.TileSide)
                    {
                        var (w, h) = (Math.Min(SurfaceLimits.TileSide, width - x), Math.Min(SurfaceLimits.TileSide, height - y));
                        if (Same(x, y, w, h, stride)) continue;
                        var start = output.WrittenCount;
                        var codec = TileCodec.Encode(next, stride, x, y, w, h, output);
                        tiles.Add((new SurfaceTile(x, y, w, h, codec, output.WrittenCount - start), start));
                    }
                if (!resized && !keyframe && tiles.Count == 0 && sentMoves.Count == 0 && desired == wanted) return;
                wanted = desired;
                (held, next) = (next, held);
                sequence++;
                inFlight = true;
                Send(scale, keyframe ? [] : sentMoves, tiles, output.WrittenSpan);
                keyframe = false;
                view.renderer.Heat();
            }

            private bool Same(int x, int y, int w, int h, int stride)
            {
                for (var row = 0; row < h; row++)
                {
                    var offset = (y + row) * stride + x * 4;
                    if (!next.AsSpan(offset, w * 4).SequenceEqual(held.AsSpan(offset, w * 4))) return false;
                }
                return true;
            }

            // Splits a frame into messages under the client's caps; moves ride in the first.
            private void Send(double scale, List<SurfaceMove> sentMoves, List<(SurfaceTile Tile, int Start)> tiles, ReadOnlySpan<byte> bytes)
            {
                var index = 0;
                var first = true;
                do
                {
                    var batch = new List<SurfaceTile>();
                    var start = index < tiles.Count ? tiles[index].Start : 0;
                    var length = 0;
                    while (index < tiles.Count && batch.Count < SurfaceLimits.MostTiles && (batch.Count == 0 || length + tiles[index].Tile.Length <= SurfaceLimits.MessageBytes))
                    {
                        length += tiles[index].Tile.Length;
                        batch.Add(tiles[index++].Tile);
                    }
                    view.Send(new SurfaceFrameHeader(view.Id, sequence, width, height, scale, wanted.Width, wanted.Height, index >= tiles.Count, first ? sentMoves : [], batch, keyframe, popup),
                        bytes.Slice(start, length).ToArray());
                    first = false;
                }
                while (index < tiles.Count);
            }
        }
    }
}