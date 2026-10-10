using System.Runtime.InteropServices;

namespace SharpRail.Scintilla;

internal static unsafe class ScintillaNative
{
    private const string Library = "SharpRail.Scintilla";

    // The library ships in the APK's lib/<abi> directory, which only the system linker searches, by file name.
    static ScintillaNative()
    {
        if (OperatingSystem.IsAndroid())
            NativeLibrary.SetDllImportResolver(typeof(ScintillaNative).Assembly, (name, _, _) =>
                name == Library ? NativeLibrary.Load("libSharpRail.Scintilla.so") : 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawCommand
    {
        internal int Operation, Surface;
        internal double Left, Top, Right, Bottom, Size, Width, Baseline;
        internal uint Fore, Back;
        internal void* Data;
        internal nint Length;
        internal double* Positions;
        internal int Weight, Italic;
        internal nint Start, End;
        internal int Rtl;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate double DrawCallback(nint context, in DrawCommand command);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint sr_create(DrawCallback callback, nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sr_failed();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint sr_revision(nint editor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sr_dirty(nint editor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_set_dirty(nint editor, int dirty);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_destroy(nint editor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint sr_send(nint editor, uint message, nuint w, nint l);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_resize(nint editor, double width, double height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sr_idle(nint editor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_paint(nint editor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_text(nint editor, byte[] text, int length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sr_key(nint editor, int key, int modifiers);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_mouse(nint editor, int kind, double x, double y, uint time, int modifiers);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_focus(nint editor, int focus);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_tick(nint editor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sr_direction(nint editor, int direction);
}