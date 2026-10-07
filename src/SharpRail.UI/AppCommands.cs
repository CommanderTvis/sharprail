using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SharpRail.UI;

/// <summary>
/// The app's one owner of quit and close commands, shared by every window. Window input (and a future
/// native menu) calls the same methods, so each press dispatches once. Keyboard quit goes through the
/// confirmation gesture; <see cref="QuitNow"/> is the direct path for explicit menu and OS quit.
/// </summary>
public sealed class AppCommands
{
    private readonly Workbench workbench;
    private readonly QuitConfirmation confirmation;
    private bool quitKeyDown;

    public AppCommands(Workbench workbench, Func<long>? now = null, Func<Action, TimeSpan, IDisposable>? every = null)
    {
        this.workbench = workbench;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        confirmation = new QuitConfirmation(() => quitKeyDown, () => workbench.Windows.Any(window => window.IsActive),
            () => Shutdown(), ShowHint, now ?? (() => clock.ElapsedMilliseconds), every ?? Every);
    }

    /// <summary>Ends the app through its normal shutdown path; the composition root supplies it.</summary>
    public Action Shutdown { get; set; } = () => { };

    private static IDisposable Every(Action callback, TimeSpan interval) =>
        new Timer(_ => Dispatcher.UIThread.Post(callback), null, interval, interval);

    private void ShowHint(QuitHint hint)
    {
        foreach (var window in workbench.Windows) window.ShowQuitHint(window.IsActive ? hint : QuitHint.Hidden);
    }

    private static readonly KeyModifiers Command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>The quit chord applies on macOS and Linux; Windows keeps Alt+F4 as an ordinary close.</summary>
    public static bool QuitChordAvailable => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>
    /// Whether a destructive letter chord matches: the typed Latin letter wins, and layouts that type no
    /// Latin letter fall back to the key's physical position.
    /// </summary>
    public static bool Matches(KeyEventArgs e, char letter)
    {
        if (e.KeyModifiers != Command) return false;
        if (e.KeySymbol is { Length: 1 } symbol && symbol[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            return char.ToUpperInvariant(symbol[0]) == letter;
        if (e.Key is >= Key.A and <= Key.Z) return e.Key - Key.A + 'A' == letter;
        return e.PhysicalKey == (letter == 'Q' ? PhysicalKey.Q : PhysicalKey.W);
    }

    /// <summary>The close chord: Command-W, or Ctrl+W and Ctrl+F4 off macOS.</summary>
    public static bool IsClose(KeyEventArgs e) =>
        Matches(e, 'W') || (!OperatingSystem.IsMacOS() && e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.Control);

    /// <summary>Tunnel key-down from a window. Returns whether the key was a command and is consumed.</summary>
    public bool KeyDown(WorkbenchWindow window, KeyEventArgs e)
    {
        if (QuitChordAvailable && Matches(e, 'Q'))
        {
            // Repeat events arrive with no key-up between them and never confirm.
            if (!quitKeyDown) { quitKeyDown = true; Quit(); }
            return true;
        }
        if (!IsClose(e)) return false;
        // A terminal keeps Ctrl+W (delete word) outside macOS.
        if (!OperatingSystem.IsMacOS() && e.Key == Key.W && InTerminal(window)) return false;
        Close(window);
        return true;
    }

    /// <summary>Tunnel key-up from a window: releasing the letter or any modifier ends the hold.</summary>
    public void KeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Q or Key.LWin or Key.RWin or Key.LeftCtrl or Key.RightCtrl || e.PhysicalKey == PhysicalKey.Q)
            quitKeyDown = false;
    }

    /// <summary>A window lost focus: an unconfirmed gesture ends, a confirmed one completes.</summary>
    public void Deactivated() { quitKeyDown = false; confirmation.Cancel(); }

    /// <summary>Keyboard quit: one press of the confirmed gesture, with the key currently down.</summary>
    public void Quit() => confirmation.Press(quitKeyDown);

    /// <summary>Returns the gesture to idle, for a quit that did not end the app.</summary>
    public void Reset() => confirmation.Reset();

    /// <summary>Quit without confirmation, for explicit menu and operating-system quit.</summary>
    public void QuitNow() => confirmation.QuitNow();

    /// <summary>Dismisses an open popup first, otherwise requests the docking close. Never closes a window.</summary>
    public void Close(WorkbenchWindow window)
    {
        if (window.OwnedWindows.Count > 0) return;
        if (TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement() is Visual focused &&
            focused.FindAncestorOfType<PopupRoot>(true) is { Parent: Popup popup })
        { popup.IsOpen = false; return; }
        window.Layout.RequestClose();
    }

    private static bool InTerminal(WorkbenchWindow window) =>
        TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement() is Visual focused &&
        focused.FindAncestorOfType<Terminal.TerminalView>(true) is not null;
}