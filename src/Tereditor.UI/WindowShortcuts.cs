using Avalonia.Input;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI;

/// <summary>
/// One window-level command: its stable keymap action id, the gesture menus and tooltips show and the key-down test that reaches
/// it. Both read the active keymap (<see cref="ActiveKeymap"/>), so they cannot drift and follow the user's preset.
/// </summary>
public sealed class WindowShortcut
{
    internal WindowShortcut(string id) => Id = id;

    public string Id { get; }

    /// <summary>The command's first chord as menus show it; a gesture without a key when the command is unbound.</summary>
    public KeyGesture Gesture => ActiveKeymap.Gesture(Id);

    /// <summary>Whether a key-down is one of the command's chords.</summary>
    public bool Matches(KeyEventArgs e) => ActiveKeymap.Matches(Id, e);
}

/// <summary>The workbench window's commands, in the order they are tried; the chords are the active keymap's.</summary>
public static class WindowShortcuts
{
    private static KeyGesture Mod(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, CommandKeys.Modifier | extra);

    public static readonly WindowShortcut OpenProject = new(KeymapActions.OpenProject);
    public static readonly WindowShortcut NewWindow = new(KeymapActions.NewWindow);
    public static readonly WindowShortcut SearchWorkspace = new(KeymapActions.SearchWorkspace);
    public static readonly WindowShortcut NewWorkspace = new(KeymapActions.NewWorkspace);
    /// <summary>Preferences: Cmd+, on macOS, Ctrl+Alt+S elsewhere in the IntelliJ preset.</summary>
    public static readonly WindowShortcut Settings = new(KeymapActions.Settings);
    public static readonly WindowShortcut ToggleLeft = new(KeymapActions.ToggleLeft);
    public static readonly WindowShortcut ToggleRight = new(KeymapActions.ToggleRight);
    public static readonly WindowShortcut ZoomIn = new(KeymapActions.ZoomIn);
    public static readonly WindowShortcut ZoomOut = new(KeymapActions.ZoomOut);
    public static readonly WindowShortcut ZoomReset = new(KeymapActions.ZoomReset);
    public static readonly WindowShortcut Refresh = new(KeymapActions.Refresh);
    public static readonly WindowShortcut Back = new(KeymapActions.Back);
    public static readonly WindowShortcut Forward = new(KeymapActions.Forward);

    /// <summary>
    /// Ghostty's undo: Mod+Shift+T, or Mod+Z as Ghostty also binds it. Like Ghostty's performable binding it acts only
    /// when there is a closed tab to reopen, so otherwise the key goes on to whatever has focus.
    /// </summary>
    public static readonly WindowShortcut UndoClose = new(KeymapActions.UndoClose);
    /// <summary>Ghostty's redo: closes the tab undo reopened.</summary>
    public static readonly WindowShortcut RedoClose = new(KeymapActions.RedoClose);

    /// <summary>The close chord menus show; <see cref="AppCommands"/> decides when it applies.</summary>
    public static KeyGesture Close => Mod(Key.W);

    /// <summary>Every chord, in dispatch order: a more specific chord precedes the one it would also match.</summary>
    public static IReadOnlyList<WindowShortcut> All { get; } =
    [OpenProject, NewWindow, SearchWorkspace, NewWorkspace, Settings, ToggleLeft, ToggleRight, ZoomIn, ZoomOut, ZoomReset, Refresh, Back, Forward, UndoClose, RedoClose];
}