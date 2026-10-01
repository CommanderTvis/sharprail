using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Input;

namespace Ghostty.Avalonia;

[Flags]
internal enum CellFlags : ushort
{
    Bold = 1 << 0, Italic = 1 << 1, Faint = 1 << 2, Blink = 1 << 3, Inverse = 1 << 4,
    Invisible = 1 << 5, Strikethrough = 1 << 6, Overline = 1 << 7, Selected = 1 << 8,
    LinkHover = 1 << 9
}

// Mirrors gav_vt_cell and gav_vt_frame in Native/GhosttyVt.c.
[StructLayout(LayoutKind.Sequential)]
internal struct Cell
{
    public uint Text;
    public ushort Length;
    public byte Wide;
    public byte Underline;
    public uint Foreground, Background, UnderlineColor;
    public CellFlags Flags;
    public ushort RowWrapped;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Frame
{
    public ushort Columns, Rows;
    public uint Background, Foreground, CursorColor;
    public ushort CursorX, CursorY;
    public byte CursorVisible, CursorBlinking, CursorStyle, CursorWideTail;
    public byte Dirty, AlternateScreen, MouseTracking, Reserved;
    public ulong ScrollTotal, ScrollOffset, ScrollLength;
    public Cell* Cells;
    public uint* Text;
}

/// <summary>One libghostty-vt terminal. Not thread-safe: use it from the thread that created it.</summary>
internal sealed unsafe class Vt : IDisposable
{
    private const string Library = "GhosttyAvaloniaVt";
    private readonly byte[] scratch = new byte[4096];
    private GCHandle self;
    private nint handle;

    internal Vt(int columns, int rows, int scrollback, Action<ReadOnlySpan<byte>> reply)
    {
        Reply = reply;
        self = GCHandle.Alloc(this);
        try
        {
            handle = gav_vt_new((ushort)columns, (ushort)rows, (nuint)scrollback, &OnWrite, GCHandle.ToIntPtr(self));
            if (handle == 0) throw new InvalidOperationException("libghostty-vt could not create a terminal.");
        }
        catch
        {
            self.Free();
            throw;
        }
    }

    // Query responses (device attributes, cursor reports) and encoded paste chunks for the PTY.
    private Action<ReadOnlySpan<byte>> Reply { get; }

    [UnmanagedCallersOnly]
    private static void OnWrite(nint context, byte* data, nuint length)
    {
        if (GCHandle.FromIntPtr(context).Target is Vt vt) vt.Reply(new ReadOnlySpan<byte>(data, (int)length));
    }

    internal void Write(ReadOnlySpan<byte> data) { fixed (byte* bytes = data) gav_vt_write(handle, bytes, (nuint)data.Length); }
    internal void Resize(int columns, int rows, int cellWidth, int cellHeight) =>
        gav_vt_resize(handle, (ushort)columns, (ushort)rows, (uint)cellWidth, (uint)cellHeight);
    internal void SetColors(TerminalColors colors) { fixed (uint* values = colors.Vt()) gav_vt_set_colors(handle, values); }
    internal bool OptionAsAlt { set => gav_vt_set_option_as_alt(handle, value); }
    internal bool Snapshot(out Frame frame) { fixed (Frame* pointer = &frame) return gav_vt_snapshot(handle, pointer); }
    internal void Scroll(long rows) => gav_vt_scroll(handle, rows);
    internal void Select(int kind, double x, double y, ulong nanoseconds) => gav_vt_select(handle, kind, x, y, nanoseconds);
    internal void SelectAll() => gav_vt_select_all(handle);
    internal void ClearSelection() => gav_vt_clear_selection(handle);

    internal ReadOnlySpan<byte> Key(int action, int key, ushort mods, ushort consumed, string? text, uint unshifted)
    {
        fixed (byte* output = scratch)
            return scratch.AsSpan(0, (int)gav_vt_key(handle, action, key, mods, consumed, text, unshifted, output, (nuint)scratch.Length));
    }

    // Null when the terminal does not track the mouse.
    internal ReadOnlySpan<byte> Mouse(int action, int button, ushort mods, float x, float y, out bool tracked)
    {
        fixed (byte* output = scratch)
        {
            tracked = gav_vt_mouse(handle, action, button, mods, x, y, output, (nuint)scratch.Length, out var written);
            return scratch.AsSpan(0, (int)written);
        }
    }

    internal byte[] Paste(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        // Bracketing adds 12 bytes; unsafe control characters are replaced one for one.
        var output = new byte[bytes.Length + 16];
        fixed (byte* input = bytes)
        fixed (byte* encoded = output)
            return output.AsSpan(0, (int)gav_vt_paste(handle, input, (nuint)bytes.Length, encoded, (nuint)output.Length)).ToArray();
    }

    internal ReadOnlySpan<byte> Focus(bool gained)
    {
        fixed (byte* output = scratch) return scratch.AsSpan(0, (int)gav_vt_focus(handle, gained, output, (nuint)scratch.Length));
    }

    internal string SelectionText() => Text(&gav_vt_selection_text);
    internal string ScreenText() => Text(&gav_vt_screen_text);

    private string Text(delegate*<nint, byte*, nuint, nuint> read)
    {
        var length = (int)read(handle, null, 0);
        if (length == 0) return "";
        var buffer = new byte[length];
        fixed (byte* output = buffer) length = (int)read(handle, output, (nuint)buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length));
    }

    public void Dispose()
    {
        if (handle == 0) return;
        gav_vt_free(handle);
        handle = 0;
        self.Free();
    }

    // Ghostty's key codes follow the W3C UI Events code values in this order, as do Avalonia's physical keys.
    private static readonly Dictionary<string, int> KeyCodes = """
        unidentified backquote backslash bracketleft bracketright comma digit0 digit1 digit2 digit3 digit4 digit5 digit6
        digit7 digit8 digit9 equal intlbackslash intlro intlyen a b c d e f g h i j k l m n o p q r s t u v w x y z minus
        period quote semicolon slash altleft altright backspace capslock contextmenu controlleft controlright enter
        metaleft metaright shiftleft shiftright space tab convert kanamode nonconvert delete end help home insert pagedown
        pageup arrowdown arrowleft arrowright arrowup numlock numpad0 numpad1 numpad2 numpad3 numpad4 numpad5 numpad6
        numpad7 numpad8 numpad9 numpadadd numpadbackspace numpadclear numpadclearentry numpadcomma numpaddecimal
        numpaddivide numpadenter numpadequal numpadmemoryadd numpadmemoryclear numpadmemoryrecall numpadmemorystore
        numpadmemorysubtract numpadmultiply numpadparenleft numpadparenright numpadsubtract numpadseparator numpadup
        numpaddown numpadright numpadleft numpadbegin numpadhome numpadend numpadinsert numpaddelete numpadpageup
        numpadpagedown escape f1 f2 f3 f4 f5 f6 f7 f8 f9 f10 f11 f12 f13 f14 f15 f16 f17 f18 f19 f20 f21 f22 f23 f24 f25
        fn fnlock printscreen scrolllock pause browserback browserfavorites browserforward browserhome browserrefresh
        browsersearch browserstop eject launchapp1 launchapp2 launchmail mediaplaypause mediaselect mediastop
        mediatracknext mediatrackprevious power sleep audiovolumedown audiovolumemute audiovolumeup wakeup copy cut paste
        """.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select((name, index) => (name, index))
        .ToDictionary(entry => entry.name, entry => entry.index);

    internal static int KeyCode(PhysicalKey key) =>
        KeyCodes.GetValueOrDefault(Enum.GetName(key)?.ToLowerInvariant() ?? "", 0);

    internal static ushort Mods(KeyModifiers modifiers) => (ushort)(
        (modifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0) | (modifiers.HasFlag(KeyModifiers.Control) ? 2 : 0) |
        (modifiers.HasFlag(KeyModifiers.Alt) ? 4 : 0) | (modifiers.HasFlag(KeyModifiers.Meta) ? 8 : 0));

    [DllImport(Library)] private static extern nint gav_vt_new(ushort columns, ushort rows, nuint scrollback, delegate* unmanaged<nint, byte*, nuint, void> write, nint context);
    [DllImport(Library)] private static extern void gav_vt_free(nint vt);
    [DllImport(Library)] private static extern void gav_vt_write(nint vt, byte* data, nuint length);
    [DllImport(Library)] private static extern void gav_vt_resize(nint vt, ushort columns, ushort rows, uint cellWidth, uint cellHeight);
    [DllImport(Library)] private static extern void gav_vt_set_colors(nint vt, uint* colors);
    [DllImport(Library)] private static extern void gav_vt_set_option_as_alt(nint vt, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(Library)][return: MarshalAs(UnmanagedType.I1)] private static extern bool gav_vt_snapshot(nint vt, Frame* frame);
    [DllImport(Library)] private static extern nuint gav_vt_key(nint vt, int action, int key, ushort mods, ushort consumed, [MarshalAs(UnmanagedType.LPUTF8Str)] string? text, uint unshifted, byte* output, nuint capacity);
    [DllImport(Library)][return: MarshalAs(UnmanagedType.I1)] private static extern bool gav_vt_mouse(nint vt, int action, int button, ushort mods, float x, float y, byte* output, nuint capacity, out nuint written);
    [DllImport(Library)] private static extern nuint gav_vt_paste(nint vt, byte* text, nuint length, byte* output, nuint capacity);
    [DllImport(Library)] private static extern nuint gav_vt_focus(nint vt, [MarshalAs(UnmanagedType.I1)] bool gained, byte* output, nuint capacity);
    [DllImport(Library)] private static extern void gav_vt_scroll(nint vt, long delta);
    [DllImport(Library)] private static extern void gav_vt_select(nint vt, int kind, double x, double y, ulong nanoseconds);
    [DllImport(Library)] private static extern void gav_vt_select_all(nint vt);
    [DllImport(Library)] private static extern void gav_vt_clear_selection(nint vt);
    [DllImport(Library)] private static extern nuint gav_vt_selection_text(nint vt, byte* output, nuint capacity);
    [DllImport(Library)] private static extern nuint gav_vt_screen_text(nint vt, byte* output, nuint capacity);
}
