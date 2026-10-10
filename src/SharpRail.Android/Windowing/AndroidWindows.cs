using Avalonia;
using Avalonia.Android;
using Avalonia.Platform;

namespace SharpRail.UI.Android.Windowing;

/// <summary>
/// Windows for Avalonia's Android backend, which has none of its own: the workbench, its dialogs and Settings are
/// <see cref="Avalonia.Controls.Window"/>s on every platform. One window at a time fills the activity; windows
/// shown over it are Android dialogs, each with its own Avalonia view.
/// </summary>
internal sealed class AndroidWindows : IWindowingPlatform
{
    private readonly List<AndroidWindow> windows = [];

    public static AndroidWindows Instance { get; } = new();

    /// <summary>The activity windows are created in; windows cannot outlive it.</summary>
    public AvaloniaActivity? Activity { get; private set; }

    /// <summary>The window filling the activity.</summary>
    public AndroidWindow? Root { get; private set; }

    public static void Install() => AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>().ToConstant(Instance);

    public void Attach(AvaloniaActivity activity) => Activity = activity;

    /// <summary>Closes every window of an activity that is being destroyed.</summary>
    public void Detach(AvaloniaActivity activity)
    {
        if (Activity != activity) return;
        foreach (var window in windows.ToArray()) window.Dispose();
        Activity = null;
    }

    public IWindowImpl CreateWindow()
    {
        var window = new AndroidWindow(this, Activity ?? throw new InvalidOperationException("Windows need a running activity."));
        windows.Add(window);
        return window;
    }

    public IWindowImpl CreateEmbeddableWindow() => throw new NotSupportedException();
    public ITopLevelImpl CreateEmbeddableTopLevel() => throw new NotSupportedException();
    public ITrayIconImpl? CreateTrayIcon() => null;

    public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> ordered, Span<long> zOrder)
    {
        for (var index = 0; index < ordered.Length; index++) zOrder[index] = ordered[index] is AndroidWindow window ? windows.IndexOf(window) : -1;
    }

    /// <summary>What the system bars, display cutout and soft keyboard cover at each screen edge, in pixels.</summary>
    public (int Left, int Top, int Right, int Bottom) Insets { get; private set; }

    internal void Inset(int left, int top, int right, int bottom)
    {
        if (Insets == (left, top, right, bottom)) return;
        Insets = (left, top, right, bottom);
        foreach (var window in windows) window.Place();
    }

    /// <summary>The system Back gesture on the activity: false leaves it to the system.</summary>
    public bool Back() => Root?.Back() ?? false;

    internal void Shown(AndroidWindow window, bool root)
    {
        // The newest shown window is the top one.
        windows.Remove(window);
        windows.Add(window);
        if (root) Root = window;
        foreach (var other in windows) if (other != window) other.Deactivated?.Invoke();
        window.Activated?.Invoke();
    }

    internal void Removed(AndroidWindow window)
    {
        windows.Remove(window);
        if (Root == window) Root = null;
    }
}