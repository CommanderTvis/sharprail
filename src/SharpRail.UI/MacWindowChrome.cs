using System.Runtime.InteropServices;

using Avalonia.Controls;

namespace SharpRail.UI;

/// <summary>
/// macOS 26 (Tahoe) rounds a window by its style: a titled window with a toolbar gets the large radius system windows
/// show, a bare titled window the small one. The extended client area leaves ours bare, so the window gets an empty
/// compact unified toolbar and the native window keeps owning the corner clipping; no app-drawn radius is involved.
/// </summary>
public static class MacWindowChrome
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const int UnifiedCompact = 4;
    private const int SeparatorNone = 1;

    /// <summary>Whether the OS version rounds toolbar windows more than bare ones (macOS 26 and later).</summary>
    public static bool WantsToolbar(Version os) => os.Major >= 26;

    /// <summary>Gives <paramref name="window"/> an empty compact toolbar on macOS 26 or later; other systems are left alone.</summary>
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsMacOS() || !WantsToolbar(Environment.OSVersion.Version)) return;
        if (window.TryGetPlatformHandle() is not { Handle: not 0 } handle) return;
        try { AttachToolbar(handle.Handle); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
    }

    private static void AttachToolbar(nint nsWindow)
    {
        var pool = PoolPush();
        try
        {
            if (Send(nsWindow, Selector("toolbar")) != 0) return;
            var identifier = SendUtf8(Class("NSString"), Selector("stringWithUTF8String:"), "SharpRailWindow");
            var toolbar = Send(Send(Class("NSToolbar"), Selector("alloc")), Selector("initWithIdentifier:"), identifier);
            SendInt(toolbar, Selector("setShowsBaselineSeparator:"), 0);
            SendInt(nsWindow, Selector("setToolbarStyle:"), UnifiedCompact);
            SendInt(nsWindow, Selector("setTitlebarSeparatorStyle:"), SeparatorNone);
            Send(nsWindow, Selector("setToolbar:"), toolbar);
        }
        finally { PoolPop(pool); }
    }

    [DllImport(Objc, EntryPoint = "objc_getClass")] private static extern nint Class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc, EntryPoint = "sel_registerName")] private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendInt(nint receiver, nint selector, nint value);
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint SendUtf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
    [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPush")] private static extern nint PoolPush();
    [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPop")] private static extern void PoolPop(nint pool);
}