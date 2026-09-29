using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LayoutFrameE2E
{
    private static RawInputModifiers Command => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    internal static void Run(string root)
    {
        StripGeometry(root);
        OuterSeparatorsHide(root);
        Commands(root);
        FrameSurvivesSwitch(root);
        Reconnect(root);
    }

    private static ISelectionItemProvider Item(Control tab) => (ISelectionItemProvider)ControlAutomationPeer.CreatePeerForElement(tab)!;

    private static void StripGeometry(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-strips"));
        app.Open("README.md", true);
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom").Id;
        Require(app.Window.Layout.Tabs(bottom).Single().Kind == "terminal", "The workspace must start with its initial bottom terminal.");
        var right = app.Window.Layout.State.Groups.First(group => group.Tools.Any(tab => tab.Id == "files")).Id;
        foreach (var id in new[] { app.Center, right, bottom })
        {
            var strip = app.Find<Border>("TabStrip_" + id);
            Require(Math.Abs(strip.Bounds.Height - 32) < .1, "Every workbench tab strip must stay one 32-pixel row.");
            var scroll = strip.GetLogicalDescendants().OfType<ScrollViewer>().Single();
            Require(ControlAutomationPeer.CreatePeerForElement((Control)scroll.Content!)!.GetAutomationControlType() == AutomationControlType.Tab &&
                scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                "Each strip must expose one tab list that scrolls horizontally without scrollbars.");
            Require(!strip.GetLogicalDescendants().OfType<Button>().Any(button => AutomationProperties.GetName(button)?.StartsWith("Scroll tabs", StringComparison.Ordinal) == true),
                "Tab strips must not add scroll buttons.");
            var tabs = strip.GetLogicalDescendants().OfType<Button>().Where(button => button.Name?.StartsWith("Tab_", StringComparison.Ordinal) == true).ToArray();
            var selected = tabs.Where(tab => Item(tab).IsSelected).ToArray();
            Require(selected.Length == 1, "Each strip must expose exactly one selected tab.");
            var body = ControlAutomationPeer.CreatePeerForElement(app.Find<Border>("DockBody_" + id))!;
            Require(body.GetAutomationControlType() == AutomationControlType.Pane && ReferenceEquals(body.GetLabeledBy(), ControlAutomationPeer.CreatePeerForElement(selected[0])),
                "The selected tab must label its pane.");
        }
        foreach (var tool in DockState.ToolNames)
        {
            var wrapper = app.Find<Button>("Tab_" + tool).GetLogicalAncestors().OfType<Grid>().First(item => item.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true);
            Require(!wrapper.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "CloseTab"), "Tool tabs must not offer a close button.");
        }
        Require(app.Find<Button>("Tab_files").GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Files") &&
            !app.Find<Button>("Tab_files").GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("All files") == true),
            "The Files tool must be titled Files.");
        Require(app.Find<Border>("TabStrip_" + app.Center).GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "CloseTab") == 1 &&
            app.Find<Border>("TabStrip_" + bottom).GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "CloseTab") == 1,
            "The document strip and the terminal strip must each offer one close button.");
        app.Click(app.Find<Button>("Tab_changes"));
        var panel = app.Find<Grid>("ChangesPanel");
        var toolbar = panel.Children.OfType<Control>().Single(child => Grid.GetRow(child) == 0);
        Require(Math.Abs(toolbar.Bounds.Height - 32) < .1 && toolbar.GetLogicalDescendants().OfType<ToggleButton>().All(toggle => toggle.Bounds.Height <= 32) &&
            toolbar.GetLogicalDescendants().OfType<Grid>().First().ClipToBounds, "The Changes toolbar must remain one clipped 32-pixel row.");
        Console.WriteLine("PASS upstream layout.spec.ts: workbench strips and feature toolbars keep one-row geometry with ARIA tabs");
    }

    private static void DragSideClosed(E2eWorkspace app, string side)
    {
        Settle(550);
        var separator = app.Find<ResizeHandle>(side + "Separator");
        var start = separator.TranslatePoint(new Point(separator.Bounds.Width / 2, separator.Bounds.Height / 2), app.Window)!.Value;
        var workbench = app.Find<DockSurface>("WorkspaceWorkbench");
        var edge = workbench.TranslatePoint(default, app.Window)!.Value.X + (side == "left" ? 1 : workbench.Bounds.Width - 1);
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        for (var step = 1; step <= 12; step++) app.Window.MouseMove(new Point(start.X + (edge - start.X) * step / 12, start.Y));
        app.Window.MouseUp(new Point(edge, start.Y), MouseButton.Left); Settle();
    }

    private static double Width(E2eWorkspace app, string side) => app.Find<Grid>("AuxiliaryStack_" + side).Bounds.Width;

    private static void OuterSeparatorsHide(string root)
    {
        var directory = Path.Combine(root, "layout-outer-hide");
        double left, right;
        using (var app = new E2eWorkspace(directory))
        {
            left = Width(app, "left"); right = Width(app, "right");
            DragSideClosed(app, "left");
            Require(!app.Window.Layout.State.LeftVisible && app.Find<Border>("leftHiddenSideRail").IsVisible &&
                !app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "AuxiliaryStack_left"),
                "Dragging the left separator to the edge must hide the left side and expose its rail.");
            DragSideClosed(app, "right");
            Require(!app.Window.Layout.State.RightVisible && app.Find<Border>("rightHiddenSideRail").IsVisible &&
                !app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "AuxiliaryStack_right"),
                "Dragging the right separator to the edge must hide the right side and expose its rail.");
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            Require(app.Find<Border>("leftHiddenSideRail").IsVisible && app.Find<Border>("rightHiddenSideRail").IsVisible,
                "Both hidden sides must survive window recreation.");
            app.Click(app.Find<Button>("leftRestoreRail")); Settle();
            app.Click(app.Find<Button>("rightRestoreRail")); Settle();
            Require(Math.Abs(Width(app, "left") - left) < 24 && Math.Abs(Width(app, "right") - right) < 24,
                "Restoring the hidden sides must recover their previous widths.");
        }
        Console.WriteLine("PASS upstream layout.spec.ts: dragging outer separators hides both sides and preserves their restore state");
    }

    private static void RightClickAt(E2eWorkspace app, Control control, Point point, ContextMenu menu)
    {
        Settle(550);
        var target = control.TranslatePoint(point, app.Window)!.Value;
        app.Window.MouseMove(target); Settle(100);
        app.Window.MouseDown(target, MouseButton.Right); app.Window.MouseUp(target, MouseButton.Right);
        Until(() => menu.IsOpen);
    }

    private static void Choose(E2eWorkspace app, ContextMenu menu, string title)
    {
        var item = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, title));
        Require(item.IsEnabled, "E2E menu action is disabled: " + title);
        app.Click(item, freshGesture: false);
    }

    private static void ShowTool(E2eWorkspace app, string tool)
    {
        var right = app.Window.Layout.State.Groups.First(group => group.Region == "right").Id;
        var add = app.Find<Button>("AddToGroup_" + right);
        app.Click(add); Until(() => add.ContextMenu!.IsOpen);
        Choose(app, add.ContextMenu!, "Show " + DockState.Tool(tool).Title);
        Until(() => app.Window.Layout.State.Groups.Any(group => group.Tools.Any(tab => tab.Id == tool)));
    }

    private static bool TabFocused(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Button>().Count(button => button.Name?.StartsWith("Tab_", StringComparison.Ordinal) == true && button.IsFocused) == 1;

    private static void Commands(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-commands"));
        var state = () => app.Window.Layout.State;
        var left = state().Groups.Single(group => group.Region == "left").Id;
        var header = app.Find<Grid>("GroupHeader_" + left);
        var menu = header.ContextMenu!;
        var tab = app.Find<Button>("Tab_projects").GetLogicalAncestors().OfType<Grid>().First(item => item.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true);
        RightClickAt(app, header, new Point(tab.TranslatePoint(default, header)!.Value.X + tab.Bounds.Width + 12, header.Bounds.Height / 2), menu);
        Choose(app, menu, "Hide left region");
        Until(() => !state().LeftVisible && app.Find<Border>("leftHiddenSideRail").IsVisible);
        app.Click(app.Find<Button>("leftRestoreRail"));
        Until(() => state().LeftVisible && app.Find<Grid>("AuxiliaryStack_left").IsVisible);

        app.ContextAction(app.Find<Button>("Tab_files"), "Close");
        Until(() => !state().Groups.Any(group => group.Tools.Any(tab => tab.Id == "files")));
        ShowTool(app, "files");
        app.Find<Button>("Tab_specs").Focus();
        app.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Until(() => !state().Groups.Any(group => group.Tools.Any(tab => tab.Id == "specs")));
        ShowTool(app, "specs");

        app.Click(app.Find<Button>("Tab_files"));
        app.Open("README.md", true); app.Open("notes.txt", true); app.Open("LINKS.md", true);
        app.Tab("README.md").Focus();
        app.Window.KeyPress(Key.Right, RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
        Until(() => app.Tabs[1].Path == "README.md");

        var overflow = app.Find<Button>("TabOverflow_" + app.Center);
        Require(!overflow.IsVisible, "A wide window must not show tab search.");
        app.Window.Width = 620; Settle();
        Until(() => overflow.IsVisible);
        app.Click(overflow);
        var flyout = (Flyout)overflow.Flyout!;
        Until(() => flyout.IsOpen);
        var content = (Control)flyout.Content!;
        content.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "TabOverflowSearch").Text = "notes";
        Settle();
        var option = content.GetLogicalDescendants().OfType<ListBox>().Single().GetVisualDescendants().OfType<ListBoxItem>().Single();
        app.Click(option, freshGesture: false);
        Until(() => !flyout.IsOpen && app.Window.Layout.Selected(app.Center)?.Path == "notes.txt" && app.Tab("notes.txt").IsFocused);
        app.Window.Width = 1352; Settle();
        Until(() => !overflow.IsVisible);

        app.Window.Width = 620; Settle();
        Until(() => overflow.IsVisible);
        app.Click(overflow); Until(() => overflow.Flyout is Flyout { IsOpen: true });
        flyout = (Flyout)overflow.Flyout!;
        app.Window.Width = 1352; Settle();
        Until(() => !overflow.IsVisible && !flyout.IsOpen);
        app.Window.Width = 620; Settle();
        Until(() => overflow.IsVisible);
        Require(!flyout.IsOpen, "Tab search must stay closed after its trigger was hidden and shown again.");
        app.Window.Width = 1352; Settle();

        app.ContextAction(app.Tab("notes.txt"), "Split right");
        Until(() => state().Center.Leaves().Count() == 2);
        Require(app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Count(handle => handle.Name!.StartsWith("CenterSeparator_", StringComparison.Ordinal)) == 1,
            "A center split must add one separator.");
        var notes = state().Center.Leaves().Single(group => app.Window.Layout.Tabs(group).Any(tab => tab.Path == "notes.txt"));
        var other = state().Center.Leaves().Single(group => group != notes);
        app.Tab("notes.txt").Focus();
        app.Window.KeyPress(Key.F6, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F6, null);
        Until(() => app.Window.Layout.View.FocusedCenter == other && TabFocused(app));
        app.Window.KeyPress(Key.F6, RawInputModifiers.Control, PhysicalKey.F6, null);
        Until(() => app.Window.Layout.View.FocusedCenter == notes && TabFocused(app));

        app.ContextAction(app.Tab("LINKS.md"), "Split down");
        Until(() => state().Center.Leaves().Count() == 3);
        Require(app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Count(handle => handle.Name!.StartsWith("CenterSeparator_", StringComparison.Ordinal)) == 2,
            "A recursive split must add another separator.");
        var linksGroup = state().Center.Leaves().Single(group => app.Window.Layout.Tabs(group).Any(tab => tab.Path == "LINKS.md"));
        var wrapper = app.Tab("LINKS.md").GetLogicalAncestors().OfType<Grid>().First(item => item.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true);
        app.Click(wrapper.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab"));
        Until(() => app.Window.Layout.Tabs(linksGroup).Count == 0);
        Require(state().Center.Leaves().Count() == 3, "Closing the last tab must leave the empty group in place.");
        app.Click(app.Find<Button>("RemoveGroup_" + linksGroup));
        Until(() => state().Center.Leaves().Count() == 2);
        Until(() => TabFocused(app));
        Console.WriteLine("PASS upstream layout.spec.ts: keyboard and menu commands reorder, search, recursively split, and explicitly remove empty groups");
    }

    private static void FrameSurvivesSwitch(string root)
    {
        var source = Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE");
        if (string.IsNullOrEmpty(source)) { Console.WriteLine("SKIP upstream layout.spec.ts one local frame: set SHARPRAIL_TEST_GIT_SOURCE."); return; }
        using var app = new E2eWorkspace(WorkspaceTabsE2E.Repository(root, "layout-frame-switch", source));
        app.Open("README.md", true);
        var separator = app.Find<ResizeHandle>("rightSeparator");
        var start = separator.TranslatePoint(new Point(separator.Bounds.Width / 2, separator.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(-70, 0)); Settle();
        app.Window.MouseUp(start + new Vector(-70, 0), MouseButton.Left); Settle();
        var resized = Width(app, "right");
        app.Window.KeyPress(Key.J, Command | RawInputModifiers.Shift, PhysicalKey.J, null); Settle();
        Require(!app.Window.Layout.State.BottomVisible, "Mod+Shift+J must hide the bottom panel.");
        WorkspaceTabsE2E.CreateWorkspace(app, "frame-second");
        Require(app.Tabs.Count == 0 && !app.Window.Layout.State.BottomVisible && Math.Abs(Width(app, "right") - resized) < 1,
            "A new workspace must inherit the local frame without the first workspace's document tabs.");
        WorkspaceTabsE2E.Switch(app, app.Root);
        Require(app.Tabs.Single().Path == "README.md" && !app.Window.Layout.State.BottomVisible && Math.Abs(Width(app, "right") - resized) < 1,
            "Returning must restore the document tab under the same local frame.");
        Console.WriteLine("PASS upstream layout.spec.ts: one local frame survives workspace switches while resource tabs stay workspace-specific");
    }

    private static void Reconnect(string root)
    {
        var directory = Path.Combine(root, "layout-reconnect");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README.md"), "# reconnect\n");
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Microsoft.AspNetCore.Builder.WebApplication Start()
        {
            var server = RemoteServer.Create(directory, IPAddress.Loopback, port, "reconnect-test");
            Task.Run(() => server.StartAsync()).GetAwaiter().GetResult(); return server;
        }
        var first = Start();
        using var remote = new RemoteProjectAdapter(new Uri($"http://127.0.0.1:{port}"), "reconnect-test");
        var window = new WorkbenchWindow(remote, directory, new ProfileStore(directory + "-profile"), E2E.E2eTerminals.Plain, remote: true) { Width = 1352, Height = 848 };
        try
        {
            window.Show(); Until(() => window.WorkspaceMounted);
            var status = window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ConnectionStatus");
            Until(() => status.Text == "Remote");
            window.Focus();
            window.KeyPress(Key.B, Command, PhysicalKey.B, null); Settle();
            Require(!window.Layout.State.LeftVisible, "Mod+B must hide the left side over a remote transport.");
            Task.Run(() => first.StopAsync()).GetAwaiter().GetResult();
            window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
            Until(() => status.Text == "Error");
            var second = Start();
            try
            {
                for (var attempt = 0; attempt < 10 && status.Text != "Remote"; attempt++)
                {
                    window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null); Settle(500);
                }
                Require(status.Text == "Remote" && !window.Layout.State.LeftVisible, "The transport must reconnect with the hidden left side intact.");
                window.KeyPress(Key.B, Command, PhysicalKey.B, null); Settle();
                Require(window.Layout.State.LeftVisible, "The layout must remain writable after the transport reconnects.");
            }
            finally { Task.Run(() => second.StopAsync()).GetAwaiter().GetResult(); }
        }
        finally { window.Close(); }
        Console.WriteLine("PASS upstream layout.spec.ts: layout survives a transport reconnect and remains writable");
    }
}
