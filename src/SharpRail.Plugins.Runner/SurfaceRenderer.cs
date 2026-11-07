using System.Buffers;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

    static SurfaceRenderer() =>
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>((viewer, args) => (TopLevel.GetTopLevel(viewer) as SurfaceWindow)?.View.Scrolled(viewer, args));

    public SurfaceRenderer(IsolationPeer peer)
    {
        this.peer = peer;
        timer = new DispatcherTimer(Idle, DispatcherPriority.Background, (_, _) => Tick());
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
    public void Acknowledge(SurfaceAck ack) => View(ack.Surface)?.Acknowledge(ack.Sequence);
    public void Focus(SurfaceFocus focus) => View(focus.Surface)?.Focus(focus.Enter, focus.Backward, focus.Pointer);

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
        View(request.Surface)?.Capture(request.Names) ?? throw new IsoCallException(IsoError.InvalidParams, $"Surface {request.Surface} is not open.");

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

    /// <summary>One surface: its top-level, the pixels the client holds and the frame in flight.</summary>
    private sealed class SurfaceView
    {
        private readonly SurfaceRenderer renderer;
        private readonly SurfaceWindow window;
        private readonly SurfaceRoot root;
        private readonly List<SurfaceMove> moves = [];
        private byte[] held = [], next = [];
        private int width, height;
        private double scale = 1;
        private Size wanted;
        private bool sized, inFlight, closed;
        private long sequence;

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
            }
            sized = true;
            renderer.Heat();
        }

        public void Acknowledge(long acknowledged)
        {
            if (acknowledged == sequence) inFlight = false;
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
            var point = new Point(input.X, input.Y);
            var modifiers = (RawInputModifiers)(input.Modifiers & 0x7fff);
            switch (input.Kind)
            {
                case SurfaceInputKind.PointerMove: window.MouseMove(point, modifiers); break;
                case SurfaceInputKind.PointerLeave: window.MouseMove(new Point(-1, -1), modifiers); break;
                case SurfaceInputKind.PointerDown: window.MouseDown(point, Button(input.Button), modifiers); break;
                case SurfaceInputKind.PointerUp: window.MouseUp(point, Button(input.Button), modifiers); break;
                case SurfaceInputKind.Wheel: window.MouseWheel(point, new Vector(Clamp(input.DeltaX), Clamp(input.DeltaY)), modifiers); break;
                case SurfaceInputKind.KeyDown:
                    if ((Key)input.Key == Key.Tab && Leaving(modifiers.HasFlag(RawInputModifiers.Shift))) break;
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
            if (closed || !sized || inFlight) return;
            using var frame = window.CaptureRenderedFrame();
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
                    moves.Clear();
                    resized = true;
                }
                Copy(locked, next);
            }
            var stride = width * 4;
            var sentMoves = moves.Where(move => SurfacePixels.Fits(move, width, height)).ToList();
            moves.Clear();
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
            var desired = root.Wanted;
            if (!resized && tiles.Count == 0 && sentMoves.Count == 0 && desired == wanted) return;
            wanted = desired;
            (held, next) = (next, held);
            sequence++;
            inFlight = true;
            Send(sentMoves, tiles, output.WrittenSpan);
            renderer.Heat();
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
        private void Send(List<SurfaceMove> sentMoves, List<(SurfaceTile Tile, int Start)> tiles, ReadOnlySpan<byte> bytes)
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
                var header = new SurfaceFrameHeader(Id, sequence, width, height, scale, wanted.Width, wanted.Height, index >= tiles.Count, first ? sentMoves : [], batch);
                renderer.peer.OneWay(SurfaceProtocol.Frame, SurfaceProtocol.Encode(header), Context, bytes.Slice(start, length).ToArray());
                first = false;
            }
            while (index < tiles.Count);
        }

        private static void Copy(ILockedFramebuffer locked, byte[] target)
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

        public SurfaceCapture Capture(List<string> names)
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new IsoCallException(IsoError.Failed, "The surface has not rendered.");
            byte[] pixels;
            int w, h;
            using (var locked = frame.Lock())
            {
                (w, h) = (locked.Size.Width, locked.Size.Height);
                pixels = new byte[w * h * 4];
                Copy(locked, pixels);
            }
            var bounds = new Dictionary<string, double[]>();
            foreach (var control in root.GetVisualDescendants().OfType<Control>())
                if (control.Name is { } name && names.Contains(name) && !bounds.ContainsKey(name) && control.TranslatePoint(default, window) is { } at)
                    bounds[name] = [at.X, at.Y, control.Bounds.Width, control.Bounds.Height];
            return new SurfaceCapture(w, h, Convert.ToBase64String(pixels), bounds);
        }

        public void Close()
        {
            closed = true;
            window.Close();
        }
    }
}