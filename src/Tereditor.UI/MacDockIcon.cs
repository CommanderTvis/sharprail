using System.Runtime.InteropServices;

using Avalonia.Platform;

namespace Tereditor.UI;

/// <summary>
/// An app started outside a bundle (<c>run dev</c>, <c>run native</c>) has no bundle icon, so macOS shows a generic one in the
/// Dock; this gives it the app icon at runtime. A bundled app keeps the icon its bundle names.
/// </summary>
public static class MacDockIcon
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private static bool applied;

    /// <summary>Sets the Dock icon once, on macOS only and only when the executable is not inside an app bundle.</summary>
    public static void Apply()
    {
        if (applied || !OperatingSystem.IsMacOS()) return;
        applied = true;
        if (Environment.ProcessPath is not { } path || path.Contains(".app/Contents/MacOS/", StringComparison.Ordinal)) return;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Tereditor.UI/Assets/AppIcon/macos.png"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            SetIcon(memory.ToArray());
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or IOException) { }
    }

    private static void SetIcon(byte[] png)
    {
        var buffer = Marshal.AllocHGlobal(png.Length);
        var pool = PoolPush();
        try
        {
            Marshal.Copy(png, 0, buffer, png.Length);
            var data = SendBytes(Class("NSData"), Selector("dataWithBytes:length:"), buffer, png.Length);
            var image = Send(Send(Class("NSImage"), Selector("alloc")), Selector("initWithData:"), data);
            if (image == 0) return;
            Send(Send(Class("NSApplication"), Selector("sharedApplication")), Selector("setApplicationIconImage:"), image);
            Send(image, Selector("release"));
        }
        finally
        {
            PoolPop(pool);
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport(Objc, EntryPoint = "objc_getClass")] private static extern nint Class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc, EntryPoint = "sel_registerName")] private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint SendBytes(nint receiver, nint selector, nint bytes, nint length);
    [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPush")] private static extern nint PoolPush();
    [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPop")] private static extern void PoolPop(nint pool);
}