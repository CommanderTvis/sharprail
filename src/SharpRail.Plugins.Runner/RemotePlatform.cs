using System.Diagnostics;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// SharpRail's remote platform backend (Security.SPEC.md, UI halves): every top-level the plugin's process opens (a
/// surface, a popup, a window) is a <see cref="RemoteTopLevel"/> that Skia's CPU raster draws into memory, which the
/// renderer diffs and sends. Rendering runs on the UI thread, ticked on demand; nothing touches a GPU, a screen or a
/// window system.
/// </summary>
internal static class RemotePlatform
{
    private static RemoteRenderTimer? timer;

    internal static Compositor Compositor { get; private set; } = null!;

    public static AppBuilder UseRemote(this AppBuilder builder) =>
        builder.UseStandardRuntimePlatformSubsystem().UseWindowingSubsystem(Initialize, "SharpRailRemote").UseHarfBuzz();

    private static void Initialize()
    {
        timer = new RemoteRenderTimer(60);
        AvaloniaLocator.CurrentMutable
            .Bind<ICursorFactory>().ToConstant(new RemoteCursorFactory())
            .Bind<IPlatformSettings>().ToSingleton<DefaultPlatformSettings>()
            .Bind<IPlatformIconLoader>().ToConstant(new RemoteIconLoader())
            .Bind<IKeyboardDevice>().ToConstant(new KeyboardDevice())
            .Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(timer))
            .Bind<IWindowingPlatform>().ToConstant(new RemoteWindowingPlatform())
            .Bind<PlatformHotkeyConfiguration>().ToSingleton<PlatformHotkeyConfiguration>()
            .Bind<KeyGestureFormatInfo>().ToConstant(new KeyGestureFormatInfo(new Dictionary<Key, string>()));
        Compositor = new Compositor(null);
    }

    /// <summary>Runs the dispatcher's jobs and renders until nothing is pending (at most ten rounds).</summary>
    public static void Settle()
    {
        var dispatcher = Dispatcher.UIThread;
        for (var round = 0; round < 10; round++)
        {
            dispatcher.RunJobs();
            timer?.ForceTick();
            if (!dispatcher.HasJobsWithPriority(DispatcherPriority.SystemIdle)) return;
        }
        dispatcher.RunJobs();
    }

    private sealed class RemoteWindowingPlatform : IWindowingPlatform
    {
        public IWindowImpl CreateWindow() => new RemoteTopLevel(null);
        public IWindowImpl CreateEmbeddableWindow() => throw new PlatformNotSupportedException();
        public ITopLevelImpl CreateEmbeddableTopLevel() => throw new PlatformNotSupportedException();
        public ITrayIconImpl? CreateTrayIcon() => null;

        public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder)
        {
            for (var index = 0; index < windows.Length; index++) zOrder[index] = (windows[index] as RemoteTopLevel)?.ZOrder ?? 0;
        }
    }

    /// <summary>Renders on the UI thread at the frame rate, and once more whenever <see cref="ForceTick"/> asks.</summary>
    private sealed class RemoteRenderTimer : DefaultRenderTimer
    {
        private readonly TimeSpan interval;
        private Action? force;

        public RemoteRenderTimer(int framesPerSecond) : base(framesPerSecond) => interval = TimeSpan.FromSeconds(1.0 / framesPerSecond);

        protected override IDisposable StartCore(Action<TimeSpan> tick)
        {
            var clock = Stopwatch.StartNew();
            force = () => tick(clock.Elapsed);
            var dispatched = new DispatcherTimer(DispatcherPriority.UiThreadRender) { Interval = interval };
            dispatched.Tick += (_, _) => tick(clock.Elapsed);
            dispatched.Start();
            return new Stop(() =>
            {
                force = null;
                dispatched.Stop();
            });
        }

        public override bool RunsInBackground => false;

        public void ForceTick() => force?.Invoke();

        private sealed class Stop(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }

    private sealed class RemoteIconLoader : IPlatformIconLoader
    {
        public IWindowIconImpl LoadIcon(string fileName) => new Icon();
        public IWindowIconImpl LoadIcon(Stream stream) => new Icon();
        public IWindowIconImpl LoadIcon(IBitmapImpl bitmap) => new Icon();

        private sealed class Icon : IWindowIconImpl
        {
            public void Save(Stream outputStream) { }
        }
    }
}

/// <summary>A standard cursor shape; the client shows the same shape over the plugin's pixels.</summary>
internal sealed class RemoteCursor(StandardCursorType shape) : ICursorImpl
{
    public StandardCursorType Shape { get; } = shape;
    public void Dispose() { }
}

/// <summary>Cursors the plugin asks for; a bitmap cursor of its own shows as the arrow.</summary>
internal sealed class RemoteCursorFactory : ICursorFactory
{
    public ICursorImpl GetCursor(StandardCursorType cursorType) => new RemoteCursor(cursorType);
    public ICursorImpl CreateCursor(Avalonia.Media.Imaging.Bitmap cursor, PixelPoint hotSpot) => new RemoteCursor(StandardCursorType.Arrow);
}

/// <summary>
/// One top-level of the plugin's process: a surface's window, a popup, or a window the plugin opens. Skia's CPU raster
/// draws it into a buffer of its own at its scale; <see cref="Rendered"/> copies the last frame drawn. Input arrives
/// through its methods, which run the dispatcher's jobs and render around each event, so what the event changed is
/// drawn before the next one.
/// </summary>
internal sealed class RemoteTopLevel : IWindowImpl, IPopupImpl, IFramebufferPlatformSurface
{
    private static int nextZOrder = 1;

    private readonly IKeyboardDevice keyboard;
    private readonly IScreenImpl screen = new RemoteScreens();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Lock sync = new();
    private readonly IPopupPositioner? positioner;
    private byte[] drawing = [], last = [];
    private PixelSize lastSize;

    public RemoteTopLevel(RemoteTopLevel? parent)
    {
        Surfaces = [this];
        keyboard = AvaloniaLocator.Current.GetRequiredService<IKeyboardDevice>();
        // Each top-level has its own mouse, so one client's pointer capture never reaches another's surfaces.
        MouseDevice = new MouseDevice(new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true));
        ClientSize = new Size(1024, 768);
        if (parent is null) return;
        Parent = parent;
        RenderScaling = parent.RenderScaling;
        positioner = new ManagedPopupPositioner(new ManagedPopupPositionerPopupImplHelper(parent, MoveResize));
    }

    /// <summary>The top-level a popup belongs to; null for a window.</summary>
    public RemoteTopLevel? Parent { get; }

    /// <summary>The cursor the plugin shows over this top-level now.</summary>
    public StandardCursorType Cursor { get; private set; } = StandardCursorType.Arrow;

    /// <summary>Raised on the UI thread when <see cref="Cursor"/> changes.</summary>
    public event Action<RemoteTopLevel>? CursorChanged;

    public int ZOrder { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public Size ClientSize { get; private set; }
    public Size? FrameSize => null;
    public double RenderScaling { get; private set; } = 1;
    public double DesktopScaling => RenderScaling;
    public IPlatformRenderSurface[] Surfaces { get; }
    public Action<RawInputEventArgs>? Input { get; set; }
    public Action<Rect>? Paint { get; set; }
    public Action<Size, WindowResizeReason>? Resized { get; set; }
    public Action<double>? ScalingChanged { get; set; }
    public Compositor Compositor => RemotePlatform.Compositor;
    public IInputRoot? InputRoot { get; private set; }
    public IMouseDevice MouseDevice { get; }
    public Action? Closed { get; set; }
    public Action? LostFocus { get; set; }
    public PixelPoint Position { get; private set; }
    public Action<PixelPoint>? PositionChanged { get; set; }
    public Action? Deactivated { get; set; }
    public Action? Activated { get; set; }
    public IPlatformHandle Handle { get; } = new PlatformHandle(IntPtr.Zero, "SharpRailRemote");
    public Size MaxAutoSizeHint => new(1920, 1080);
    public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }
    public WindowTransparencyLevel TransparencyLevel { get; private set; } = WindowTransparencyLevel.Transparent;
    public Action? GotInputWhenDisabled { get; set; }
    public bool IsClientAreaExtendedToDecorations => false;
    public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
    public bool NeedsManagedDecorations => false;
    public PlatformRequestedDrawnDecoration RequestedDrawnDecorations => default;
    public Thickness ExtendedMargins => default;
    public Thickness OffScreenMargin => default;
    public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => new(1, 1, 1);
    public WindowState WindowState { get; set; }
    public bool WindowStateGetterIsUsable => false;
    public Action<WindowState>? WindowStateChanged { get; set; }
    public Func<WindowCloseReason, bool>? Closing { get; set; }
    IPopupPositioner IPopupImpl.PopupPositioner => positioner!;

    public void SetInputRoot(IInputRoot inputRoot) => InputRoot = inputRoot;
    public Point PointToClient(PixelPoint point) => (point - Position).ToPoint(RenderScaling);
    public PixelPoint PointToScreen(Point point) => PixelPoint.FromPoint(point, RenderScaling) + Position;

    public void SetCursor(ICursorImpl? cursor)
    {
        var shape = (cursor as RemoteCursor)?.Shape ?? StandardCursorType.Arrow;
        if (shape == Cursor) return;
        Cursor = shape;
        CursorChanged?.Invoke(this);
    }

    public object? TryGetFeature(Type featureType) => featureType == typeof(IScreenImpl) ? screen : null;

    public void Show(bool activate, bool isDialog)
    {
        if (activate) Activate();
    }

    public void Hide() => Dispatcher.UIThread.Post(() => Deactivated?.Invoke(), DispatcherPriority.Input);

    public void Activate()
    {
        ZOrder = nextZOrder++;
        Dispatcher.UIThread.Post(() => Activated?.Invoke(), DispatcherPriority.Input);
    }

    public void Resize(Size clientSize, WindowResizeReason reason)
    {
        if (ClientSize == clientSize) return;
        if (Parent is not null) ApplySize(clientSize, reason);
        else Dispatcher.UIThread.Post(() => ApplySize(clientSize, reason), DispatcherPriority.Send);
    }

    private void ApplySize(Size clientSize, WindowResizeReason reason)
    {
        if (ClientSize == clientSize) return;
        ClientSize = clientSize;
        Resized?.Invoke(clientSize, reason);
    }

    private void MoveResize(PixelPoint position, Size size, double scaling)
    {
        Position = position;
        PositionChanged?.Invoke(position);
        ApplySize(size, WindowResizeReason.Unspecified);
    }

    void IWindowImpl.Move(PixelPoint point)
    {
        Position = point;
        PositionChanged?.Invoke(point);
    }

    /// <summary>Renders at <paramref name="scaling"/> from now on, at the same size in device-independent pixels.</summary>
    public void SetRenderScaling(double scaling)
    {
        if (!(scaling > 0)) throw new ArgumentOutOfRangeException(nameof(scaling), "Scaling must be greater than zero.");
        if (RenderScaling == scaling) return;
        var size = ClientSize;
        RenderScaling = scaling;
        ScalingChanged?.Invoke(scaling);
        Resize(size, WindowResizeReason.DpiChange);
    }

    public IPopupImpl? CreatePopup() => new RemoteTopLevel(this);

    public void Dispose()
    {
        Closed?.Invoke();
        lock (sync) (drawing, last, lastSize) = ([], [], default);
    }

    // Rendering: the compositor draws a whole frame into a buffer of the top-level's pixel size, which becomes the last
    // frame when the drawing ends.

    public IFramebufferRenderTarget CreateFramebufferRenderTarget() => new FuncFramebufferRenderTarget(Lock);

    private ILockedFramebuffer Lock()
    {
        var size = PixelSize.FromSize(ClientSize, RenderScaling);
        var length = Math.Max(1, size.Width) * Math.Max(1, size.Height) * 4;
        byte[] buffer;
        lock (sync)
        {
            if (drawing.Length != length) drawing = GC.AllocateArray<byte>(length, pinned: true);
            buffer = drawing;
        }
        Array.Clear(buffer);
        return new Framebuffer(buffer, size, new Vector(96, 96) * RenderScaling, () =>
        {
            lock (sync)
            {
                if (!ReferenceEquals(buffer, drawing)) return;
                (drawing, last) = (last, drawing);
                lastSize = size;
            }
        });
    }

    /// <summary>
    /// Settles the dispatcher and renders, then copies the last frame drawn (premultiplied BGRA) into
    /// <paramref name="target"/>, reallocated when its size changed; false before the first frame.
    /// </summary>
    public bool Rendered(ref byte[] target, out PixelSize size)
    {
        RemotePlatform.Settle();
        lock (sync)
        {
            size = lastSize;
            if (lastSize.Width <= 0 || lastSize.Height <= 0) return false;
            var length = lastSize.Width * lastSize.Height * 4;
            if (target.Length != length) target = new byte[length];
            last.AsSpan(0, length).CopyTo(target);
            return true;
        }
    }

    private sealed class Framebuffer(byte[] pixels, PixelSize size, Vector dpi, Action done) : ILockedFramebuffer
    {
        private bool disposed;

        public IntPtr Address { get; } = Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0);
        public PixelSize Size => size;
        public int RowBytes => size.Width * 4;
        public Vector Dpi => dpi;
        public PixelFormat Format => PixelFormat.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Premul;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            done();
        }
    }

    // Input: each event runs the dispatcher's pending jobs and renders before and after it.

    private ulong Timestamp => (ulong)clock.ElapsedMilliseconds;

    private void Raise(RawInputEventArgs args)
    {
        RemotePlatform.Settle();
        if (InputRoot is null) return;
        if (!IsEnabled)
        {
            GotInputWhenDisabled?.Invoke();
            return;
        }
        Input?.Invoke(args);
        RemotePlatform.Settle();
    }

    public void PointerMove(Point point, RawInputModifiers modifiers) => Raise(new RawPointerEventArgs(MouseDevice, Timestamp, InputRoot!, RawPointerEventType.Move, point, modifiers));

    public void PointerDown(Point point, MouseButton button, RawInputModifiers modifiers) => Raise(new RawPointerEventArgs(MouseDevice, Timestamp, InputRoot!, button switch
    {
        MouseButton.Left => RawPointerEventType.LeftButtonDown,
        MouseButton.Right => RawPointerEventType.RightButtonDown,
        MouseButton.Middle => RawPointerEventType.MiddleButtonDown,
        MouseButton.XButton1 => RawPointerEventType.XButton1Down,
        MouseButton.XButton2 => RawPointerEventType.XButton2Down,
        _ => RawPointerEventType.Move,
    }, point, modifiers));

    public void PointerUp(Point point, MouseButton button, RawInputModifiers modifiers) => Raise(new RawPointerEventArgs(MouseDevice, Timestamp, InputRoot!, button switch
    {
        MouseButton.Left => RawPointerEventType.LeftButtonUp,
        MouseButton.Right => RawPointerEventType.RightButtonUp,
        MouseButton.Middle => RawPointerEventType.MiddleButtonUp,
        MouseButton.XButton1 => RawPointerEventType.XButton1Up,
        MouseButton.XButton2 => RawPointerEventType.XButton2Up,
        _ => RawPointerEventType.Move,
    }, point, modifiers));

    public void Wheel(Point point, Vector delta, RawInputModifiers modifiers) => Raise(new RawMouseWheelEventArgs(MouseDevice, Timestamp, InputRoot!, point, delta, modifiers));

    public void Key(bool down, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? symbol) =>
        Raise(new RawKeyEventArgs(keyboard, Timestamp, InputRoot!, down ? RawKeyEventType.KeyDown : RawKeyEventType.KeyUp, key, modifiers, physicalKey, symbol));

    public void Text(string text) => Raise(new RawTextInputEventArgs(keyboard, Timestamp, InputRoot!, text));

    // What a desktop window would do and a remote top-level has no use for.

    public void SetMinMaxSize(Size minSize, Size maxSize) { }
    public void SetTopmost(bool value) { }
    public void SetTitle(string? title) => Title = title;
    public string? Title { get; private set; }
    public void SetSystemDecorations(bool enabled) { }
    public void SetIcon(IWindowIconImpl? icon) { }
    public void ShowTaskbarIcon(bool value) { }
    public void CanResize(bool value) { }
    public void SetCanMinimize(bool value) { }
    public void SetCanMaximize(bool value) { }
    public void SetWindowManagerAddShadowHint(bool enabled) { }

    public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevel) =>
        TransparencyLevel = transparencyLevel.Contains(WindowTransparencyLevel.Transparent) ? WindowTransparencyLevel.Transparent : WindowTransparencyLevel.None;

    public void SetParent(IWindowImpl? parent) => Owner = parent as RemoteTopLevel;
    public RemoteTopLevel? Owner { get; private set; }
    public void SetEnabled(bool enable) => IsEnabled = enable;
    public void SetWindowDecorations(WindowDecorations enabled) { }
    public void BeginMoveDrag(PointerPressedEventArgs e) { }
    public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e) { }
    public void SetExtendClientAreaToDecorationsHint(bool extendIntoClientAreaHint) { }
    public void SetExtendClientAreaTitleBarHeightHint(double titleBarHeight) { }
    public void SetFrameThemeVariant(PlatformThemeVariant? themeVariant) { }
    public void TakeFocus() { }
    public void SetHitTestVisible(bool isHitTestVisible) { }

    /// <summary>One screen of 1,920×1,280 pixels, which keeps a popup's placement where the headless platform kept it.</summary>
    private sealed class RemoteScreens : ScreensBase<int, PlatformScreen>
    {
        protected override IReadOnlyList<int> GetAllScreenKeys() => [1];
        protected override PlatformScreen CreateScreenFromKey(int key) => new Screen(key);

        private sealed class Screen : PlatformScreen
        {
            public Screen(int key) : base(new PlatformHandle(key, nameof(RemoteScreens)))
            {
                Scaling = 1;
                Bounds = WorkingArea = new PixelRect(0, 0, 1920, 1280);
                IsPrimary = true;
            }
        }
    }
}