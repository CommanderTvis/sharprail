using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SharpRail.UI;

/// <summary>
/// The native menu bar on macOS: Edit and Window menus beside the application menu, whose Hide and Quit
/// items the platform supplies. Items call the commands their chords already reach, so a menu pick and a
/// key press are one path; a menu Quit is the direct quit, outside the keyboard confirmation gesture.
/// </summary>
public static class ApplicationMenu
{
    private const KeyModifiers Command = KeyModifiers.Meta;

    /// <summary>Gives every window that opens the menu bar: a dialog needs Edit as much as the workbench does.</summary>
    public static IDisposable Install(Application app, AppCommands commands)
    {
        // An empty application menu keeps the platform items and drops the framework's About entry.
        NativeMenu.SetMenu(app, []);
        return Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (NativeMenu.GetMenu(window) is null) NativeMenu.SetMenu(window, Build(window, commands));
        });
    }

    public static NativeMenu Build(Window window, AppCommands commands)
    {
        NativeMenu edit =
        [
            Item("Undo", Key.Z, Command, () => Edit(window, Key.Z, Command, box => box.Undo())),
            Item("Redo", Key.Z, Command | KeyModifiers.Shift, () => Edit(window, Key.Z, Command | KeyModifiers.Shift, box => box.Redo())),
            new NativeMenuItemSeparator(),
            Item("Cut", Key.X, Command, () => Edit(window, Key.X, Command, box => box.Cut())),
            Item("Copy", Key.C, Command, () => Edit(window, Key.C, Command, box => box.Copy())),
            Item("Paste", Key.V, Command, () => Edit(window, Key.V, Command, box => box.Paste())),
            // Delete has no chord to forward: outside a text box the Delete key means something else.
            Item("Delete", Key.None, KeyModifiers.None, () => Edit(window, Key.None, KeyModifiers.None, box => box.SelectedText = "")),
            Item("Select All", Key.A, Command, () => Edit(window, Key.A, Command, box => box.SelectAll())),
        ];
        NativeMenu windows =
        [
            Item("Minimize", Key.M, Command, () => window.WindowState = WindowState.Minimized),
            Item("Zoom", Key.None, KeyModifiers.None, () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
            Item("Close", Key.W, Command, () => { if (window is WorkbenchWindow workbench) commands.Close(workbench); else window.Close(); }),
            new NativeMenuItemSeparator(),
            Item("Bring All to Front", Key.None, KeyModifiers.None, () => BringAllToFront(window)),
        ];
        return [new NativeMenuItem("Edit") { Menu = edit }, new NativeMenuItem("Window") { Menu = windows }];
    }

    private static NativeMenuItem Item(string header, Key key, KeyModifiers modifiers, Action action)
    {
        var item = new NativeMenuItem(header);
        if (key != Key.None) item.Gesture = new KeyGesture(key, modifiers);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// A text box takes the command directly. Editors, terminals and selectable text implement these chords
    /// themselves, so a menu pick reaches them as the chord and stays on their one editing path.
    /// </summary>
    private static void Edit(Window window, Key key, KeyModifiers modifiers, Action<TextBox> text)
    {
        var focused = window.FocusManager?.GetFocusedElement();
        if (focused is TextBox box) { text(box); return; }
        if (key == Key.None || focused is not Interactive target) return;
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            KeySymbol = ((char)(key - Key.A + (modifiers.HasFlag(KeyModifiers.Shift) ? 'A' : 'a'))).ToString(),
            PhysicalKey = PhysicalKey.A + (key - Key.A)
        });
    }

    private static void BringAllToFront(Window window)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var other in desktop.Windows) if (other != window) other.Activate();
        window.Activate();
    }
}