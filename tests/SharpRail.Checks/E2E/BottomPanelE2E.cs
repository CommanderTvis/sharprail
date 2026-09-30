using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SharpRail.UI.Docking;
using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.TerminalsE2E;

namespace SharpRail.Checks.E2E;

// Translates upstream e2e/bottom-panel.spec.ts. Reload means a new window over the same profile.
internal static class BottomPanelE2E
{
    private static readonly RawInputModifiers Command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "bottom-panel-git"));
        SquareActions(root);
        InitialTerminalGroup(root);
        HiddenFrameReservesTerminal(root);
        ClosedInitialTerminalStaysClosed(root);
        ToggleFromTerminal(root);
        AlignmentsAndHeightPersist(root);
        ExcludedCorners(root);
        NarrowAlignments(root);
        NarrowSideResizing(root);
        FinalResourceRetainsGroups(root);
        AlignmentsDuringResize(root);
        BottomGroups(root);
        NarrowBottomGroups(root);
        WindowLocalBottom(root);
    }

    private static Rect Box(E2eWorkspace app, Control control) => new(control.TranslatePoint(default, app.Window)!.Value, control.Bounds.Size);
    private static Control Panel(E2eWorkspace app) => app.Find<Border>("AuxiliaryRegion_bottom");
    private static Control Center(E2eWorkspace app) => app.Find<Border>("CenterRegion");
    private static Control Side(E2eWorkspace app, string side) => app.Find<Border>("AuxiliaryRegion_" + side);
    private static Control Workbench(E2eWorkspace app) => app.Find<DockSurface>("WorkspaceWorkbench");
    private static bool Shown(E2eWorkspace app) => app.Window.Layout.State.BottomVisible && Panel(app).IsVisible;
    private static DockGroup[] Groups(E2eWorkspace app) => app.Window.Layout.State.Groups.Where(group => group.Region == "bottom").ToArray();
    private static double Height(E2eWorkspace app) => Panel(app).Bounds.Height;

    private static void HorizontalSpan(E2eWorkspace app, Func<Control> surface, Control start, Control end) => Until(() =>
    {
        app.Window.UpdateLayout();
        var (box, first, last) = (Box(app, surface()), Box(app, start), Box(app, end));
        return Math.Max(Math.Abs(box.X - first.X), Math.Abs(box.Right - last.Right)) < 4;
    });

    private static void VerticalSpan(E2eWorkspace app, Control surface, Control reference) => Until(() =>
    {
        app.Window.UpdateLayout();
        var (box, other) = (Box(app, surface), Box(app, reference));
        return Math.Max(Math.Abs(box.Y - other.Y), Math.Abs(box.Bottom - other.Bottom)) < 4;
    });

    private static void Align(E2eWorkspace app, string label)
    {
        var button = app.Find<Button>("BottomAlignment");
        app.Click(button); Until(() => button.ContextMenu!.IsOpen);
        app.Click(button.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label)), freshGesture: false);
        Until(() => !button.ContextMenu!.IsOpen); Settle(100);
    }

    private static void ToggleBottom(TopLevel target)
    {
        target.KeyPress(Key.J, Command | RawInputModifiers.Shift, PhysicalKey.J, null);
        target.KeyRelease(Key.J, Command | RawInputModifiers.Shift, PhysicalKey.J, null); Settle();
    }

    private static void Drag(E2eWorkspace app, Control handle, Vector offset, int steps = 12, bool release = true)
    {
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        for (var step = 1; step <= steps; step++) app.Window.MouseMove(start + offset * step / steps);
        if (release) { app.Window.MouseUp(start + offset, MouseButton.Left); Settle(); }
    }

    private static void Press(Key key, PhysicalKey physical, RawInputModifiers modifiers, TopLevel target)
    {
        target.KeyPress(key, modifiers, physical, null);
        target.KeyRelease(key, modifiers, physical, null); Settle(100);
    }

    private static E2eWorkspace Default(string directory) => new(directory, openFiles: false);

    private static string Repository(string root, string name) => IsolatedGit.Repository(Path.Combine(root, name));

    private static void Menu(E2eWorkspace app, string tab, string title) => app.ContextAction(app.Find<Button>("Tab_" + tab), title);

    private static void SquareActions(string root)
    {
        using var app = Default(Repository(root, "bottom-square"));
        void Square(Control control) => Require(Math.Abs(control.Bounds.Width - 32) < .5 && Math.Abs(control.Bounds.Width - control.Bounds.Height) <= 1,
            $"Panel-header action {control.Name} must be a 32-pixel square.");
        var right = app.Window.Layout.State.Groups.First(group => group.Region == "right").Id;
        var bottom = Groups(app).Single().Id;
        foreach (var name in new[] { "NewTerminal_" + app.Center, "NewTerminal_" + bottom, "NewTerminal_" + right, "FoldRestore_" + right, "BottomAlignment" })
            Square(app.Find<Button>(name));
        var terminal = TerminalTabs(app).Single();
        app.ContextAction(app.Find<Button>("Tab_" + terminal.Id.Replace(':', '_')), "New bottom group at right");
        Until(() => Groups(app).Length == 2);
        foreach (var group in Groups(app)) Square(app.Find<Button>("FoldRestore_" + group.Id));
        app.Click(app.Find<Button>("Tab_files"));
        foreach (var name in new[] { "README.md", "notes.txt", "LINKS.md" }) app.Open(name, true);
        app.Window.Width = 620; Settle();
        Until(() => app.Find<Button>("TabOverflow_" + app.Center).IsVisible);
        Square(app.Find<Button>("TabOverflow_" + app.Center));
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: full-height panel-header actions stay square");
    }

    private static void InitialTerminalGroup(string root)
    {
        using var app = Default(Repository(root, "bottom-initial"));
        Ready(app, TerminalTabs(app).Single());
        Require(app.Window.Layout.State.BottomAlignment == "center" && Groups(app).Length == 1, "A new workspace must start with one centered bottom group.");
        var bottom = Groups(app).Single().Id;
        var tab = app.Window.Layout.Tabs(bottom).Single();
        var strip = app.Find<Border>("TabStrip_" + bottom);
        Require(tab is { Kind: "terminal", Title: "Terminal 1" } &&
            strip.GetLogicalDescendants().OfType<Control>().Count(control => ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationControlType() == AutomationControlType.Tab) == 1,
            "The bottom group must expose one tab list holding Terminal 1.");
        var button = app.Find<Button>("Tab_" + tab.Id.Replace(':', '_'));
        var peer = ControlAutomationPeer.CreatePeerForElement(button)!;
        Require(((ISelectionItemProvider)peer).IsSelected && ReferenceEquals(ControlAutomationPeer.CreatePeerForElement(app.Find<Border>("DockBody_" + bottom))!.GetLabeledBy(), peer),
            "Terminal 1 must be selected and label the bottom tab panel.");
        var separator = ControlAutomationPeer.CreatePeerForElement(app.Find<ResizeHandle>("bottomSeparator"))!;
        Require(separator.GetAutomationControlType() == AutomationControlType.Separator && separator.GetName() == "Horizontal pane separator",
            "The bottom resize handle must be a horizontal separator.");
        Until(() => Math.Abs(Height(app) / Workbench(app).Bounds.Height - .3) < .05);
        app.Find<Button>("Tab_changes").Focus();
        Press(Key.F6, PhysicalKey.F6, RawInputModifiers.Control, app.Window);
        Until(() => button.IsFocused);
        Press(Key.F6, PhysicalKey.F6, RawInputModifiers.Control | RawInputModifiers.Shift, app.Window);
        Until(() => app.Find<Button>("Tab_changes").IsFocused);
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: a new workspace starts with one accessible terminal group in a 30% bottom panel");
    }

    private static void HiddenFrameReservesTerminal(string root)
    {
        var directory = Repository(root, "bottom-hidden-frame");
        string workspace;
        using (var app = Default(directory))
        {
            app.Window.Layout.ApplyPreset(DockState.Preset("focus")); Settle();
            Require(!Shown(app), "The Focus preset must hide the bottom panel.");
            Press(Key.B, PhysicalKey.B, Command, app.Window);
            workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
            var tabs = TerminalTabs(app);
            Require(!Shown(app) && tabs.Length == 1 && tabs[0].Title == "Terminal 1" && !app.Window.GetVisualDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().Any() &&
                app.Terminals.StartedIn(workspace) == 0, "A hidden frame must reserve the workspace's terminal without starting it.");
        }
        using (var app = Default(directory))
        {
            WorkspaceTabsE2E.Switch(app, workspace, "workspace-1");
            Require(!Shown(app) && app.Terminals.StartedIn(workspace) == 0, "Reloading must keep the bottom hidden and the terminal unattached.");
            ToggleBottom(app.Window);
            Ready(app, TerminalTabs(app).Single());
            Require(TerminalTabs(app).Length == 1 && app.Terminals.StartedIn(workspace) == 1, "Showing the bottom must attach its one reserved terminal.");
        }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: a hidden local frame keeps the host terminal reserved without attaching until shown");
    }

    private static void ClosedInitialTerminalStaysClosed(string root)
    {
        var directory = Repository(root, "bottom-closed-initial");
        string workspace;
        using (var app = Default(directory))
        {
            workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
            var tab = TerminalTabs(app).Single();
            Ready(app, tab);
            CloseTab(app, tab);
            Until(() => TerminalTabs(app).Length == 0);
        }
        using (var app = Default(directory))
        {
            WorkspaceTabsE2E.Switch(app, workspace, "workspace-1");
            Settle();
            Require(Shown(app) && app.Find<Button>("NewTerminal_" + Groups(app).Single().Id).IsEffectivelyVisible &&
                TerminalTabs(app).Length == 0 && app.Terminals.StartedIn(workspace) == 0,
                "A workspace whose initial terminal was closed must not recreate it.");
        }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: a completed initial-terminal handshake never recreates a terminal after explicit close");
    }

    // A reload is a new window of the same app, over the same profile and terminal host.
    private static void ToggleFromTerminal(string root)
    {
        var directory = Repository(root, "bottom-toggle");
        var terminals = new E2eTerminals();
        try
        {
            string tabId;
            using (var app = new E2eWorkspace(directory, openFiles: false, terminals: terminals))
            {
                var tab = TerminalTabs(app).Single();
                tabId = tab.Id;
                var terminal = Ready(app, tab);
                var view = View(app, tab);
                terminal.Run("printf 'bottom-panel-%s\\n' running-marker");
                Expect(terminal, "bottom-panel-running-marker");
                terminal.FocusTerminal();
                Until(() => terminal.View.IsKeyboardFocusWithin);
                ToggleBottom(app.Window);
                Require(!Shown(app) && !Presented(app, view), "Mod+Shift+J from the terminal must hide the bottom panel.");
                ToggleBottom(app.Window);
                Require(ReferenceEquals(Ready(app, tab), terminal) && terminal.Text.Contains("bottom-panel-running-marker", StringComparison.Ordinal),
                    "Showing the panel again must present the same shell.");
                terminal.FocusTerminal();
                Until(() => terminal.View.IsKeyboardFocusWithin);
                ToggleBottom(app.Window);
                Require(!Shown(app), "Mod+Shift+J from the terminal must hide the bottom panel again.");
            }
            using (var app = new E2eWorkspace(directory, openFiles: false, terminals: terminals))
            {
                Settle();
                Require(!Shown(app), "A reload must keep the bottom panel hidden.");
                ToggleBottom(app.Window);
                var tab = TerminalTabs(app).Single();
                var terminal = Ready(app, tab);
                Require(tab.Id == tabId && terminal.Text.Contains("bottom-panel-running-marker", StringComparison.Ordinal) &&
                    terminals.StartedIn(app.Root) == 1, "A reload must reattach the same PTY and show its earlier output.");
                app.Window.ShowSettings();
                Until(() => app.Window.OwnedWindows.OfType<SharpRail.UI.Panels.SettingsWindow>().Any());
                var settings = app.Window.OwnedWindows.OfType<SharpRail.UI.Panels.SettingsWindow>().Single();
                settings.Focus();
                ToggleBottom(settings);
                Require(Shown(app), "Mod+Shift+J must not act behind a modal dialog.");
                settings.Close(); Settle();
                app.Find<Button>("Tab_changes").Focus();
                ToggleBottom(app.Window);
                Require(!Shown(app), "Mod+Shift+J must work again after the dialog closes.");
            }
        }
        finally { terminals.Quit(); }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: Mod+Shift+J works from xterm, preserves its PTY through hide and reload, and is modal-aware");
    }

    private static void AlignmentsAndHeightPersist(string root)
    {
        var directory = Repository(root, "bottom-alignments");
        double resized;
        using (var app = Default(directory))
        {
            HorizontalSpan(app, () => Panel(app), Center(app), Center(app));
            var button = app.Find<Button>("BottomAlignment");
            button.Focus();
            Press(Key.Enter, PhysicalKey.Enter, RawInputModifiers.None, app.Window);
            Until(() => button.ContextMenu!.IsOpen);
            var items = button.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            Until(() => items[0].IsFocused);
            Require(Equals(items[0].Header, "Below center"), "The alignment menu must open on Below center.");
            Press(Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None, TopLevel.GetTopLevel(items[0])!);
            Until(() => items[1].IsFocused && Equals(items[1].Header, "Below center and left"));
            Press(Key.Enter, PhysicalKey.Enter, RawInputModifiers.None, TopLevel.GetTopLevel(items[1])!);
            Until(() => app.Window.Layout.State.BottomAlignment == "center-left");
            Align(app, "Below center");
            var before = Height(app);
            Drag(app, app.Find<ResizeHandle>("bottomSeparator"), new Vector(0, -90));
            Until(() => Height(app) > before + 55);
            resized = Height(app);
            Align(app, "Below center and left");
            HorizontalSpan(app, () => Panel(app), Side(app, "left"), Center(app));
            Align(app, "Below center and right");
            HorizontalSpan(app, () => Panel(app), Center(app), Side(app, "right"));
            Align(app, "Full width");
            HorizontalSpan(app, () => Panel(app), Side(app, "left"), Side(app, "right"));
            Align(app, "Below center and left");
        }
        using (var app = Default(directory))
        {
            Require(app.Window.Layout.State.BottomAlignment == "center-left", "The alignment must survive reload.");
            Until(() => Math.Abs(Height(app) - resized) < 24);
            var keyboardBefore = Height(app);
            app.Find<ResizeHandle>("bottomSeparator").Focus();
            Press(Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.None, app.Window);
            Until(() => Height(app) > keyboardBefore);
            ToggleBottom(app.Window);
            var tab = app.Find<Button>("Tab_files");
            var start = tab.TranslatePoint(new Point(24, tab.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
            app.Window.MouseMove(start + new Vector(12, 8)); Settle();
            var overlay = Workbench(app).GetVisualChildren().OfType<Canvas>().Single();
            var zone = overlay.Children.OfType<Border>().Single(hint => hint.Height == 24 && hint.BorderThickness.Top >= 1 && hint.BorderThickness.Left == 0);
            HorizontalSpan(app, () => zone, Side(app, "left"), Center(app));
            app.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            app.Window.MouseUp(start + new Vector(12, 8), MouseButton.Left); Settle();
            ToggleBottom(app.Window);
            HorizontalSpan(app, () => Panel(app), Side(app, "left"), Center(app));
        }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: bottom height, all alignments, and keyboard resizing persist across reload");
    }

    private static void ExcludedCorners(string root)
    {
        using var app = Default(Repository(root, "bottom-corners"));
        foreach (var (name, leftOwns, rightOwns) in new[]
        {
            ("Below center", true, true), ("Below center and left", false, true),
            ("Below center and right", true, false), ("Full width", false, false)
        })
        {
            Align(app, name);
            for (var pass = 0; pass < 2; pass++)
            {
                VerticalSpan(app, Side(app, "left"), leftOwns ? Workbench(app) : Center(app));
                VerticalSpan(app, Side(app, "right"), rightOwns ? Workbench(app) : Center(app));
                ToggleBottom(app.Window);
            }
        }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: bottom alignments give excluded lower corners to the actual side panels");
    }

    private static void NarrowAlignments(string root)
    {
        using var app = Default(Repository(root, "bottom-narrow-alignments"));
        app.Window.Width = 390; app.Window.Height = 844; Settle();
        Align(app, "Below center");
        HorizontalSpan(app, () => Panel(app), Center(app), Center(app));
        Align(app, "Below center and left");
        HorizontalSpan(app, () => Panel(app), Side(app, "left"), Center(app));
        Align(app, "Below center and right");
        HorizontalSpan(app, () => Panel(app), Center(app), Side(app, "right"));
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: bottom alignments follow locally compressed side geometry at narrow widths");
    }

    private static void NarrowSideResizing(string root)
    {
        using var app = Default(Repository(root, "bottom-narrow-sides"));
        (double Left, double Right) Durable() => (app.Window.Layout.State.LeftWidth, app.Window.Layout.State.RightWidth);
        (double Left, double Right) Local()
        {
            var width = Workbench(app).Bounds.Width;
            return (Side(app, "left").Bounds.Width / width, Side(app, "right").Bounds.Width / width);
        }
        foreach (var (alignment, side, keyboard) in new[]
        {
            ("Full width", "left", false), ("Full width", "right", true), ("Below center", "left", true), ("Below center", "right", false)
        })
        {
            app.Window.Width = 1200; app.Window.Height = 844; Settle();
            // Each scenario starts from the default widths so the narrow viewport compresses both sides.
            app.Window.Layout.Geometry(state => (state.LeftWidth, state.RightWidth) = (.18, .28));
            Align(app, alignment);
            var before = Durable();
            app.Window.Width = 560; Settle();
            Until(() => Math.Max(Math.Abs(Local().Left - before.Left), Math.Abs(Local().Right - before.Right)) > .0001);
            var handle = app.Find<ResizeHandle>(side + "Separator");
            if (keyboard)
            {
                handle.Focus();
                Press(Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None, app.Window);
            }
            else Drag(app, handle, new Vector(side == "left" ? -20 : 20, 0));
            app.Window.Width = 1200; Settle();
            var after = Durable();
            Require(Math.Abs((side == "left" ? after.Left : after.Right) - (side == "left" ? before.Left : before.Right)) > .001,
                $"Resizing the {side} side at a narrow width must persist that side.");
            Require(Math.Abs((side == "left" ? after.Right : after.Left) - (side == "left" ? before.Right : before.Left)) < .005,
                $"Resizing the {side} side must not persist the untouched side's compressed width.");
        }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: narrow side resizing persists only the side whose separator moved");
    }

    private static void FinalResourceRetainsGroups(string root)
    {
        using var app = Default(Repository(root, "bottom-final-resource"));
        var tab = TerminalTabs(app).Single();
        Ready(app, tab);
        app.ContextAction(app.Find<Button>("Tab_" + tab.Id.Replace(':', '_')), "New bottom group at right");
        Until(() => Groups(app).Length == 2);
        CloseTab(app, tab);
        Until(() => TerminalTabs(app).Length == 0);
        Require(Groups(app).Length == 2 && Groups(app).All(group => app.Find<Button>("NewTerminal_" + group.Id).IsEffectivelyVisible),
            "Closing the final bottom resource must retain both groups with their New terminal actions.");
        app.Click(app.Find<Button>("RemoveGroup_" + Groups(app)[0].Id));
        Until(() => Groups(app).Length == 1);
        app.Click(app.Find<Button>("RemoveGroup_" + Groups(app)[0].Id));
        Until(() => Groups(app).Length == 0);
        Require(!Shown(app) && !Workbench(app).GetVisualChildren().OfType<Canvas>().Single().Children.Any(),
            "Removing the last bottom group must remove the panel without leaving a drop zone.");
        Until(() => app.Window.Layout.View.FocusedGroup == app.Center && app.Window.FocusManager?.GetFocusedElement() is Control focused &&
            focused.GetVisualAncestors().Prepend(focused).OfType<Control>().Any(control =>
                control.Name == "TabStrip_" + app.Center || control.Name == "DockBody_" + app.Center || control.Name == "GroupHeader_" + app.Center));
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: closing a final bottom resource retains its frame groups until explicit removal");
    }

    private static void AlignmentsDuringResize(string root)
    {
        using var app = Default(Repository(root, "bottom-resize-gesture"));
        foreach (var (name, start, end, offset) in new[]
        {
            ("Below center", "center", "center", 60d), ("Below center and left", "left", "center", -50d), ("Below center and right", "center", "right", 50d)
        })
        {
            Align(app, name);
            Control Region(string region) => region == "center" ? Center(app) : Side(app, region);
            Drag(app, app.Find<ResizeHandle>("leftSeparator"), new Vector(offset, 0), steps: 8, release: false);
            HorizontalSpan(app, () => Panel(app), Region(start), Region(end));
            var point = app.Find<ResizeHandle>("leftSeparator").TranslatePoint(default, app.Window)!.Value;
            app.Window.MouseUp(point, MouseButton.Left); Settle();
        }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: bottom alignments follow side geometry while a resize gesture is in progress");
    }

    private static void BottomGroups(string root)
    {
        using var app = Default(Repository(root, "bottom-groups"));
        Ready(app, TerminalTabs(app).Single());
        Menu(app, "changes", "New bottom group at right");
        Until(() => Groups(app).Length == 2);
        string Titles(int index) => string.Join(",", app.Window.Layout.Tabs(Groups(app)[index].Id).Select(tab => tab.Title));
        Require(Titles(0) == "Terminal 1" && Titles(1) == "Changes", "Bottom groups must arrange left to right.");
        var changes = app.Find<Button>("Tab_changes");
        app.ContextAction(changes, "Move to pane");
        var move = changes.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane"));
        Until(() => move.IsSubMenuOpen);
        app.Click(move.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "bottom: Terminal 1")), freshGesture: false);
        Until(() => Titles(0) == "Terminal 1,Changes");
        Require(Groups(app).Length == 2, "Moving the tool out must retain its emptied group.");
        app.Click(app.Find<Button>("RemoveGroup_" + Groups(app)[1].Id));
        Until(() => Groups(app).Length == 1);
        Menu(app, "changes", "New bottom group at right");
        Until(() => Groups(app).Length == 2);
        var (first, second) = (Groups(app)[0].Id, Groups(app)[1].Id);
        double Width(string id) => app.Find<Border>("TabStrip_" + id).Bounds.Width;
        var firstBefore = Width(first);
        var handle = app.Find<ResizeHandle>("AuxiliarySeparator_" + first + "_" + second);
        Require(ControlAutomationPeer.CreatePeerForElement(handle)!.GetName() == "Vertical pane separator", "Bottom group separators must be vertical.");
        Drag(app, handle, new Vector(90, 0));
        Until(() => Width(first) > firstBefore + 50);
        app.Click(app.Find<Button>("FoldRestore_" + first));
        Until(() => app.Window.Layout.Group(first).Folded && app.Find<Button>("FoldRestore_" + first).IsFocused);
        var folded = app.Find<Control>("FoldedDockGroup_" + first);
        Require(Math.Abs(folded.Bounds.Width - 27) < 1 && !app.Window.GetVisualDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().Any(),
            "A folded bottom group must shrink to 27 pixels and unmount its terminal.");
        app.Find<Button>("Tab_changes").Focus();
        Press(Key.F6, PhysicalKey.F6, RawInputModifiers.Control | RawInputModifiers.Shift, app.Window);
        Until(() => app.Find<Button>("FoldRestore_" + first).IsFocused);
        Press(Key.Space, PhysicalKey.Space, RawInputModifiers.None, app.Window);
        Until(() => !app.Window.Layout.Group(first).Folded);
        var terminal = TerminalTabs(app).Single();
        Until(() => app.Find<Button>("Tab_" + terminal.Id.Replace(':', '_')).IsFocused);
        Ready(app, terminal);
        Menu(app, "files", "New bottom group at left");
        Until(() => Groups(app).Length == 3);
        Require(Titles(0) == "Files", "A new left bottom group must come first.");
        var specs = app.Find<Button>("Tab_specs");
        app.Click(specs, mouseButton: MouseButton.Right); Until(() => specs.ContextMenu!.IsOpen);
        foreach (var direction in new[] { "left", "right" })
            Require(specs.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, $"New bottom group at {direction} — limited to 3")) is { IsEnabled: false },
                "The bottom group limit must disable further bottom groups.");
        specs.ContextMenu!.Close();
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: bottom groups arrange left-to-right, resize, fold to 27px, restore, and enforce their own limit");
    }

    private static void NarrowBottomGroups(string root)
    {
        var directory = Repository(root, "bottom-narrow-groups");
        string[] ids;
        using (var app = Default(directory))
        {
            Menu(app, "changes", "New bottom group at right"); Until(() => Groups(app).Length == 2);
            Menu(app, "files", "New bottom group at right"); Until(() => Groups(app).Length == 3);
            ids = Groups(app).Select(group => group.Id).ToArray();
            Align(app, "Full width");
            app.Window.Width = 390; app.Window.Height = 844; Settle();
            Require(Groups(app).Length == 3 && app.Window.Layout.State.BottomAlignment == "full", "Compression must keep the bottom topology.");
            var row = Box(app, Panel(app));
            foreach (var group in Groups(app))
            {
                var box = Box(app, app.Find<Border>("TabStrip_" + group.Id));
                Require(box.Width > 0 && box.X >= row.X - 1 && box.Right <= row.Right + 1 && !group.Folded,
                    "Each bottom group must stay inside the narrow row without folding.");
            }
            Require(Groups(app).Select(group => group.Id).SequenceEqual(ids), "Compression must not rewrite bottom group identities.");
        }
        using (var app = Default(directory))
            Require(Groups(app).Length == 3 && app.Window.Layout.State.BottomAlignment == "full" && Groups(app).Select(group => group.Id).SequenceEqual(ids),
                "Reloading must restore the bottom topology.");
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: a narrow viewport locally compresses bottom groups without rewriting their topology");
    }

    private static void WindowLocalBottom(string root)
    {
        var directory = Repository(root, "bottom-window-local");
        var peerProfile = directory + "-peer-profile";
        using var app = Default(directory);
        using (var peer = new E2eWorkspace(directory, openFiles: false, profileRoot: peerProfile))
        {
            Require(Shown(peer), "The peer window must show its bottom panel.");
            app.Window.Activate(); app.Find<Button>("Tab_changes").Focus();
            Align(app, "Full width");
            Require(peer.Window.Layout.State.BottomAlignment == "center", "Alignment must stay local to its window.");
            ToggleBottom(app.Window);
            Require(!Shown(app) && Shown(peer), "Visibility must stay local to its window.");
            peer.Window.Activate(); peer.Find<Button>("Tab_changes").Focus();
            Align(peer, "Below center and left");
        }
        using (var peer = new E2eWorkspace(directory, openFiles: false, profileRoot: peerProfile))
            Require(peer.Window.Layout.State.BottomAlignment == "center-left" && !Shown(app),
                "A reloaded window must restore its own alignment without changing the other window.");
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: bottom visibility and alignment stay local to each window and survive its reload");
    }
}
