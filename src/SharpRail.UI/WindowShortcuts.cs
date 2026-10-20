using Avalonia.Input;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI;

/// <summary>
/// One window-level chord: the gesture menus and tooltips show, and the key-down test that reaches its
/// command. The two are declared together so they cannot drift.
/// </summary>
public sealed class WindowShortcut
{
    private readonly Func<KeyGesture> gesture;
    private readonly Func<KeyEventArgs, bool> matches;

    internal WindowShortcut(string id, Func<KeyGesture> gesture, Func<KeyEventArgs, bool> matches)
    {
        Id = id; this.gesture = gesture; this.matches = matches;
    }

    public string Id { get; }

    /// <summary>The chord as menus show it, using the platform's command modifier.</summary>
    public KeyGesture Gesture => gesture();

    /// <summary>Whether a key-down is this chord.</summary>
    public bool Matches(KeyEventArgs e) => matches(e);
}

/// <summary>The workbench window's chords, in the order they are tried.</summary>
public static class WindowShortcuts
{
    private static bool Command(KeyEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
    private static bool Shift(KeyEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    private static KeyGesture Mod(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, CommandKeys.Modifier | extra);

    public static readonly WindowShortcut OpenProject = new("OpenProject", () => Mod(Key.O), e => Command(e) && e.Key == Key.O);
    public static readonly WindowShortcut NewWindow = new("NewWindow", () => Mod(Key.N, KeyModifiers.Shift), e => Command(e) && e.Key == Key.N && Shift(e));
    public static readonly WindowShortcut SearchWorkspace = new("SearchWorkspace", () => Mod(Key.F, KeyModifiers.Shift), e => Command(e) && e.Key == Key.F && Shift(e));
    public static readonly WindowShortcut NewWorkspace = new("NewWorkspace", () => Mod(Key.N), e => Command(e) && e.Key == Key.N);
    /// <summary>Preferences' own chord on macOS; other platforms have no such convention to honour.</summary>
    public static readonly WindowShortcut Settings = new("Settings", () => Mod(Key.OemComma),
        e => CommandKeys.Modifier == KeyModifiers.Meta && e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.OemComma);
    public static readonly WindowShortcut ToggleLeft = new("ToggleLeft", () => Mod(Key.B), e => Command(e) && e.Key == Key.B);
    public static readonly WindowShortcut ToggleRight = new("ToggleRight", () => Mod(Key.J), e => Command(e) && e.Key == Key.J && !Shift(e));
    public static readonly WindowShortcut ZoomIn = new("ZoomIn", () => Mod(Key.OemPlus), e => Command(e) && e.Key is Key.OemPlus or Key.Add);
    public static readonly WindowShortcut ZoomOut = new("ZoomOut", () => Mod(Key.OemMinus), e => Command(e) && e.Key is Key.OemMinus or Key.Subtract);
    public static readonly WindowShortcut ZoomReset = new("ZoomReset", () => Mod(Key.D0), e => Command(e) && e.Key is Key.D0 or Key.NumPad0);
    public static readonly WindowShortcut Refresh = new("Refresh", () => new KeyGesture(Key.F5), e => e.Key == Key.F5);
    /// <summary>Mod+[ on macOS, Alt+Left elsewhere.</summary>
    public static readonly WindowShortcut Back = new("Back",
        () => OperatingSystem.IsMacOS() ? new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta) : new KeyGesture(Key.Left, KeyModifiers.Alt),
        e => OperatingSystem.IsMacOS() ? e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.OemOpenBrackets : e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Left);
    /// <summary>Mod+] on macOS, Alt+Right elsewhere.</summary>
    public static readonly WindowShortcut Forward = new("Forward",
        () => OperatingSystem.IsMacOS() ? new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Meta) : new KeyGesture(Key.Right, KeyModifiers.Alt),
        e => OperatingSystem.IsMacOS() ? e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.OemCloseBrackets : e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Right);

    /// <summary>The close chord menus show; <see cref="AppCommands"/> decides when it applies.</summary>
    public static KeyGesture Close => Mod(Key.W);

    /// <summary>Every chord, in dispatch order: a more specific chord precedes the one it would also match.</summary>
    public static IReadOnlyList<WindowShortcut> All { get; } =
    [OpenProject, NewWindow, SearchWorkspace, NewWorkspace, Settings, ToggleLeft, ToggleRight, ZoomIn, ZoomOut, ZoomReset, Refresh, Back, Forward];
}