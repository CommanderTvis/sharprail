using Avalonia;
using Avalonia.Controls;
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
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-chrome"));
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        app.Click(app.Find<Button>("NewTerminal_" + bottom.Id));
        Until(() => app.Window.Layout.Tabs(bottom.Id).Count(tab => tab.Kind == "terminal") == 2);
        var terminal = app.Window.Layout.Tabs(bottom.Id).Last();
        Require(app.Window.Layout.Selected(bottom.Id)?.Id == terminal.Id && terminal.Title == "Terminal 2",
            "The pane menu must add and select a second terminal beside the workspace's initial one.");
        var name = terminal.Id.Replace(':', '_');
        var body = app.Find<Control>("TerminalSurface_" + name);
        var tab = app.Find<Button>("Tab_" + name);
        var side = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tool => tool.Id == "specs"));
        var menu = tab.ContextMenu!;
        app.ContextAction(tab, "Move to pane");
        var move = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane"));
        Until(() => move.IsSubMenuOpen);
        app.Click(move.Items.OfType<MenuItem>().Single(item => Equals(item.Header, side.Region + ": " + app.Window.Layout.Selected(side.Id)!.Title)), freshGesture: false);
        Until(() => app.Window.Layout.Tabs(side.Id).Any(item => item.Id == terminal.Id));
        Require(ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "Moving a terminal must retain its one surface.");
        app.Click(app.Find<Button>("FoldRestore_" + side.Id));
        Require(!body.GetVisualAncestors().Contains(app.Window), "A folded pane must not present its terminal surface.");
        app.Click(app.Find<Button>("FoldRestore_" + side.Id));
        Until(() => body.GetVisualAncestors().Contains(app.Window));
        app.ContextAction(app.Find<Button>("Tab_" + name), "Close");
        Require(!app.Window.Layout.State.Workspaces.Values.SelectMany(view => view.Documents.Values).SelectMany(tabs => tabs).Any(item => item.Id == terminal.Id),
            "Closing a terminal must remove its resource placement.");
        Console.WriteLine("PASS terminal chrome creation, cross-region move, surface identity, fold/restore and close");
    }

    private static void SideTerminal(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-side-group"));
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        var id = app.Window.Layout.Tabs(bottom.Id).Single(tab => tab.Kind == "terminal").Id;
        var name = id.Replace(':', '_');
        var body = app.Find<Control>("TerminalSurface_" + name);
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        app.Window.KeyPress(Key.B, command, PhysicalKey.B, null); Settle();
        var rail = app.Find<Border>("leftHiddenSideRail");
        Require(rail.IsVisible, "Hiding the left side must expose its drop rail.");
        var terminal = app.Find<Button>("Tab_" + name);
        var start = terminal.TranslatePoint(new Point(24, terminal.Bounds.Height / 2), app.Window)!.Value;
        var target = rail.TranslatePoint(new Point(rail.Bounds.Width / 2, rail.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(-12, 8)); Settle();
        Require(app.Find<DockSurface>("WorkspaceWorkbench").IsDragging, "Terminal pointer movement must start a drag.");
        app.Window.MouseMove(target); Settle(); app.Window.MouseUp(target, MouseButton.Left); Settle();
        var left = app.Window.Layout.State.Groups.Where(group => group.Region == "left").ToArray();
        Require(left.Length == 2 && app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 2 &&
            app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Count(tab => tab.Id == id) == 1 &&
            ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "The hidden-side drop must create one group and retain one terminal body.");
        terminal = app.Find<Button>("Tab_" + name);
        app.Click(terminal, mouseButton: MouseButton.Right); Until(() => terminal.ContextMenu!.IsOpen);
        foreach (var title in new[] { "New left group at bottom", "New left group at top" })
            Require(terminal.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, title)).IsEnabled, "Terminal side creation must be enabled.");
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
        app.Click(app.Find<Button>("FoldRestore_" + terminalGroup));
        Require(app.Window.Layout.Group(terminalGroup).Folded && Math.Abs(Height(terminalGroup) - 27) < .5 && !body.GetVisualAncestors().Contains(app.Window),
            "Folding must reduce the terminal group to 27 pixels and remove its body from the visible tree.");
        app.Find<Button>("FoldRestore_" + terminalGroup).Focus();
        app.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        app.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null); Settle();
        Require(!app.Window.Layout.Group(terminalGroup).Folded && ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "Space must restore the same terminal body.");
        foreach (var group in new[] { projects, terminalGroup }) app.Click(app.Find<Button>("FoldRestore_" + group));
        Require(new[] { projects, terminalGroup }.All(group => app.Window.Layout.Group(group).Folded && Math.Abs(Height(group) - 27) < .5), "Both side groups must fold independently to 27 pixels.");
        foreach (var group in new[] { projects, terminalGroup }) app.Click(app.Find<Button>("FoldRestore_" + group));
        Require(ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)), "Restoring both groups must retain the terminal body.");
        app.ContextAction(app.Find<Button>("Tab_files"), "New left group at bottom");
        Require(app.Window.Layout.State.Groups.Count(group => group.Region == "left") == 3 &&
            app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Count(tab => tab.Id == "files") == 1,
            "The Files context action must create one new left group without duplicating its tab.");
        Console.WriteLine("PASS upstream layout.spec.ts: a terminal can move to its own side group; resize, fold, and visibility gate its one body");
    }

    private static void RegionCommands(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-region-commands"));
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        app.Click(app.Find<Button>("NewTerminal_" + bottom.Id));
        Until(() => app.Window.Layout.Tabs(bottom.Id).Count(tab => tab.Kind == "terminal") == 2);
        var id = app.Window.Layout.Tabs(bottom.Id).Last().Id;
        var name = id.Replace(':', '_');
        var body = app.Find<Control>("TerminalSurface_" + name);
        foreach (var region in new[] { "left", "right", "bottom" })
            foreach (var atStart in new[] { true, false })
            {
                var count = app.Window.Layout.State.Groups.Count(group => group.Region == region);
                var direction = region == "bottom" ? atStart ? "left" : "right" : atStart ? "top" : "bottom";
                app.ContextAction(app.Find<Button>("Tab_" + name), $"New {region} group at {direction}");
                Until(() => app.Window.Layout.State.Groups.Count(group => group.Region == region) == count + 1);
                var groups = app.Window.Layout.State.Groups.Where(group => group.Region == region).ToArray();
                Require(app.Window.Layout.Tabs(atStart ? groups[0].Id : groups[^1].Id).Single().Id == id &&
                    app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Count(tab => tab.Id == id) == 1 &&
                    ReferenceEquals(body, app.Find<Control>("TerminalSurface_" + name)),
                    "Region creation must insert at the chosen end and move the one terminal surface without copying it.");
            }
        var tab = app.Find<Button>("Tab_" + name);
        app.Click(tab, mouseButton: MouseButton.Right); Until(() => tab.ContextMenu!.IsOpen);
        Require(tab.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Header is string title && title.StartsWith("New bottom group", StringComparison.Ordinal))
            .All(item => !item.IsEnabled && ((string)item.Header!).EndsWith(" — limited to 3", StringComparison.Ordinal)),
            "Region creation must disable its commands and explain the configured group limit.");
        tab.ContextMenu.Close();
        Console.WriteLine("PASS terminal region-edge commands, retained surface identity and group limits");
    }

    private static void TabTooltip(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "tab-tooltip"));
        var tab = app.Find<Button>("Tab_specs");
        var tip = (ToolTip)ToolTip.GetTip(tab)!;
        var bottom = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height), app.Window)!.Value;
        app.Window.MouseMove(bottom - new Vector(0, 2));
        Until(() => ToolTip.GetIsOpen(tab)); Settle();
        var tooltipRoot = TopLevel.GetTopLevel(tip)!;
        var position = tip.TranslatePoint(default, tooltipRoot)!.Value;
        var screen = tooltipRoot.PointToScreen(position);
        var tabBottom = app.Window.PointToScreen(bottom);
        Require(screen.Y >= tabBottom.Y && screen.Y <= tabBottom.Y + 8,
            "The tooltip must sit immediately below the tab, independently of the pointer's vertical offset.");
        var expectedX = screen.X;
        app.Window.MouseMove(tab.TranslatePoint(new Point(6, 2), app.Window)!.Value); Settle();
        Require(tooltipRoot.PointToScreen(tip.TranslatePoint(default, tooltipRoot)!.Value).X == expectedX,
            "Moving across a tab must not move its tooltip horizontally with the pointer.");
        Console.WriteLine("PASS tab tooltip follows the tab bounds rather than the pointer");
    }

    private static void TabHover(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "tab-hover"));
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        var first = app.Window.Layout.Tabs(bottom.Id).Single();
        app.Window.Layout.NewTerminal(bottom.Id); Settle();
        var name = first.Id.Replace(':', '_');
        var chrome = app.Find<Grid>("DockTab_" + name);
        var frame = chrome.Children.OfType<Border>().First();
        var button = app.Find<Button>("Tab_" + name);
        var close = chrome.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "CloseTab");
        var presenter = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .Single(item => item.Name == "PART_ContentPresenter");
        var original = SharpRail.UI.Rendering.Ui.Theme;
        foreach (var theme in SharpRail.UI.Rendering.Themes.All)
        {
            SharpRail.UI.Rendering.Ui.Apply(theme);
            app.Window.MouseMove(button.TranslatePoint(new Point(6, 6), app.Window)!.Value); Settle();
            Require(ReferenceEquals(frame.Background, SharpRail.UI.Rendering.Ui.Hover) &&
                presenter.Background is Avalonia.Media.ISolidColorBrush brush && brush.Color.A == 0,
                "Hover must paint the whole tab frame without a second label-only background.");
            app.Window.MouseMove(close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), app.Window)!.Value); Settle();
            Require(ReferenceEquals(frame.Background, SharpRail.UI.Rendering.Ui.Hover) && close.Opacity == 1,
                "Moving onto the close button must retain the whole-tab hover.");
            app.Window.MouseMove(new Point(2, 2)); Settle();
            Require(ReferenceEquals(frame.Background, SharpRail.UI.Rendering.Ui.Elevated),
                "Leaving an inactive tab must restore its normal background.");
        }
        SharpRail.UI.Rendering.Ui.Apply(original);
        app.Window.MouseMove(button.TranslatePoint(new Point(6, 6), app.Window)!.Value); Settle();
        app.Window.Layout.ApplyPreset(DockState.Preset("review")); Settle();
        Require(app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Any(tab => tab.Id == first.Id),
            "Changing layout under a hovered tab must retain the resource and process pointer exit safely.");
        Console.WriteLine("PASS whole-tab hover over label and close button in dark/light themes and layout changes");
    }

    private static void SideMenu(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-side-menu"));
        var side = app.Window.Layout.State.Groups.First(group => group.Region == "right");
        Require(!app.Window.Layout.Tabs(side.Id).Any(tab => tab.Kind == "terminal"), "The Specs group must start without a terminal.");
        Require(!app.Find<Button>("NewTerminal_" + side.Id).IsVisible, "The Specs view must not show a terminal opener.");
        app.Window.Layout.NewTerminal(side.Id);
        app.Click(app.Find<Button>("NewTerminal_" + side.Id));
        Until(() => app.Window.Layout.Tabs(side.Id).Count(tab => tab.Kind == "terminal") == 2);
        Require(!app.Window.Layout.State.Center.Leaves().SelectMany(app.Window.Layout.Tabs).Any(tab => tab.Kind == "terminal"),
            "The side menu must create a terminal in its own group.");
        app.ContextAction(app.Find<Button>("Tab_projects"), "Close");
        app.ContextAction(app.Find<Button>("Tab_changes"), "Close");
        var add = app.Find<Button>("AddToGroup_" + side.Id);
        app.Click(add); Until(() => add.ContextMenu!.IsOpen);
        var entries = add.ContextMenu!.Items.OfType<MenuItem>().ToArray();
        Require(entries.Any(item => Equals(item.Header, "Show Changes")) && !entries.Any(item => Equals(item.Header, "Show Projects")),
            "A side menu must offer only its own side's hidden tools.");
        add.ContextMenu.Close();
        Console.WriteLine("PASS upstream layout.spec.ts: the side group menu shows tools for its own side and opens terminals in that group");
    }
}