using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Terminal;

internal sealed class GhosttyTerminal : Border, IDisposable
{
    private readonly TerminalHost terminal;

    public GhosttyTerminal(string directory, string clipboardDirectory)
    {
        terminal = new TerminalHost(directory, clipboardDirectory) { Focusable = true };
        Child = terminal;
        Ui.ThemeChanged += terminal.UpdateColors;
    }

    public void Dispose()
    {
        Ui.ThemeChanged -= terminal.UpdateColors;
        terminal.Dispose();
    }

    internal void FocusTerminal() => terminal.FocusTerminal();

    protected override void OnGotFocus(Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    private sealed class TerminalHost(string directory, string clipboardDirectory) : NativeControlHost, IDisposable
    {
        private nint view;
        private bool disposed;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (view == 0)
            {
                view = Native.Create(directory, clipboardDirectory);
                if (view == 0) throw new InvalidOperationException("Ghostty could not create a Metal terminal surface.");
                UpdateColors();
            }
            return new PlatformHandle(view, "NSView");
        }

        // Detaching a hidden tab releases Avalonia's attachment, not the shell session.
        protected override void DestroyNativeControlCore(IPlatformHandle control) { }

        protected override void OnGotFocus(Avalonia.Input.FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            if (view != 0) Native.Focus(view);
        }

        internal void FocusTerminal()
        {
            Focus();
            if (view != 0) Native.Focus(view);
        }

        /// <summary>Mirrors the reference's xterm theme: selection composited over the surface, and its contrast floor.</summary>
        internal void UpdateColors()
        {
            if (view == 0) return;
            var theme = Ui.Theme;
            var background = Ui.Surface.Color;
            uint[] colors =
            [
                background.ToUInt32(), Ui.TextBrush.Color.ToUInt32(), Ui.Accent.Color.ToUInt32(),
                Ui.Over(theme["editorSelection"], background).ToUInt32(), theme.Colors["editorSelectionForeground"]?.ToUInt32() ?? 0,
                .. theme.Ansi.Select(color => color.ToUInt32())
            ];
            Native.SetColors(view, colors, theme.IsHighContrast ? 7 : 4.5);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (view != 0) { Native.Destroy(view); view = 0; }
        }
    }

    private static class Native
    {
        private const string Library = "SharpRailGhostty";
        [DllImport(Library, EntryPoint = "sr_terminal_create")]
        internal static extern nint Create([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string clipboardDirectory);
        [DllImport(Library, EntryPoint = "sr_terminal_set_colors")]
        internal static extern void SetColors(nint view, uint[] colors, double minimumContrast);
        [DllImport(Library, EntryPoint = "sr_terminal_destroy")]
        internal static extern void Destroy(nint view);
        [DllImport(Library, EntryPoint = "sr_terminal_focus")]
        internal static extern void Focus(nint view);
    }
}
