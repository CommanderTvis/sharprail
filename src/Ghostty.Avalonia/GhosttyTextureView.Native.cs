using System.Runtime.InteropServices;

namespace Ghostty.Avalonia;

public sealed partial class GhosttyTextureView
{
    private static class Native
    {
        private const string Library = "GhosttyAvaloniaView";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Callback();
        [DllImport(Library, EntryPoint = "gav_texture_enable")] internal static extern nint Enable(nint view);
        [DllImport(Library, EntryPoint = "gav_texture_notify")] internal static extern void Notify(nint frames, Callback? callback);
        [DllImport(Library, EntryPoint = "gav_texture_acquire")] internal static extern nint Acquire(nint frames, out int width, out int height, out nint lease);
        [DllImport(Library, EntryPoint = "gav_texture_return")] internal static extern void Return(nint lease);
        [DllImport(Library, EntryPoint = "gav_texture_release")] internal static extern void Release(nint handle);
        [DllImport(Library, EntryPoint = "gav_texture_stop")] internal static extern void Stop(nint frames);
        [DllImport(Library, EntryPoint = "gav_texture_retained_count")] internal static extern int RetainedCount(nint frames);
        [DllImport(Library, EntryPoint = "gav_texture_resize")] internal static extern void Resize(nint view, double width, double height, double scale);
        [DllImport(Library, EntryPoint = "gav_texture_focus")] internal static extern void Focus(nint view, [MarshalAs(UnmanagedType.I1)] bool focused);
        [DllImport(Library, EntryPoint = "gav_texture_visible")] internal static extern void Visible(nint view, [MarshalAs(UnmanagedType.I1)] bool visible);
        [DllImport(Library, EntryPoint = "gav_texture_key")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Key(nint view, int action, uint keycode, int mods, int consumed,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? text, uint unshifted);
        [DllImport(Library, EntryPoint = "gav_texture_text")] internal static extern void Text(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
        [DllImport(Library, EntryPoint = "gav_texture_preedit")] internal static extern void Preedit(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string? text);
        [DllImport(Library, EntryPoint = "gav_texture_ime_point")] internal static extern void ImePoint(nint view, out double x, out double y, out double width, out double height);
        [DllImport(Library, EntryPoint = "gav_texture_mouse")] internal static extern void Mouse(nint view, double x, double y, int mods, int action, int button);
        [DllImport(Library, EntryPoint = "gav_texture_over_link")][return: MarshalAs(UnmanagedType.I1)] internal static extern bool OverLink(nint view);
        [DllImport(Library, EntryPoint = "gav_texture_scroll")] internal static extern void Scroll(nint view, double x, double y);
        [DllImport(Library, EntryPoint = "gav_texture_action")] internal static extern void Action(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string action);
    }
}