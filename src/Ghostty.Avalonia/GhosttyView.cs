using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Ghostty.Avalonia;

/// <summary>Where a <see cref="GhosttyView"/> starts its child process.</summary>
/// <param name="Command">A shell command line; null runs the user's login shell.</param>
/// <param name="Environment">One variable added to the child's environment.</param>
/// <param name="ClipboardImageDirectory">When set, pasting an image saves it there as PNG and pastes its shell-quoted path.</param>
public sealed record GhosttyLaunch(string WorkingDirectory, string? Command = null,
    KeyValuePair<string, string>? Environment = null, string? ClipboardImageDirectory = null);

/// <summary>A Command key equivalent offered to the host before the terminal sees it.</summary>
public sealed class GhosttyShortcutEventArgs(Key key, KeyModifiers modifiers, bool repeat) : EventArgs
{
    public Key Key { get; } = key;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool IsRepeat { get; } = repeat;
    /// <summary>Set to keep the key from the terminal.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Embedded libghostty in an AppKit view: Ghostty owns emulation, its child process, input, fonts and the
/// Metal renderer. Avalonia hosts the view as a native control, so it always draws above Avalonia content.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class GhosttyView : NativeControlHost, IDisposable
{
    private GCHandle self;
    private nint view;
    private TerminalColors colors = TerminalColors.Default;

    /// <exception cref="InvalidOperationException">Ghostty could not create a Metal surface.</exception>
    public GhosttyView(GhosttyLaunch launch)
    {
        Focusable = true;
        self = GCHandle.Alloc(this);
        unsafe
        {
            view = Native.Create(launch.WorkingDirectory, launch.ClipboardImageDirectory, launch.Command,
                launch.Environment?.Key, launch.Environment?.Value, &OnEvent, &OnShortcut, GCHandle.ToIntPtr(self));
        }
        if (view == 0)
        {
            self.Free();
            throw new InvalidOperationException("Ghostty could not create a Metal terminal surface.");
        }
        Native.SetColors(view, colors.NativeView(), colors.MinimumContrast);
    }

    /// <summary>The child exited with its status. Ghostty keeps the final screen until the view is disposed.</summary>
    public event EventHandler<int>? Exited;
    public event EventHandler<GhosttyShortcutEventArgs>? Shortcut;

    /// <summary>The AppKit view, for platform automation.</summary>
    public nint Handle => view;

    public TerminalColors Colors
    {
        get => colors;
        set { colors = value; if (view != 0) Native.SetColors(view, value.NativeView(), value.MinimumContrast); }
    }

    /// <summary>True while a process other than the shell holds the terminal's foreground.</summary>
    public bool IsBusy => view != 0 && Native.Busy(view);
    /// <summary>True once Ghostty presented a frame with more than one colour.</summary>
    public bool IsRendered => view != 0 && Native.Rendered(view);

    /// <summary>Types text as keyboard input; newlines press Return.</summary>
    public void Type(string text) { if (view != 0) Native.Input(view, text); }

    /// <summary>Shift+Enter sends ESC CR, the newline agents read, unless the program negotiated a keyboard protocol.</summary>
    public bool AgentNewline
    {
        get;
        set { field = value; if (view != 0) Native.SetAgentNewline(view, value); }
    }

    // For renderers that deliver keys themselves: false when a negotiated protocol should encode Shift+Enter.
    internal bool SendAgentNewline() => view != 0 && Native.AgentNewline(view);

    /// <summary>The screen as plain text.</summary>
    public string ReadScreen()
    {
        if (view == 0) return "";
        var buffer = new byte[1 << 20];
        var count = (int)Native.Read(view, buffer, (nuint)buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, count);
    }

    public void FocusTerminal()
    {
        Focus();
        if (view != 0) Native.Focus(view);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        ObjectDisposedException.ThrowIf(view == 0, this);
        return new PlatformHandle(view, "NSView");
    }

    // Detaching from a hidden tab releases Avalonia's attachment, not the surface.
    protected override void DestroyNativeControlCore(IPlatformHandle control) { }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (view != 0) Native.Focus(view);
    }

    public void Dispose()
    {
        if (view == 0) return;
        Native.Destroy(view);
        view = 0;
        self.Free();
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint context, int kind, int value)
    {
        if (GCHandle.FromIntPtr(context).Target is not GhosttyView owner || kind != 1) return;
        Dispatcher.UIThread.Post(() => owner.Exited?.Invoke(owner, value));
    }

    // Runs synchronously on the main thread from AppKit's key handling.
    [UnmanagedCallersOnly]
    private static unsafe byte OnShortcut(nint context, byte* key, int mods, byte repeat)
    {
        if (GCHandle.FromIntPtr(context).Target is not GhosttyView owner || owner.Shortcut is null) return 0;
        var name = Marshal.PtrToStringUTF8((nint)key) ?? "";
        var args = new GhosttyShortcutEventArgs(KeyFor(name), Modifiers(mods), repeat != 0);
        owner.Shortcut(owner, args);
        return args.Handled ? (byte)1 : (byte)0;
    }

    private static Key KeyFor(string name) => name is [var c] ? c switch
    {
        >= 'a' and <= 'z' => Key.A + (c - 'a'),
        >= '0' and <= '9' => Key.D0 + (c - '0'),
        _ => Key.None
    } : Key.None;

    // Ghostty's modifier bits: shift, ctrl, alt, super.
    private static KeyModifiers Modifiers(int mods) =>
        ((mods & 1) != 0 ? KeyModifiers.Shift : 0) | ((mods & 2) != 0 ? KeyModifiers.Control : 0) |
        ((mods & 4) != 0 ? KeyModifiers.Alt : 0) | ((mods & 8) != 0 ? KeyModifiers.Meta : 0);

    private static unsafe class Native
    {
        private const string Library = "GhosttyAvaloniaView";
        [DllImport(Library, EntryPoint = "gav_view_create")]
        internal static extern nint Create([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string? clipboardDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? command, [MarshalAs(UnmanagedType.LPUTF8Str)] string? environmentName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? environmentValue, delegate* unmanaged<nint, int, int, void> callback,
            delegate* unmanaged<nint, byte*, int, byte, byte> shortcut, nint context);
        [DllImport(Library, EntryPoint = "gav_view_set_colors")]
        internal static extern void SetColors(nint view, uint[] colors, double minimumContrast);
        [DllImport(Library, EntryPoint = "gav_view_destroy")]
        internal static extern void Destroy(nint view);
        [DllImport(Library, EntryPoint = "gav_view_focus")]
        internal static extern void Focus(nint view);
        [DllImport(Library, EntryPoint = "gav_view_busy")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Busy(nint view);
        [DllImport(Library, EntryPoint = "gav_view_set_agent_newline")]
        internal static extern void SetAgentNewline(nint view, [MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Library, EntryPoint = "gav_view_agent_newline")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool AgentNewline(nint view);
        [DllImport(Library, EntryPoint = "gav_view_input")]
        internal static extern void Input(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
        [DllImport(Library, EntryPoint = "gav_view_read")]
        internal static extern nuint Read(nint view, byte[] buffer, nuint capacity);
        [DllImport(Library, EntryPoint = "gav_view_rendered")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Rendered(nint view);
    }
}