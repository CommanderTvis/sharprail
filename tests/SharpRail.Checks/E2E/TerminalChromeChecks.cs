using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class TerminalChromeChecks
{
    internal static void Run(string root)
    {
        SideMenu(root);
        TabTooltip(root);
        TabHover(root);
        RegionCommands(root);
        SideTerminal(root);
        var directory = Path.Combine(root, "terminal-chrome");
        using var app = new E2eWorkspace(directory, prepare: _ => WorkspaceTabsE2E.Git(directory, "init", "-q", "-b", "main"));
        var terminalGroup = app.Window.Layout.Group(app.Center);
        app.Window.Layout.NewTerminal(terminalGroup.Id);
        app.Click(app.Find<Button>("NewTerminal_" + terminalGroup.Id));
        Until(() => app.Window.Layout.Tabs(terminalGroup.Id).Count(tab => tab.Kind == "terminal") == 2);
        var terminal = app.Window.Layout.Tabs(terminalGroup.Id).Last();
        Require(app.Window.Layout.Selected(terminalGroup.Id)?.Id == terminal.Id && terminal.Title == "Terminal 2",
            "The pane menu must add and select a second terminal beside the first explicit terminal.");
        var name = terminal.Id.Replace(':', '_');
        Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "TerminalSurface_" + name));
        var body = app.Find<Control>("TerminalSurface_" + name);
        var tab = app.Find<Button>("Tab_" + name);
        // Tools and terminals use separate panes, so the terminal moves to an empty right pane, which the menu names.
        var specs = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tool => tool.Id == DockState.SpecsTool));
        Require(app.Window.Layout.NewGroup(specs.Id, "after"), "An empty right pane could not be created.");
        Settle();
        var side = app.Window.Layout.State.Groups.Single(group => group.Region == "right" && group.Tools.Count == 0);
        tab = app.Find<Button>("Tab_" + name);
        var menu = tab.ContextMenu!;
        app.ContextAction(tab, "Move to pane");
        var move = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane"));
        Until(() => move.IsSubMenuOpen);
        Require(!move.Items.OfType<MenuItem>().Any(item => item.IsVisible && Equals(item.Header, "right pane (Specs, Files)")),
            "A terminal must not be offered a tool pane.");
        app.Click(move.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "right pane (empty)")), freshGesture: false);
        Until(() => app.Window.Layout.Tabs(side.Id).Any(item => item.Id == terminal.Id));
        Require(ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "Moving a terminal must retain its one surface.");
        app.Click(app.Find<Button>("HidePane_" + side.Id));
        Require(!body.GetVisualAncestors().Contains(app.Window), "A folded pane must not present its terminal surface.");
        app.Click(app.Find<Button>("RailPane_" + side.Id));
        Until(() => body.GetVisualAncestors().Contains(app.Window));
        app.ContextAction(app.Find<Button>("Tab_" + name), "Close");
        Require(!app.Window.Layout.State.Workspaces.Values.SelectMany(view => view.Documents.Values).SelectMany(tabs => tabs).Any(item => item.Id == terminal.Id),
            "Closing a terminal must remove its resource placement.");
        Console.WriteLine("PASS terminal chrome creation, cross-region move, surface identity, fold/restore and close");
    }

    private static void SideTerminal(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-side-group"));
        var sourceGroup = app.Window.Layout.State.Groups.First(group => group.Region == "right");
        var originalRight = app.Window.Layout.State.Groups.Where(group => group.Region == "right").Select(group => group.Id).ToArray();
        var originalTools = sourceGroup.Tools.Select(tab => tab.Id).ToArray();
        app.Window.Layout.NewTerminal(sourceGroup.Id);
        var createdGroup = app.Window.Layout.View.FocusedGroup;
        var right = app.Window.Layout.State.Groups.Where(group => group.Region == "right").Select(group => group.Id).ToArray();
        Require(right.Length == originalRight.Length + 1 && createdGroup != sourceGroup.Id &&
            Array.IndexOf(right, createdGroup) == Array.IndexOf(right, sourceGroup.Id) + 1 &&
            app.Window.Layout.Group(sourceGroup.Id).Tools.Select(tab => tab.Id).SequenceEqual(originalTools) &&
            app.Window.Layout.Tabs(sourceGroup.Id).All(tab => tab.IsTool),
            "A terminal launch from a tool pane must create exactly one separate pane directly below without moving its tools.");
        var id = app.Window.Layout.Tabs(createdGroup).Single(tab => tab.Kind == "terminal").Id;
        var name = id.Replace(':', '_');
        Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "TerminalSurface_" + name));
        var body = app.Find<Control>("TerminalSurface_" + name);
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        app.Window.KeyPress(Key.B, command, PhysicalKey.B, null); Settle();
        var rail = app.Find<Border>("leftRail");
        Require(rail.IsVisible, "Hiding the left side must expose its drop rail.");
        var terminal = app.Find<Button>("Tab_" + name);
        var start = terminal.TranslatePoint(new Point(24, terminal.Bounds.Height / 2), app.Window)!.Value;
        var target = rail.TranslatePoint(new Point(rail.Bounds.Width / 2, rail.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(-12, 8)); Settle();
        Require(app.Find<DockSurface>("WorkspaceWorkbench").IsDragging, "Terminal pointer movement must start a drag.");
        app.Window.MouseMove(target); Settle(); app.Window.MouseUp(target, MouseButton.Left); Settle();
        var left = app.Window.Layout.State.Groups.Where(group => group.Region == "left").ToArray();
        Require(left.Length == 2 && app.Window.Layout.State.Groups.Count(group => group.Region == "right") == originalRight.Length + 1 &&
            app.Window.Layout.State.Groups.Any(group => group.Id == createdGroup && !group.Folded) &&
            app.Window.Layout.Tabs(createdGroup).Count == 0 &&
            app.Window.Layout.Group(sourceGroup.Id).Tools.Select(tab => tab.Id).SequenceEqual(originalTools) &&
            app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Count(tab => tab.Id == id) == 1 &&
            ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "The hidden-side drop must create one left pane, retain the empty source pane and its neighbouring tools, and retain one terminal body.");
        terminal = app.Find<Button>("Tab_" + name);
        app.Click(terminal, mouseButton: MouseButton.Right); Until(() => terminal.ContextMenu!.IsOpen);
        Require(app.SplitItem(terminal, "Left").IsEnabled, "Terminal side creation must be enabled.");
        terminal.ContextMenu!.Close();
        double Height(string group) => ((Grid)app.Find<Grid>("GroupHeader_" + group).Parent!.Parent!).Bounds.Height;
        var projects = left.Single(group => group.Tools.Any(tab => tab.Id == "projects")).Id;
        var terminalGroup = left.Single(group => app.Window.Layout.Tabs(group.Id).Any(tab => tab.Id == id)).Id;
        var before = Height(projects);
        var handle = app.Find<ResizeHandle>("AuxiliarySeparator_" + left[0].Id + "_" + left[1].Id);
        var handlePoint = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseDown(handlePoint, MouseButton.Left); app.Window.MouseMove(handlePoint + new Vector(0, 80));
        app.Window.MouseUp(handlePoint + new Vector(0, 80), MouseButton.Left); Settle();
        Require(Height(projects) > before + 40, "Resizing the side stack must enlarge Projects.");
        bool Folded(string group) => app.Window.Layout.Group(group).Folded &&
            !app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "GroupHeader_" + group);
        app.Click(app.Find<Button>("HidePane_" + terminalGroup));
        Require(Folded(terminalGroup) && !body.GetVisualAncestors().Contains(app.Window),
            "Folding must leave the terminal pane only on its rail and remove its body from the visible tree.");
        app.Find<Button>("RailPane_" + terminalGroup).Focus();
        app.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        app.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null); Settle();
        Require(!app.Window.Layout.Group(terminalGroup).Folded && ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "Space must restore the same terminal body.");
        foreach (var group in new[] { projects, terminalGroup }) app.Click(app.Find<Button>("HidePane_" + group));
        Require(new[] { projects, terminalGroup }.All(Folded), "Both side groups must fold independently onto the rail.");
        foreach (var entry in new[] { "Tab_projects", "RailPane_" + terminalGroup }) app.Click(app.Find<Button>(entry));
        Require(ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "Restoring both groups must retain the terminal body.");
        app.SplitAction(app.Find<Button>("Tab_files"), "Left");
        Require(app.Window.Layout.State.Groups.Count(group => group.Region == "left") == 3 &&
            app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Count(tab => tab.Id == "files") == 1,
            "The Files context action must create one new left group without duplicating its tab.");
        Console.WriteLine("PASS upstream layout.spec.ts: a terminal can move to its own side group; resize, fold, and visibility gate its one body");
    }

    private static void RegionCommands(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-region-commands"));
        var terminalGroup = app.Window.Layout.Group(app.Center);
        app.Window.Layout.NewTerminal(terminalGroup.Id);
        app.Click(app.Find<Button>("NewTerminal_" + terminalGroup.Id));
        Until(() => app.Window.Layout.Tabs(terminalGroup.Id).Count(tab => tab.Kind == "terminal") == 2);
        var id = app.Window.Layout.Tabs(terminalGroup.Id).Last().Id;
        var name = id.Replace(':', '_');
        Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "TerminalSurface_" + name));
        var body = app.Find<Control>("TerminalSurface_" + name);
        foreach (var region in new[] { "left", "right" })
        {
            var count = app.Window.Layout.State.Groups.Count(group => group.Region == region);
            app.SplitAction(app.Find<Button>("Tab_" + name), region == "left" ? "Left" : "Right");
            Until(() => app.Window.Layout.State.Groups.Count(group => group.Region == region) == count + 1);
            var groups = app.Window.Layout.State.Groups.Where(group => group.Region == region).ToArray();
            Require(app.Window.Layout.Tabs(groups[^1].Id).Single().Id == id &&
                app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Count(tab => tab.Id == id) == 1 &&
                ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)),
                "Region creation must append at the bottom and move the one terminal surface without copying it.");
        }
        app.Window.Layout.Geometry(state => state.SideLimit = state.Groups.Count(group => group.Region == "right"));
        var tab = app.Find<Button>("Tab_" + name);
        app.Click(tab, mouseButton: MouseButton.Right); Until(() => tab.ContextMenu!.IsOpen);
        Require(!app.SplitItem(tab, "Right").IsEnabled && ((string)app.SplitItem(tab, "Right").Header!).EndsWith($" — limited to {app.Window.Layout.State.SideLimit}", StringComparison.Ordinal) &&
            !tab.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Header is string title && title.StartsWith("New ", StringComparison.Ordinal)),
            "Region creation must disable its commands and explain the configured group limit.");
        tab.ContextMenu!.Close();
        Console.WriteLine("PASS terminal region-edge commands, retained surface identity and group limits");
    }

    private static void TabTooltip(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "tab-tooltip"));
        // Projects tabs form a vertical list; their tooltips open beside the tab bounds.
        app.Open("README.md", true);
        var tab = app.Tab("README.md");
        var tip = (ToolTip)ToolTip.GetTip(tab)!;
        var bottom = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height), app.Window)!.Value;
        app.Window.MouseMove(bottom - new Vector(0, 2));
        Until(() => ToolTip.GetIsOpen(tab)); Settle();
        var tooltipRoot = TopLevel.GetTopLevel(tip)!;
        var position = tip.TranslatePoint(default, tooltipRoot)!.Value;
        var screen = tooltipRoot.PointToScreen(position);
        var tabRight = app.Window.PointToScreen(tab.TranslatePoint(new Point(tab.Bounds.Width, 0), app.Window)!.Value);
        Require(ToolTip.GetPlacement(tab) == PlacementMode.Right && screen.X >= tabRight.X && screen.X <= tabRight.X + 8,
            "The tooltip must sit immediately beside the Projects tab, independently of the pointer position.");
        var expectedX = screen.X;
        app.Window.MouseMove(tab.TranslatePoint(new Point(6, 2), app.Window)!.Value); Settle();
        Require(tooltipRoot.PointToScreen(tip.TranslatePoint(default, tooltipRoot)!.Value).X == expectedX,
            "Moving across a tab must not move its tooltip horizontally with the pointer.");
        Console.WriteLine("PASS tab tooltip follows the tab bounds rather than the pointer");
    }

    private static void TabHover(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "tab-hover"));
        var terminalGroup = app.Window.Layout.Group(app.Center);
        app.Window.Layout.NewTerminal(terminalGroup.Id);
        var first = app.Window.Layout.Tabs(terminalGroup.Id).Single();
        app.Window.Layout.NewTerminal(terminalGroup.Id); Settle();
        var name = first.Id.Replace(':', '_');
        var chrome = app.Find<Grid>("DockTab_" + name);
        var frame = chrome.Children.OfType<Border>().First();
        var button = app.Find<Button>("Tab_" + name);
        var close = chrome.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "CloseTab");
        var presenter = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .Single(item => item.Name == "PART_ContentPresenter");
        var original = Ui.Theme;
        foreach (var theme in SharpRail.UI.Rendering.Themes.All)
        {
            Ui.Apply(theme);
            app.Window.MouseMove(button.TranslatePoint(new Point(6, 6), app.Window)!.Value); Settle();
            Require(ReferenceEquals(frame.Background, Ui.Hover) &&
                presenter.Background is Avalonia.Media.ISolidColorBrush brush && brush.Color.A == 0,
                "Hover must paint the whole tab frame without a second label-only background.");
            app.Window.MouseMove(close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), app.Window)!.Value); Settle();
            Require(ReferenceEquals(frame.Background, Ui.Hover) && close.Opacity == 1,
                "Moving onto the close button must retain the whole-tab hover.");
            app.Window.MouseMove(new Point(2, 2)); Settle();
            Require(ReferenceEquals(frame.Background, Avalonia.Media.Brushes.Transparent),
                "Leaving an inactive tab must restore its normal background.");
        }
        Ui.Apply(original);
        app.Window.MouseMove(button.TranslatePoint(new Point(6, 6), app.Window)!.Value); Settle();
        app.Window.Layout.ApplyPreset(DockState.Preset("review")); Settle();
        Require(app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Any(tab => tab.Id == first.Id),
            "Changing layout under a hovered tab must retain the resource and process pointer exit safely.");
        Console.WriteLine("PASS whole-tab hover over label and close button in dark/light themes and layout changes");
    }

    private static void SideMenu(string root)
    {
        var directory = Path.Combine(root, "terminal-side-menu");
        using var app = new E2eWorkspace(directory, prepare: _ => WorkspaceTabsE2E.Git(directory, "init", "-q", "-b", "main"));
        var side = app.Window.Layout.State.Groups.First(group => group.Region == "right");
        Require(!app.Window.Layout.Tabs(side.Id).Any(tab => tab.Kind == "terminal"), "The Specs group must start without a terminal.");
        Require(!app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "NewTerminal_" + side.Id),
            "The Specs view must not show a terminal opener.");
        // Tools and terminals use separate panes: New terminal from a tool pane opens a terminal pane directly below it.
        Require(!app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "AddToGroup_" + side.Id),
            "Pane headers must not show an add button.");
        var add = app.Find<Grid>("GroupHeader_" + side.Id);
        add.ContextMenu!.Open(add); Until(() => add.ContextMenu!.IsOpen);
        add.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "NewTerminalBelow_" + side.Id).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        add.ContextMenu.Close();
        var right = app.Window.Layout.State.Groups.Where(group => group.Region == "right").ToList();
        var below = right[right.FindIndex(group => group.Id == side.Id) + 1];
        Until(() => app.Window.Layout.Tabs(below.Id).Count(tab => tab.Kind == "terminal") == 1);
        app.Click(app.Find<Button>("NewTerminal_" + below.Id));
        Until(() => app.Window.Layout.Tabs(below.Id).Count(tab => tab.Kind == "terminal") == 2);
        Require(!app.Window.Layout.State.Center.Leaves().SelectMany(app.Window.Layout.Tabs).Any(tab => tab.Kind == "terminal") &&
            app.Window.Layout.Tabs(side.Id).All(tab => tab.IsTool),
            "The side menu must create terminals in a terminal pane of its own side.");
        app.ContextAction(app.Find<Button>("Tab_projects"), "Close");
        app.ContextAction(app.Find<Button>("Tab_review"), "Close");
        add = app.Find<Grid>("GroupHeader_" + side.Id);
        add.ContextMenu!.Open(add); Until(() => add.ContextMenu!.IsOpen);
        var entries = add.ContextMenu!.Items.OfType<MenuItem>().ToArray();
        Require(entries.Any(item => Equals(item.Header, "Show Review")) && !entries.Any(item => Equals(item.Header, "Show Projects")),
            "A side menu must offer only its own side's hidden tools.");
        add.ContextMenu.Close();
        Console.WriteLine("PASS upstream layout.spec.ts: the side group menu shows tools for its own side and opens terminals in that group");
    }
}