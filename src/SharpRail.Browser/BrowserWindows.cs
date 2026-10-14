using System.Reflection;

using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;

namespace SharpRail.UI.Browser;

/// <summary>Hosts the shared workbench's windows and dialogs in browser surfaces.</summary>
internal sealed class BrowserWindows : IWindowingPlatform
{
    private readonly List<BrowserWindow> windows = [];
    public static BrowserWindows Instance { get; } = new();
    public static void Install()
    {
        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>().ToConstant(Instance);
        AvaloniaLocator.CurrentMutable.Bind<PlatformHotkeyConfiguration>().ToConstant(BrowserInterop.IsMac()
            ? new PlatformHotkeyConfiguration(KeyModifiers.Meta, wholeWordTextActionModifiers: KeyModifiers.Alt)
            : new PlatformHotkeyConfiguration(KeyModifiers.Control));
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(window) is BrowserWindow browser)
                browser.SetWindow(window);
        });
    }
    internal void HandleEvent(string message)
    {
        if (message.StartsWith("notification:", StringComparison.Ordinal)) { BrowserNotifier.Instance.OnActivated(message[13..]); return; }
        if (message == "blur") { foreach (var window in windows) window.Deactivated?.Invoke(); return; }
        if (message.StartsWith("focus:", StringComparison.Ordinal) && windows.FirstOrDefault(window => window.Id == message[6..]) is { } active) Activate(active);
    }
    public IWindowImpl CreateWindow()
    {
        var window = new BrowserWindow(this);
        windows.Add(window);
        return window;
    }
    public IWindowImpl CreateEmbeddableWindow() => CreateWindow();
    public ITopLevelImpl CreateEmbeddableTopLevel() => throw new NotSupportedException();
    public ITrayIconImpl? CreateTrayIcon() => null;
    public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> ordered, Span<long> zOrder)
    {
        for (var i = 0; i < ordered.Length; i++) zOrder[i] = ordered[i] is BrowserWindow window ? windows.IndexOf(window) : -1;
    }
    internal void Activate(BrowserWindow window)
    {
        foreach (var other in windows) if (other != window) other.Deactivated?.Invoke();
        windows.Remove(window); windows.Add(window);
        window.Activated?.Invoke();
    }
    internal Action Dim(Window owner, Avalonia.Media.Color color)
    {
        var window = windows.Single(window => window.Window == owner);
        window.DimCount++;
        void Update() => BrowserInterop.DimNative(window.Id, color.R, color.G, color.B,
            1 - Math.Pow(1 - color.A / 255d, window.DimCount));
        Update();
        var removed = false;
        return () => { if (removed) return; removed = true; window.DimCount--; Update(); };
    }
    internal void Remove(BrowserWindow window) => windows.Remove(window);
}

internal sealed class BrowserWindow : IWindowImpl
{
    private readonly BrowserWindows windows;
    private readonly string id;
    private readonly AvaloniaView view;
    private readonly ITopLevelImpl surface;
    private bool shown, dialog, disposed;
    private Size requested = new(1200, 800);

    public BrowserWindow(BrowserWindows windows)
    {
        this.windows = windows;
        id = BrowserInterop.CreateWindow();
        view = new AvaloniaView(id);
        var root = (TopLevel)typeof(AvaloniaView).GetProperty("TopLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var renderer = typeof(TopLevel).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
        renderer.GetType().GetMethod("Stop")!.Invoke(renderer, null);
        surface = (ITopLevelImpl)typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(root)!;
        surface.Paint = null; surface.Resized = null; surface.ScalingChanged = null; surface.LostFocus = null;
        surface.TransparencyLevelChanged = null; surface.Closed = null;
    }
    internal int DimCount { get; set; }
    internal string Id => id;
    internal Window? Window { get; private set; }
    internal void SetWindow(Window window)
    {
        Window = window;
        BrowserInterop.SetWorkbench(id, window is WorkbenchWindow);
        BrowserInterop.PlaceWindow(id, requested.Width, requested.Height, dialog, shown);
    }
    public double DesktopScaling => surface.DesktopScaling;
    public IPlatformHandle? Handle => surface.Handle;
    public double RenderScaling => surface.RenderScaling;
    public IPlatformRenderSurface[] Surfaces => surface.Surfaces;
    public Compositor Compositor => surface.Compositor;
    public WindowTransparencyLevel TransparencyLevel => WindowTransparencyLevel.Transparent;
    public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => surface.AcrylicCompensationLevels;
    public Action<RawInputEventArgs>? Input { get => surface.Input; set => surface.Input = value; }
    public Action<Rect>? Paint { get => surface.Paint; set => surface.Paint = value; }
    public Action<Size, WindowResizeReason>? Resized { get => surface.Resized; set => surface.Resized = value; }
    public Action<double>? ScalingChanged { get => surface.ScalingChanged; set => surface.ScalingChanged = value; }
    public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }
    public Action? LostFocus { get => surface.LostFocus; set => surface.LostFocus = value; }
    public Action? Closed { get; set; }
    public Action? Activated { get; set; }
    public Action? Deactivated { get; set; }
    public Action<PixelPoint>? PositionChanged { get; set; }
    public Action<WindowState>? WindowStateChanged { get; set; }
    public Action? GotInputWhenDisabled { get; set; }
    public Func<WindowCloseReason, bool>? Closing { get; set; }
    public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
    public Size ClientSize => surface.ClientSize;
    public Size? FrameSize => null;
    public Size MaxAutoSizeHint => new(1920, 1080);
    public PixelPoint Position => default;
    public WindowState WindowState { get => WindowState.Normal; set { } }
    public bool WindowStateGetterIsUsable => true;
    public bool IsClientAreaExtendedToDecorations => true;
    public bool NeedsManagedDecorations => false;
    public PlatformRequestedDrawnDecoration RequestedDrawnDecorations => default;
    public Thickness ExtendedMargins => default;
    public Thickness OffScreenMargin => default;
    public void SetInputRoot(IInputRoot inputRoot) => surface.SetInputRoot(inputRoot);
    public Point PointToClient(PixelPoint point) => surface.PointToClient(point);
    public PixelPoint PointToScreen(Point point) => surface.PointToScreen(point);
    public void SetCursor(ICursorImpl? cursor) => surface.SetCursor(cursor);
    public IPopupImpl? CreatePopup() => null;
    public void SetFrameThemeVariant(PlatformThemeVariant? themeVariant) => surface.SetFrameThemeVariant(themeVariant);
    public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> levels) { }
    public object? TryGetFeature(Type type) => surface.TryGetFeature(type);
    public void Show(bool activate, bool isDialog)
    {
        dialog |= isDialog; shown = true;
        BrowserInterop.PlaceWindow(id, requested.Width, requested.Height, dialog, true);
        if (activate) Activate();
    }
    public void Hide() { shown = false; BrowserInterop.PlaceWindow(id, requested.Width, requested.Height, dialog, false); }
    public void Activate() { BrowserInterop.ActivateWindow(id); windows.Activate(this); }
    public void SetParent(IWindowImpl? parent) => dialog = parent is not null;
    public void Resize(Size size, WindowResizeReason reason)
    {
        requested = size;
        BrowserInterop.PlaceWindow(id, size.Width, size.Height, dialog, shown);
    }
    public void SetTitle(string? title) => BrowserInterop.SetTitle(id, title ?? "SharpRail");
    public void SetEnabled(bool enabled) => BrowserInterop.EnableWindow(id, enabled);
    public void SetWindowDecorations(WindowDecorations enabled) { }
    public void SetIcon(IWindowIconImpl? icon) { }
    public void ShowTaskbarIcon(bool value) { }
    public void CanResize(bool value) { }
    public void SetCanMinimize(bool value) { }
    public void SetCanMaximize(bool value) { }
    public void SetTopmost(bool value) { }
    public void BeginMoveDrag(PointerPressedEventArgs e) { }
    public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e) { }
    public void Move(PixelPoint point) { }
    public void SetMinMaxSize(Size minSize, Size maxSize) { }
    public void SetExtendClientAreaToDecorationsHint(bool value) { }
    public void SetExtendClientAreaTitleBarHeightHint(double height) { }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        surface.Dispose(); BrowserInterop.RemoveWindow(id); windows.Remove(this); Closed?.Invoke();
    }
}