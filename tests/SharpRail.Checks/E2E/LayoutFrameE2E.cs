using System.Net;
using System.Net.Sockets;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
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
        var directory = Path.Combine(root, "layout-strips");
        using var app = new E2eWorkspace(directory, prepare: _ => WorkspaceTabsE2E.Git(directory, "init", "-q", "-b", "main"));
        app.Open("README.md", true);
        Require(app.Window.Layout.State.Groups.All(group => group.Region != "bottom"), "The workspace has no bottom zone.");
        var right = app.Window.Layout.State.Groups.First(group => group.Tools.Any(tab => tab.Id == "files")).Id;
        Require(Math.Abs(app.Find<Border>("TabStrip_" + right).Bounds.Height - 32) < .1 &&
            !app.Find<Border>("TabStrip_" + right).GetLogicalDescendants().OfType<ScrollViewer>().Any(),
            "A side tool pane's header must be one 32-pixel row without a tab list.");
        foreach (var tool in DockState.ToolNames.Append(DockState.SpecsTool))
        {
            if (tool == "lsp")
            {
                app.Window.Layout.RestoreTool(tool);
                Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "LspPanel"));
            }
            var entry = app.Find<Button>("Tab_" + tool.Replace(':', '_'));
            Require(!entry.GetLogicalAncestors().OfType<Grid>().First().GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "CloseTab"),
                "Tool rail entries must not offer a close button.");
        }
        app.Click(app.Find<Button>("Tab_files"));
        Require(AutomationProperties.GetName(app.Find<Button>("Tab_files")) == "Files" &&
            app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "PaneTitle" && text.Text == "Files"),
            "The Files tool must be titled Files.");
        Require(app.Find<Button>("Tab_markdown_README.md").GetLogicalAncestors().OfType<Grid>().First().GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "CloseTab") == 1,
            "The document list must offer its close button.");
        app.ShowTool("changes");
        var panel = app.Find<Grid>("ChangesPanel");
        var header = panel.Children.OfType<Control>().Single(child => Grid.GetRow(child) == 0);
        var toolbar = app.Find<ChangesToolbarLayout>("ChangesToolbar");
        // One 32-pixel row when everything fits; otherwise the compare picker takes a second row. Either way nothing is cut off or overlaps the list.
        bool ToolbarFits(bool wrapped)
        {
            app.Window.UpdateLayout();
            var bounds = new Rect(toolbar.Bounds.Size);
            return toolbar.Children.Count == 3 && toolbar.Wrapped == wrapped && Math.Abs(toolbar.Bounds.Height - (wrapped ? 64 : 32)) < .1 &&
                panel.Children.OfType<Control>().Where(child => Grid.GetRow(child) == 1).All(child => child.Bounds.Top >= header.Bounds.Bottom - .1) &&
                toolbar.Children.All(child => child.Bounds.Height <= 32 && bounds.Contains(child.Bounds) && child.Bounds.Width >= child.DesiredSize.Width - .5);
        }
        Require(ToolbarFits(false), "A Changes toolbar that fits must remain one 32-pixel row above the list with nothing cut off.");
        var previousWidth = panel.Width;
        // Room for the scope and view toggle, not the compare picker as well.
        panel.Width = toolbar.Children[0].DesiredSize.Width + toolbar.Children[1].DesiredSize.Width + toolbar.Margin.Left + toolbar.Margin.Right + 8;
        Require(ToolbarFits(true), "A narrow Changes toolbar must wrap the compare picker to a second 32-pixel row above the list with nothing cut off.");
        panel.Width = previousWidth;
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
            Require(!app.Window.Layout.State.LeftVisible && app.Find<Border>("leftRail").IsVisible &&
                !app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "AuxiliaryStack_left"),
                "Dragging the left separator to the edge must hide the left side and expose its rail.");
            DragSideClosed(app, "right");
            Require(!app.Window.Layout.State.RightVisible && app.Find<Border>("rightRail").IsVisible &&
                !app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "AuxiliaryStack_right"),
                "Dragging the right separator to the edge must hide the right side and expose its rail.");
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            Require(app.Find<Border>("leftRail").IsVisible && app.Find<Border>("rightRail").IsVisible,
                "Both hidden sides must survive window recreation.");
            app.Click(app.Find<Border>("leftRail").GetLogicalDescendants().OfType<Button>().First()); Settle();
            app.Click(app.Find<Border>("rightRail").GetLogicalDescendants().OfType<Button>().First()); Settle();
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
        Require(!app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "AddToGroup_" + right),
            "Pane headers must not show an add button.");
        var header = app.Find<Grid>("GroupHeader_" + right);
        RightClickAt(app, header, new Point(header.Bounds.Width - 100, header.Bounds.Height / 2), header.ContextMenu!);
        Choose(app, header.ContextMenu!, "Show " + DockState.Tool(tool).Title);
        Until(() => app.Window.Layout.State.Groups.Any(group => group.Tools.Any(tab => tab.Id == tool)));
    }

    private static void Commands(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-commands"));
        var state = () => app.Window.Layout.State;
        var left = state().Groups.Single(group => group.Region == "left").Id;
        var header = app.Find<Grid>("GroupHeader_" + left);
        var menu = header.ContextMenu!;
        // The blank part of a tool pane's header, between its title and its actions, opens the pane menu.
        RightClickAt(app, header, new Point(header.Bounds.Width - 100, header.Bounds.Height / 2), menu);
        Choose(app, menu, "Hide left region");
        Until(() => !state().LeftVisible && app.Find<Border>("leftRail").IsVisible);
        app.Click(app.Find<Button>("Tab_projects"));
        Until(() => state().LeftVisible && app.Find<Grid>("AuxiliaryStack_left").IsVisible);

        app.ContextAction(app.Find<Button>("Tab_files"), "Close");
        Until(() => !state().Groups.Any(group => group.Tools.Any(tab => tab.Id == "files")));
        ShowTool(app, "files");
        app.Find<Button>("Tab_plugin_spec-dialect_specs").Focus();
        app.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Until(() => !state().Groups.Any(group => group.Tools.Any(tab => tab.Id == DockState.SpecsTool)));
        ShowTool(app, DockState.SpecsTool);

        app.ShowTool("files");
        app.Open("README.md", true); app.Open("notes.txt", true); app.Open("LINKS.md", true);
        app.Tab("README.md").Focus();
        app.Window.KeyPress(Key.Right, RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
        Until(() => app.Tabs[1].Path == "README.md");

        var overflow = app.Find<Button>("TabOverflow_" + app.Center);
        app.Window.Width = 620; Settle();
        Require(!overflow.IsVisible, "Projects lists tabs vertically without a horizontal search trigger.");
        app.Window.Width = 1352; Settle();

        Require(state().Center.IsLeaf && !app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.Name!.StartsWith("CenterSeparator_", StringComparison.Ordinal)),
            "The main column must stay one group without center separators.");
        Console.WriteLine("PASS upstream layout.spec.ts: keyboard and menu commands reorder and search tabs in the one main column group");
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
        Require(app.Window.Layout.State.Groups.All(group => group.Region != "bottom"), "The retired shortcut must not create a bottom zone.");
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        Require(app.Tabs.Count == 0 && !app.Window.Layout.State.Groups.Any(legacyGroup => legacyGroup.Region == "bottom") && Math.Abs(Width(app, "right") - resized) < 1,
            "A new workspace must inherit the local frame without the first workspace's document tabs.");
        WorkspaceTabsE2E.Switch(app, app.Root);
        Require(app.Tabs.Single().Path == "README.md" && !app.Window.Layout.State.Groups.Any(legacyGroup => legacyGroup.Region == "bottom") && Math.Abs(Width(app, "right") - resized) < 1,
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