using System.Buffers;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
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
        // A window the plugin opens (a dialog) becomes a remote top-level of the surface it belongs to.
        Window.WindowOpenedEvent.AddClassHandler<Window>((opened, _) => current?.Opened(opened));
    }

    // The surface that last had input from its client: a window opened with no owner belongs to it.
    private SurfaceView? lastInput;

    /// <summary>
    /// A window the plugin opened: it joins the surface of its owner (or of its owner's surface), else the surface that
    /// last had input; with none, it closes again, since no client could show it.
    /// </summary>
    private void Opened(Window opened)
    {
        if (opened is SurfaceWindow) return;
        SurfaceView? owner = null;
        for (var at = opened.Owner; at is not null && owner is null; at = (at as Window)?.Owner)
            owner = at is SurfaceWindow surface ? surface.View : views.Values.FirstOrDefault(view => view.Holds(at));
        owner ??= lastInput is { } last && views.ContainsValue(last) ? last : null;
        if (owner is null)
        {
            Console.Error.WriteLine("[error] The plugin opened a window that belongs to no view, so it closed.");
            opened.Close();
            return;
        }
        owner.Add(opened);
        Heat();
    }

    // While a modal dialog of a client's context is open, that client's other surfaces and popups take no input.
    private bool Blocked(long context, TopLevel target) =>
        views.Values.Any(view => view.Context == context && view.Modal is { } modal && !ReferenceEquals(modal, target));

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
    /// One surface: its top-level, its open popups and the windows (dialogs) the plugin opened from it, each a top-level
    /// of its own that the client places, and the frames of each.
    /// </summary>
    private sealed class SurfaceView
    {
        private readonly SurfaceRenderer renderer;
        private readonly SurfaceWindow window;
        private readonly SurfaceRoot root;
        private readonly FrameSource main;
        private readonly List<SurfaceMove> moves = [];
        private readonly Dictionary<TopLevel, PopupView> popups = [];
        private readonly List<Window> dialogs = [];
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

        /// <summary>The newest modal dialog of this surface that is open, if any.</summary>
        public Window? Modal => dialogs.LastOrDefault(dialog => Remote(dialog) is { IsModal: true });

        /// <summary>Whether <paramref name="top"/> is one of this surface's dialogs.</summary>
        public bool Holds(WindowBase top) => top is Window dialog && dialogs.Contains(dialog);

        /// <summary>A window the plugin opened from this surface, shown as a dialog from the next tick on.</summary>
        public void Add(Window dialog)
        {
            if (closed || dialogs.Contains(dialog)) return;
            dialogs.Add(dialog);
            Remote(dialog)?.SetRenderScaling(scale);
            dialog.Closed += (_, _) =>
            {
                dialogs.Remove(dialog);
                renderer.Heat();
            };
        }

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
                Remote(window)?.SetRenderScaling(newScale);
                foreach (var top in popups.Keys.Concat(dialogs).Distinct().ToArray()) Remote(top)?.SetRenderScaling(newScale);
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

        // A press outside the surface and its popups on the client, which hid them all: every popup closes, the newest
        // first. Dialogs stay.
        public void Dismiss()
        {
            foreach (var view in popups.Values.OrderByDescending(view => view.Id).ToArray())
                if (view.Popup is { } popup) Shut(popup);
            foreach (var popup in window.OpenedPopups.ToArray()) Shut(popup);
            renderer.Heat();
        }

        // A tooltip closes through its control, so hovering it again opens it again.
        private static void Shut(Popup popup)
        {
            if (popup.Child is ToolTip && popup.PlacementTarget is { } adorned) ToolTip.SetIsOpen(adorned, false);
            if (popup.IsOpen) popup.Close();
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
            var popupView = input.Popup == 0 ? null : popups.Values.FirstOrDefault(popup => popup.Id == input.Popup);
            TopLevel? top = input.Popup == 0 ? window : popupView?.Root;
            if (top is null || Remote(top) is not { } target) return;
            renderer.lastInput = this;
            if (input.Kind == SurfaceInputKind.CloseDialog)
            {
                // The client's frame asks the dialog to close; the plugin may still keep it open.
                if (popupView?.Dialog is { } closing) closing.Close();
                renderer.Heat();
                return;
            }
            if (renderer.Blocked(Context, top)) return;
            // Keys go to the dialog they are for, else to the surface's top-level, which hands them to its popups.
            var dialogKeys = popupView?.Dialog is not null;
            if ((dialogKeys ? target : Remote(window)) is not { } keys) return;
            var point = new Point(input.X, input.Y);
            var modifiers = (RawInputModifiers)(input.Modifiers & 0x7fff);
            switch (input.Kind)
            {
                case SurfaceInputKind.PointerMove: target.PointerMove(point, modifiers); break;
                case SurfaceInputKind.PointerLeave: target.PointerMove(new Point(-1, -1), modifiers); break;
                case SurfaceInputKind.PointerDown: target.PointerDown(point, Button(input.Button), modifiers); break;
                case SurfaceInputKind.PointerUp: target.PointerUp(point, Button(input.Button), modifiers); break;
                case SurfaceInputKind.Wheel: target.Wheel(point, new Vector(Clamp(input.DeltaX), Clamp(input.DeltaY)), modifiers); break;
                // Keys and text go to the surface's top-level: the keyboard reaches the focused element, in a popup too.
                case SurfaceInputKind.KeyDown:
                    if (!dialogKeys && (Key)input.Key == Key.Tab && Leaving(modifiers.HasFlag(RawInputModifiers.Shift))) break;
                    if (!dialogKeys && (Key)input.Key == Key.Escape && Escaped()) break;
                    keys.Key(true, KeyOf(input.Key), modifiers, PhysicalOf(input.PhysicalKey), Symbol(input.Text));
                    break;
                case SurfaceInputKind.KeyUp: keys.Key(false, KeyOf(input.Key), modifiers, PhysicalOf(input.PhysicalKey), Symbol(input.Text)); break;
                case SurfaceInputKind.Text:
                    if (input.Text.Length is > 0 and <= 256) keys.Text(input.Text);
                    break;
                // Composition goes to the top-level whose text field reported its caret: the surface, a popup or a dialog.
                case SurfaceInputKind.Preedit:
                    if (input.Text.Length <= 256) target.Preedit(input.Text, Math.Clamp(input.Caret, 0, input.Text.Length));
                    break;
                // An accessibility action acts on a node of the top-level the client named.
                case SurfaceInputKind.Invoke or SurfaceInputKind.SetFocus or SurfaceInputKind.SetValue or SurfaceInputKind.Expand or SurfaceInputKind.Collapse or SurfaceInputKind.Toggle:
                    Source(input.Popup)?.Automation.Act(input);
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

        // Escape while a popup that takes input is open: the newest closes, as the client hides it, and the key goes no further.
        private bool Escaped()
        {
            if (popups.Values.Where(view => view.Placement is { Input: true } && view.Popup is { IsOpen: true }).MaxBy(view => view.Id) is not { Popup: { } newest }) return false;
            newest.Close();
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
            main.Report();
            foreach (var popup in popups.Values.ToArray())
                if (popup.Root.PlatformImpl is not null)
                {
                    popup.Frames.Tick(scale, default, null);
                    popup.Frames.Report();
                }
        }

        // The popups open on the surface now (a submenu's under its menu's), numbered as they appear; each one's place is
        // sent before its first frame and again when it moves or resizes, and its closing after its last frame.
        private void Track()
        {
            var open = new List<(Popup? Popup, TopLevel Root)>();
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
            foreach (var dialog in dialogs)
                if (dialog.PlatformImpl is not null && dialog.IsVisible) open.Add((null, dialog));
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
                    Remote(host)?.SetRenderScaling(scale);
                    view = new PopupView(++lastPopup, popup, host as Window, host, new FrameSource(this, host, lastPopup));
                    popups[host] = view;
                }
                var at = host.PointToScreen(default);
                // A dialog's place is the client's to choose; its title is plain text, as a client takes it.
                var placement = view.Dialog is { } dialog
                    ? new SurfacePopup(Id, view.Id, 0, 0, Math.Max(1, host.ClientSize.Width), Math.Max(1, host.ClientSize.Height), true, true, true, Title(dialog.Title))
                    : new SurfacePopup(Id, view.Id, (at.X - origin.X) / scale, (at.Y - origin.Y) / scale, Math.Max(1, host.ClientSize.Width), Math.Max(1, host.ClientSize.Height),
                        true, popup!.IsHitTestVisible && popup.Child is not ToolTip);
                if (placement == view.Placement) continue;
                view.Placement = placement;
                Place(placement);
            }
        }

        private static string Title(string? title)
        {
            var plain = new string((title ?? "").Where(character => !char.IsControl(character)).ToArray());
            return plain.Length <= SurfaceLimits.MostTitle ? plain : plain[..SurfaceLimits.MostTitle];
        }

        private void Place(SurfacePopup placement) =>
            renderer.peer.OneWay(SurfaceProtocol.Popup, SurfaceProtocol.Encode(placement), Context);

        private FrameSource? Source(int popup) => popup == 0 ? main : popups.Values.FirstOrDefault(view => view.Id == popup)?.Frames;

        private void Send(SurfaceFrameHeader header, byte[] bytes) => renderer.peer.OneWay(SurfaceProtocol.Frame, SurfaceProtocol.Encode(header), Context, bytes);

        // The remote platform's top-level behind an open top-level; null once it closed.
        private static RemoteTopLevel? Remote(TopLevel? top) => top?.PlatformImpl as RemoteTopLevel;

        public SurfaceCapture Capture(List<string> names, int popup)
        {
            TopLevel target = popup == 0 ? window : popups.Values.FirstOrDefault(view => view.Id == popup)?.Root ?? throw new IsoCallException(IsoError.InvalidParams, $"Popup {popup} is not open.");
            var pixels = Array.Empty<byte>();
            if (Remote(target)?.Rendered(ref pixels, out var size) != true) throw new IsoCallException(IsoError.Failed, "The surface has not rendered.");
            var (w, h) = (size.Width, size.Height);
            var bounds = new Dictionary<string, double[]>();
            var texts = new Dictionary<string, string>();
            foreach (var control in target.GetVisualDescendants().OfType<Control>())
            {
                if (control.Name is not { } name || !names.Contains(name) || bounds.ContainsKey(name) || control.TranslatePoint(default, target) is not { } at) continue;
                bounds[name] = [at.X, at.Y, control.Bounds.Width, control.Bounds.Height];
                if (control is not TextBox field) continue;
                texts[name] = field.Text ?? "";
                texts[name + ".preedit"] = field.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.PreeditText ?? "";
            }
            return new SurfaceCapture(w, h, Convert.ToBase64String(pixels), bounds, texts, Source(popup)?.Automation.Lines());
        }

        public void Close()
        {
            closed = true;
            foreach (var dialog in dialogs.ToArray()) dialog.Close();
            window.Close();
        }

        /// <summary>
        /// An open popup or dialog: its number, the plugin's popup (or its window, for a dialog) and its top-level, its
        /// frames and the place last sent.
        /// </summary>
        private sealed class PopupView(int id, Popup? popup, Window? dialog, TopLevel root, FrameSource frames)
        {
            public int Id { get; } = id;
            public Popup? Popup { get; } = popup;
            public Window? Dialog { get; } = dialog;
            public TopLevel Root { get; } = root;
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
            private SurfaceCursor? cursor;
            private SurfaceIme? ime;

            /// <summary>This top-level's accessibility tree.</summary>
            public SurfaceAutomationFeed Automation { get; } = new(top);

            /// <summary>Sends the cursor over this top-level, its text field's caret and its accessibility tree when they changed.</summary>
            public void Report()
            {
                if (Remote(top) is not { } remote) return;
                var shown = new SurfaceCursor(view.Id, popup, (int)remote.Cursor);
                if (shown != cursor)
                {
                    cursor = shown;
                    view.renderer.peer.OneWay(SurfaceProtocol.Cursor, SurfaceProtocol.Encode(shown), view.Context);
                }
                var caret = remote.Caret;
                var now = new SurfaceIme(view.Id, popup, remote.Composing, caret.X, caret.Y, caret.Width, caret.Height);
                if (now != ime)
                {
                    ime = now;
                    view.renderer.peer.OneWay(SurfaceProtocol.Ime, SurfaceProtocol.Encode(now), view.Context);
                }
                if (Automation.Changes(view.Id, popup) is { } tree) view.renderer.peer.OneWay(SurfaceProtocol.Automation, SurfaceProtocol.Encode(tree), view.Context);
            }

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
                if (Remote(top)?.Rendered(ref next, out var size) != true) return;
                var resized = false;
                if (size.Width != width || size.Height != height)
                {
                    (width, height) = (size.Width, size.Height);
                    held = new byte[width * height * 4];
                    moves?.Clear();
                    resized = true;
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