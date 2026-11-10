using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

using SharpRail.UI.Editor;

namespace SharpRail.UI;

/// <summary>
/// The native menu bar on macOS: File, Edit, View, Window and Help in a workbench window, Edit and Window in any
/// other, beside the application menu, whose Hide and Quit items the platform supplies. Every item is a second way
/// into a command the window already has: it calls what that command's chord, button or menu calls, so a menu pick
/// and the primary path are one; a menu Quit is the direct quit, outside the keyboard confirmation gesture.
/// </summary>
public static class ApplicationMenu
{
    private const KeyModifiers Command = KeyModifiers.Meta;

    /// <summary>Gives every window that opens the menu bar: a dialog needs Edit as much as the workbench does.</summary>
    public static IDisposable Install(Application app, AppCommands commands)
    {
        return Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (NativeMenu.GetMenu(window) is null) NativeMenu.SetMenu(window, Build(window, commands));
        });
    }

    /// <summary>
    /// Sets the application menu. Avalonia fills it with its own "About Avalonia" when none is set by the time the platform
    /// builds the menu bar, so this runs from <c>Initialize</c>, before that; the menu holds About SharpRail and the platform items follow.
    /// </summary>
    public static void SetApplicationMenu(Application app) => NativeMenu.SetMenu(app, AppMenu(() => Panels.Dialogs.About(ActiveWindow()),
        () => { if (ActiveWindow() is WorkbenchWindow { IsActive: true } window) window.Run(WindowShortcuts.Settings); }));

    /// <summary>
    /// The application menu's own items; the platform adds Services, Hide, Hide Others, Show All and Quit around them.
    /// Settings is the key workbench window's Command-comma; like the chord it does nothing while another window is key.
    /// </summary>
    public static NativeMenu AppMenu(Action about, Action settings) =>
    [
        Item("About " + AboutInfo.Name, Key.None, KeyModifiers.None, about),
        new NativeMenuItemSeparator(),
        Follow(WithIcon(Item("Settings…", WindowShortcuts.Settings.Gesture, settings), SettingsIcon), WindowShortcuts.Settings, null),
    ];

    private static readonly Lazy<Bitmap> SettingsGlyph = new(() => Gear(16, 2));

    /// <summary>The gear beside Settings, made once; macOS only, as the native menu is.</summary>
    private static Bitmap? SettingsIcon => OperatingSystem.IsMacOS() ? SettingsGlyph.Value : null;

    private static NativeMenuItem WithIcon(NativeMenuItem item, Bitmap? icon)
    {
        item.Icon = icon;
        return item;
    }

    /// <summary>
    /// A gear in black with alpha at <paramref name="points"/> points and <paramref name="scale"/> pixels per point (16 at 2 is the native 16 pt menu icon
    /// at 2x). Black on transparent is what macOS tints as a template image, so it follows the light or dark menu and the selection highlight.
    /// </summary>
    public static Bitmap Gear(int points, int scale)
    {
        var size = points * scale;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96 * scale, 96 * scale));
        var middle = points / 2.0;
        var outer = points * 0.47;
        var root = points * 0.37;
        const int teeth = 8;
        var body = new StreamGeometry();
        using (var context = body.Open())
        {
            context.SetFillRule(FillRule.EvenOdd);
            for (var i = 0; i < teeth * 4; i++)
            {
                // Each tooth is a flat top and a flat root, 45 degrees apart over the circle, 11 degrees of flat per side.
                var step = Math.PI * 2 / (teeth * 4);
                var angle = (i - 0.5) * step - Math.PI / 2;
                var radius = (i / 2) % 2 == 0 ? outer : root;
                var point = new Point(middle + Math.Cos(angle) * radius, middle + Math.Sin(angle) * radius);
                if (i == 0) context.BeginFigure(point, true); else context.LineTo(point);
            }
            context.EndFigure(true);
            var hole = points * 0.16;
            context.BeginFigure(new Point(middle + hole, middle), true);
            context.ArcTo(new Point(middle - hole, middle), new Size(hole, hole), 0, false, SweepDirection.Clockwise);
            context.ArcTo(new Point(middle + hole, middle), new Size(hole, hole), 0, false, SweepDirection.Clockwise);
            context.EndFigure(true);
        }
        using var drawing = bitmap.CreateDrawingContext();
        drawing.DrawGeometry(Brushes.Black, null, body);
        return bitmap;
    }

    private static Window? ActiveWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(window => window.IsActive) ?? desktop.MainWindow : null;

    public static NativeMenu Build(Window window, AppCommands commands)
    {
        if (window is WorkbenchWindow workbench) return WorkbenchMenus(workbench, commands);
        NativeMenu edit = Editing(window);
        NativeMenu windows =
        [
            Item("Minimize", Key.M, Command, () => window.WindowState = WindowState.Minimized),
            Zoom(window),
            Item("Close", WindowShortcuts.Close.Key, WindowShortcuts.Close.KeyModifiers, window.Close),
            new NativeMenuItemSeparator(),
            Item("Bring All to Front", Key.None, KeyModifiers.None, () => BringAllToFront(window)),
        ];
        return [new NativeMenuItem("Edit") { Menu = edit }, new NativeMenuItem("Window") { Menu = windows }];
    }

    private static NativeMenu WorkbenchMenus(WorkbenchWindow window, AppCommands commands)
    {
        NativeMenuItem Shortcut(string header, WindowShortcut shortcut) => Follow(Item(header, shortcut.Gesture, () => { if (window.CanRun(shortcut)) window.Run(shortcut); }), shortcut, window);
        var recent = new NativeMenu();
        void FillRecent()
        {
            var paths = window.RecentProjects();
            if (recent.Items.OfType<NativeMenuItem>().Select(item => item.Header).SequenceEqual(paths)) return;
            recent.Items.Clear();
            foreach (var path in paths) recent.Items.Add(Item(path, Key.None, KeyModifiers.None, () => window.OpenRecentProject(path)));
        }
        FillRecent();
        window.ProjectsChanged += FillRecent;
        var recentItem = new NativeMenuItem("Open Recent") { Menu = recent };
        NativeMenu file =
        [
            Shortcut("New Workspace", WindowShortcuts.NewWorkspace),
            Shortcut("New Window", WindowShortcuts.NewWindow),
            new NativeMenuItemSeparator(),
            Shortcut("Open Project…", WindowShortcuts.OpenProject),
            Item("Clone Repository…", Key.None, KeyModifiers.None, window.CloneProject),
            Item("Create Project…", Key.None, KeyModifiers.None, window.CreateProject),
            recentItem,
            new NativeMenuItemSeparator(),
            Item("Close Tab", WindowShortcuts.Close, () => commands.Close(window)),
            Item("Close Window", new KeyGesture(Key.W, Command | KeyModifiers.Shift), window.Close),
            new NativeMenuItemSeparator(),
            Save(window),
        ];
        NativeMenu edit = Editing(window);
        edit.Items.Add(new NativeMenuItemSeparator());
        // Mod+Z stays Undo's key equivalent; this item shows Ghostty's other undo chord and acts whatever has focus.
        edit.Items.Add(Follow(Item("Undo Close Tab", WindowShortcuts.UndoClose.Gesture, () => window.UndoClose()), WindowShortcuts.UndoClose, window));
        edit.Items.Add(new NativeMenuItemSeparator());
        edit.Items.Add(Item("Find", Key.F, Command, () => Edit(window, Key.F, Command, _ => { })));
        edit.Items.Add(new NativeMenuItemSeparator());
        var language = Language(window);
        foreach (var item in language) edit.Items.Add(item);
        if (Terminal.SecureInput.Shared.Supported)
        {
            edit.Items.Add(new NativeMenuItemSeparator());
            edit.Items.Add(SecureKeyboardEntry(Terminal.SecureInput.Shared));
        }
        NativeMenu view =
        [
            Shortcut("Zoom In", WindowShortcuts.ZoomIn),
            Shortcut("Zoom Out", WindowShortcuts.ZoomOut),
            Shortcut("Actual Size", WindowShortcuts.ZoomReset),
        ];
        NativeMenu windows =
        [
            Item("Minimize", Key.M, Command, () => window.WindowState = WindowState.Minimized),
            Zoom(window),
            new NativeMenuItemSeparator(),
            Item("Host Settings…", Key.None, KeyModifiers.None, () => window.ShowSettings("Host")),
#if !ANDROID && !BROWSER
            Item("Connect to a Tailscale Host…", Key.None, KeyModifiers.None, () => Panels.SettingsView.ConnectTailscale(window.ShowNotification)),
#endif
            new NativeMenuItemSeparator(),
            Item("Bring All to Front", Key.None, KeyModifiers.None, () => BringAllToFront(window)),
        ];
        // The secondary way into About's Report an issue link.
        NativeMenu help = [Item("Report an Issue…", Key.None, KeyModifiers.None, () => Panels.AboutFields.ReportIssue(window))];
        return
        [
            new NativeMenuItem("File") { Menu = file }, new NativeMenuItem("Edit") { Menu = edit }, new NativeMenuItem("View") { Menu = view },
            new NativeMenuItem("Window") { Menu = windows }, new NativeMenuItem("Help") { Menu = help }
        ];
    }

    /// <summary>
    /// The editor context menu's language actions, enabled while the focused editor's file has a running server. They
    /// follow focus and the editor's own last answer, held in memory, so the menu never asks the host.
    /// </summary>
    private static NativeMenuItem[] Language(WorkbenchWindow window)
    {
        CodeDocumentView? editor = null;
        var items = CodeDocumentView.LanguageOperations.Select(entry =>
        {
            var item = new NativeMenuItem(entry.Label) { IsEnabled = false };
            // The key equivalent is the action's active first chord and follows keymap changes in place.
            void Gesture() => item.Gesture = !ActiveKeymap.Recording && ActiveKeymap.Gesture(entry.ActionId) is { Key: not Key.None } gesture ? gesture : null;
            Gesture();
            ActiveKeymap.Changed += Gesture;
            window.Closed += (_, _) => ActiveKeymap.Changed -= Gesture;
            item.Click += (_, _) => { if (editor is { LanguageAvailable: true } target) target.RunLanguageOperation(entry.Operation); };
            return item;
        }).ToArray();
        void Refresh() { foreach (var item in items) item.IsEnabled = editor?.LanguageAvailable == true; }
        window.AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            var next = e.Source is Visual source && source.FindAncestorOfType<CodeDocumentView>(true) is { } view &&
                (source == view.Editor || view.Editor.IsVisualAncestorOf(source)) ? view : null;
            if (next != editor)
            {
                if (editor is not null) editor.LanguageAvailabilityChanged -= Refresh;
                editor = next;
                if (editor is not null) editor.LanguageAvailabilityChanged += Refresh;
            }
            Refresh();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        return items;
    }

    /// <summary>Ghostty's toggle_secure_input, the only menu entry for it. Checked while on.</summary>
    internal static NativeMenuItem SecureKeyboardEntry(Terminal.SecureInput secure)
    {
        var item = Item(Terminal.TerminalView.SecureInputMenuHeader, Key.None, KeyModifiers.None, secure.Toggle);
        item.ToggleType = MenuItemToggleType.CheckBox;
        void Refresh() => item.IsChecked = secure.Manual;
        Refresh();
        secure.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        return item;
    }

    /// <summary>Command-S's own save, enabled while the editor it saves has unsaved changes.</summary>
    private static NativeMenuItem Save(WorkbenchWindow window)
    {
        var item = Item("Save", Key.S, Command, () => { if (window.CanSave) _ = window.SaveSelectedAsync(); });
        void Refresh() => item.IsEnabled = window.CanSave;
        Refresh();
        window.SaveStateChanged += Refresh;
        return item;
    }

    private static NativeMenuItem Zoom(Window window) =>
        Item("Zoom", Key.None, KeyModifiers.None, () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);

    private static NativeMenu Editing(Window window)
    {
        return
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
    }

    /// <summary>
    /// Keeps a menu item's key equivalent on its command's active chord: the keymap can change while the menu bar is up, and the system
    /// fires key equivalents itself. The item is edited in place; the owner window's close ends the subscription.
    /// </summary>
    private static NativeMenuItem Follow(NativeMenuItem item, WindowShortcut shortcut, Window? owner)
    {
        void Update() => item.Gesture = !ActiveKeymap.Recording && shortcut.Gesture is { Key: not Key.None } gesture ? gesture : null;
        ActiveKeymap.Changed += Update;
        if (owner is not null) owner.Closed += (_, _) => ActiveKeymap.Changed -= Update;
        return item;
    }

    private static NativeMenuItem Item(string header, KeyGesture gesture, Action action) => Item(header, gesture.Key, gesture.KeyModifiers, action);

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