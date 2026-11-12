using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;

using AndroidX.Core.View;

using Avalonia;
using Avalonia.Android;
using Avalonia.Android.Platform.SkiaPlatform;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

using AndroidColor = Android.Graphics.Color;
using AndroidDialog = Android.App.Dialog;
using FrameLayout = Android.Widget.FrameLayout;

namespace SharpRail.UI.Android.Windowing;

/// <summary>
/// One Avalonia window on Android: an Avalonia view of its own, whose surface, input and text input the window
/// takes over as its platform implementation. Shown without an owner it fills the activity; shown over another
/// window it is an Android dialog of the size the window asks for.
/// </summary>
internal sealed class AndroidWindow : IWindowImpl
{
    private readonly AndroidWindows windows;
    private readonly AvaloniaActivity activity;
    private readonly AvaloniaView view;
    private readonly TopLevelImpl surface;
    private IInputRoot? inputRoot;
    // The input root learns its top level only after it is handed to the implementation.
    private TopLevel? topLevel => inputRoot?.FocusRoot as TopLevel;
    private AndroidWindow? parent;
    private WindowDialog? dialog;
    private InsetFrame? frame;
    private Size maximum = Size.Infinity;
    private Screens? screens;
    private Size requested;
    private bool shown, hasSurface, rendering, disposed;

    public AndroidWindow(AndroidWindows windows, AvaloniaActivity activity)
    {
        this.windows = windows; this.activity = activity;
        // Only the window filling the activity may own the activity's insets: a view created in the bare activity
        // installs itself as their listener, which a dialog's view must not take from the window beneath it.
        view = new AvaloniaView(windows.Root is null ? activity : new ContextThemeWrapper(activity, activity.Theme));
        surface = view.TopLevelImpl;
        // The view makes a root of its own for embedded content. The window replaces it as the surface's top level.
        var embedded = view._root;
        view._root = null;
        if (embedded is not null) TopLevelAccess.Close(embedded);
        // Back reaches windows through the activity (AndroidWindows.Back). The view's own listener would answer
        // the same press a second time, after the window already acted on it, and report it unhandled.
        (surface._systemNavigationManager as IDisposable)?.Dispose();
        surface.Paint = null; surface.Resized = null; surface.ScalingChanged = null; surface.LostFocus = null;
        surface.TransparencyLevelChanged = null; surface.Closed = null;
        surface.InternalView!.SurfaceWindowCreated += (_, _) => { hasSurface = true; Render(); Dispatcher.UIThread.Post(Fit); };
        surface.InternalView.SurfaceWindowDestroyed += (_, _) => { hasSurface = false; Render(); };
    }

    /// <summary>Whether this window fills its Android window rather than taking the size it asks for.</summary>
    private bool Fills => dialog is null || !sheet;
    // A window sized to its content is a sheet at the bottom of the screen; any other is a page over all of it.
    private bool sheet;

    public double DesktopScaling => surface.DesktopScaling;
    public IPlatformHandle? Handle => surface.Handle;
    public double RenderScaling => surface.RenderScaling;
    public IPlatformRenderSurface[] Surfaces => surface.Surfaces;
    public Compositor Compositor => surface.Compositor;
    public WindowTransparencyLevel TransparencyLevel { get; private set; } = WindowTransparencyLevel.None;
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

    // Until its surface exists a window lays out at the size it will get, not at the empty surface's.
    public Size ClientSize => hasSurface ? surface.ClientSize : Fills ? Screen : requested;
    public Size? FrameSize => null;
    public Size MaxAutoSizeHint => Screen;
    public PixelPoint Position => default;
    public WindowState WindowState { get => WindowState.Normal; set { } }
    public bool WindowStateGetterIsUsable => true;
    public bool IsClientAreaExtendedToDecorations => true;
    public bool NeedsManagedDecorations => false;
    public PlatformRequestedDrawnDecoration RequestedDrawnDecorations => default;
    public Thickness ExtendedMargins => default;
    public Thickness OffScreenMargin => default;

    private Size Screen
    {
        get
        {
            var metrics = activity.Resources!.DisplayMetrics!;
            return new Size(metrics.WidthPixels / (double)metrics.Density, metrics.HeightPixels / (double)metrics.Density);
        }
    }

    public void SetInputRoot(IInputRoot inputRoot)
    {
        this.inputRoot = inputRoot;
        surface.SetInputRoot(inputRoot);
    }

    public Point PointToClient(PixelPoint point) => surface.PointToClient(point);
    public PixelPoint PointToScreen(Point point) => surface.PointToScreen(point);
    public void SetCursor(ICursorImpl? cursor) => surface.SetCursor(cursor);
    public IPopupImpl? CreatePopup() => null;
    public void SetFrameThemeVariant(PlatformThemeVariant? themeVariant) => surface.SetFrameThemeVariant(themeVariant);

    // A dialog's Android window is transparent, so the card a transparent window draws floats over the window beneath it.
    public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevels)
    {
        var level = transparencyLevels.Contains(WindowTransparencyLevel.Transparent) ? WindowTransparencyLevel.Transparent : WindowTransparencyLevel.None;
        if (level == TransparencyLevel) return;
        TransparencyLevel = level;
        TransparencyLevelChanged?.Invoke(level);
    }

    public object? TryGetFeature(Type featureType)
    {
        if (featureType == typeof(ISystemNavigationManagerImpl)) return null;
        if (featureType == typeof(IScreenImpl)) return screens ??= surface.TryGetFeature(featureType) is IScreenImpl found ? new Screens(found) : null;
        if (surface.TryGetFeature(featureType) is { } feature) return feature;
        // A dialog's view is not created in the activity, so what needs one comes from the window filling it.
        return featureType == typeof(IStorageProvider) || featureType == typeof(ILauncher) || featureType == typeof(IClipboard)
            ? windows.Root is { } root && root != this ? root.TryGetFeature(featureType) : null
            : null;
    }

    public void Show(bool activate, bool isDialog)
    {
        if (disposed) return;
        sheet = topLevel is Avalonia.Controls.Window { SizeToContent: SizeToContent.Height or SizeToContent.WidthAndHeight };
        shown = true;
        // The screen decides a window's size here; a minimum the screen cannot give would draw past its surface.
        if (topLevel is Avalonia.Controls.Window minimum) minimum.MinWidth = minimum.MinHeight = 0;
        if (parent is null && windows.Root is null or { shown: false })
        {
            // The view fills the activity, replacing whichever window did before, inside the system bars and keyboard.
            if (topLevel?.Content is Control content) content.SetValue(TopLevel.AutoSafeAreaPaddingProperty, false);
            frame = new InsetFrame(activity, this);
            frame.AddView(view);
            activity.SetContentView(frame);
            view.RequestFocus();
            view.ViewTreeObserver?.AddOnGlobalLayoutListener(new LayoutListener(this));
            windows.Shown(this, root: true);
        }
        else
        {
            dialog = new WindowDialog(activity, this);
            dialog.SetContentView(view);
            dialog.SetCanceledOnTouchOutside(sheet);
            if (sheet) dialog.Window?.SetWindowAnimations(global::Android.Resource.Style.AnimationInputMethod);
            Place();
            dialog.Show();
            windows.Shown(this, root: false);
        }
        Render();
    }

    public void Hide()
    {
        if (!shown && !disposed) return;
        shown = false;
        Render();
        var (hiddenDialog, hiddenFrame) = (dialog, frame);
        dialog = null; frame = null;
        // A window usually hides or closes inside one of its own touch events, which its view is still dispatching.
        Dispatcher.UIThread.Post(() =>
        {
            hiddenDialog?.Dismiss();
            if (shown) return;
            (view.Parent as ViewGroup)?.RemoveView(view);
            (hiddenFrame?.Parent as ViewGroup)?.RemoveView(hiddenFrame);
            if (!disposed) return;
            view.Dispose();
            surface.Dispose();
        }, DispatcherPriority.Background);
    }

    public void Activate() => Activated?.Invoke();

    public void Resize(Size clientSize, WindowResizeReason reason = WindowResizeReason.Application)
    {
        requested = clientSize;
        if (Fills) Dispatcher.UIThread.Post(Fit);
        else Place();
    }

    public void SetParent(IWindowImpl? parent) => this.parent = parent as AndroidWindow;

    public void SetTitle(string? title) { }
    public void SetEnabled(bool enable) { }
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
    public void SetMinMaxSize(Size minSize, Size maxSize)
    {
        maximum = maxSize;
        Place();
    }
    public void SetExtendClientAreaToDecorationsHint(bool extendIntoClientAreaHint) { }
    public void SetExtendClientAreaTitleBarHeightHint(double titleBarHeight) { }

    /// <summary>The system Back gesture: closes the window unless it cancels closing. False when nothing handled it.</summary>
    public bool Back()
    {
        if (disposed || topLevel is null) return false;
        var request = new Avalonia.Interactivity.RoutedEventArgs(TopLevel.BackRequestedEvent);
        topLevel.RaiseEvent(request);
        if (request.Handled) return true;
        if (dialog is null) return false;
        RequestClose();
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Hide();
        windows.Removed(this);
        Closed?.Invoke();
    }

    // Rendering needs a surface: a window draws only while it is shown and Android has given its view one.
    private void Render()
    {
        if (topLevel is null) return;
        var render = shown && hasSurface && !disposed;
        // The window starts rendering by itself as it shows, before Android has made its surface.
        if (render == rendering && render) return;
        rendering = render;
        if (render) TopLevelAccess.StartRendering(topLevel);
        else TopLevelAccess.StopRendering(topLevel);
    }

    /// <summary>Tells the window the size Android gave it.</summary>
    private void Fit()
    {
        if (disposed || !shown || !hasSurface) return;
        Resized?.Invoke(surface.ClientSize, WindowResizeReason.Layout);
    }

    /// <summary>
    /// Lays a window shown over another in the part of the screen the system bars and keyboard leave visible:
    /// a page over all of it, a sheet across its bottom at the height the window asks for.
    /// </summary>
    internal void Place()
    {
        if (dialog?.Window is not { } window) return;
        window.SetBackgroundDrawable(new ColorDrawable(AndroidColor.Transparent));
        window.ClearFlags(WindowManagerFlags.DimBehind);
        var metrics = activity.Resources!.DisplayMetrics!;
        var (left, top, right, bottom) = windows.Insets;
        var width = metrics.WidthPixels - left - right;
        var height = metrics.HeightPixels - top - bottom;
        var attributes = window.Attributes!;
        attributes.Gravity = GravityFlags.Top | GravityFlags.Left;
        attributes.X = left;
        attributes.Y = top;
        if (sheet)
        {
            // On a tablet a sheet keeps a readable width instead of spanning the screen.
            var sheetWidth = Math.Min(width, (int)(SheetWidth * metrics.Density));
            var sheetHeight = Math.Min(height, Math.Max(1, (int)Math.Ceiling(requested.Height * metrics.Density)));
            attributes.X += (width - sheetWidth) / 2;
            attributes.Y += height - sheetHeight;
            (width, height) = (sheetWidth, sheetHeight);
        }
        else
        {
            // A page shorter than the screen leaves the window beneath it showing below, and reachable: a
            // workbench keeps its page bar under Settings that way.
            if (double.IsFinite(maximum.Height)) height = Math.Min(height, (int)Math.Ceiling(maximum.Height * metrics.Density));
            window.AddFlags(WindowManagerFlags.NotTouchModal);
        }
        attributes.Width = width;
        attributes.Height = height;
        window.Attributes = attributes;
    }

    private const int SheetWidth = 640;

    /// <summary>Asks the window to close, as its own close button would; it may refuse.</summary>
    private void RequestClose()
    {
        if (!disposed) Closing?.Invoke(WindowCloseReason.WindowClosing);
    }

    // A top level's rendering and teardown are reserved to its subclasses, which a platform implementation is not.
    private static class TopLevelAccess
    {
        public static readonly Action<TopLevel> StartRendering = Method(nameof(StartRendering));
        public static readonly Action<TopLevel> StopRendering = Method(nameof(StopRendering));
        public static readonly Action<TopLevel> Close = Method("EnsureClosed");

        [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(TopLevel))]
        private static Action<TopLevel> Method(string name) =>
            typeof(TopLevel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<TopLevel>>();
    }

    private sealed class WindowDialog(Context context, AndroidWindow window) : AndroidDialog(context, global::Android.Resource.Style.ThemeTranslucentNoTitleBar)
    {
#pragma warning disable CA1422 // The predictive Back callback is not enabled, so the platform still calls this.
        public override void OnBackPressed() => window.Back();
#pragma warning restore CA1422

        // A tap outside a sheet closes its window; Android must not dismiss the dialog under it.
        public override void Cancel() => window.RequestClose();
    }

    /// <summary>
    /// Holds the view of the window filling the activity clear of the status bar, navigation bar, display cutout
    /// and soft keyboard, so the window lays out in the space that stays visible.
    /// </summary>
    private sealed class InsetFrame : FrameLayout, IOnApplyWindowInsetsListener
    {
        private readonly AndroidWindow window;

        public InsetFrame(Context context, AndroidWindow window) : base(context)
        {
            this.window = window;
            ViewCompat.SetOnApplyWindowInsetsListener(this, this);
            Paint();
        }

        // A frame that replaces another in a laid-out activity is not sent the insets by itself.
        protected override void OnAttachedToWindow()
        {
            base.OnAttachedToWindow();
            ViewCompat.RequestApplyInsets(this);
        }

        public WindowInsetsCompat OnApplyWindowInsets(View? view, WindowInsetsCompat? insets)
        {
            var bars = insets!.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout() | WindowInsetsCompat.Type.Ime())!;
            SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            window.windows.Inset(bars.Left, bars.Top, bars.Right, bars.Bottom);
            Paint();
            return WindowInsetsCompat.Consumed!;
        }

        // The bars sit over the window's own background colour.
        private void Paint() =>
            SetBackgroundColor(window.topLevel?.Background is Avalonia.Media.ISolidColorBrush { Color: var color }
                ? new AndroidColor(color.R, color.G, color.B) : AndroidColor.Black);
    }

    // The backend finds a top level's screen through its own implementation type, which a window wraps.
    private sealed class Screens(IScreenImpl screens) : IScreenImpl
    {
        public int ScreenCount => screens.ScreenCount;
        public IReadOnlyList<Screen> AllScreens => screens.AllScreens;
        public Action? Changed { get => screens.Changed; set => screens.Changed = value; }
        public Screen? ScreenFromWindow(IWindowBaseImpl window) => ScreenFromTopLevel(window);
        public Screen? ScreenFromTopLevel(ITopLevelImpl topLevel) => screens.ScreenFromTopLevel(topLevel is AndroidWindow window ? window.surface : topLevel);
        public Screen? ScreenFromPoint(PixelPoint point) => screens.ScreenFromPoint(point);
        public Screen? ScreenFromRect(PixelRect rect) => screens.ScreenFromRect(rect);
        public Task<bool> RequestScreenDetails() => screens.RequestScreenDetails();
    }

    private sealed class LayoutListener(AndroidWindow window) : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener
    {
        public void OnGlobalLayout() => window.Fit();
    }
}