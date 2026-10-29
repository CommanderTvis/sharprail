using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class DockInputChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    // Upstream layout SPEC "Auxiliary presentation and recall" and Project Home's own Projects pane.
    private static void RailChecks(WorkbenchWindow window, Func<string, Control> find)
    {
        T Find<T>(string name) where T : Control => (T)find(name);
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        void Pump(Func<bool> done, string message)
        {
            var until = Awake.Now.AddSeconds(15);
            while (!done() && Awake.Now < until)
            {
                using var stop = new CancellationTokenSource(10);
                Dispatcher.UIThread.MainLoop(stop.Token); window.UpdateLayout();
            }
            Require(done(), message);
        }
        IFocusManager Focus() => TopLevel.GetTopLevel(window)!.FocusManager!;
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var right = window.Layout.State.Groups.Where(group => group.Region == "right").ToArray();
        var surface = window.GetLogicalDescendants().OfType<DockSurface>().Single();
        var resolveIcon = surface.TabIcon;
        surface.TabIcon = (tab, brush) => tab.Id == "files" ? null : new Border { Tag = "resolved:" + tab.Id };
        surface.Rebuild();
        ContentControl HeaderIcon() => Find<Grid>("GroupHeader_" + right[0].Id).GetLogicalDescendants().OfType<ContentControl>()
            .Single(control => control.Width == 14 && control.Height == 14);
        Require(HeaderIcon().Content is Border { Tag: "resolved:plugin:spec-dialect:specs" },
            "The initial pane header must use the same plugin-aware icon resolver as its rail entry.");
        Find<Button>("Tab_files").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => window.Layout.Selected(right[0].Id)?.Id == "files", "Select the built-in Files tool.");
        Require(HeaderIcon().Content is Control fallback && !Equals(fallback.Tag, "resolved:files"),
            "A null custom icon must keep the built-in pane glyph.");
        Find<Button>("Tab_plugin_spec-dialect_specs").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => HeaderIcon().Content is Border { Tag: "resolved:plugin:spec-dialect:specs" },
            "Switching tools must refresh the pane header through the plugin-aware icon resolver.");
        window.Layout.Close(right[1].Id, "review");
        Require(Find<Button>("RailUnplaced_review").GetLogicalDescendants().OfType<Border>().Any(border => Equals(border.Tag, "resolved:review")),
            "Unplaced tools must use the same custom icon resolver.");
        surface.TabIcon = resolveIcon;
        surface.Rebuild();
        window.Layout.RestoreTool("review");

        var rail = Find<StackPanel>("rightRailEntries");
        var clusters = rail.Children.OfType<StackPanel>().ToArray();
        Require(clusters.Select(cluster => cluster.Name).SequenceEqual(right.Select(group => "RailCluster_" + group.Id)) &&
            clusters[0].GetLogicalDescendants().OfType<Button>().Select(button => button.Name).SequenceEqual(new[] { "Tab_plugin_spec-dialect_specs", "Tab_files" }) &&
            rail.GetLogicalDescendants().OfType<Button>().Count(button => Equals(button.Tag, "pressed")) == right.Length,
            "The rail must mirror its panes as separated clusters, with exactly one pressed entry per expanded pane.");
        // Every entry's accent bar shares its icon's vertical centre, and every separator the icon column's horizontal centre.
        double CentreY(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height / 2), rail)!.Value.Y;
        double CentreX(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, 0), rail)!.Value.X;
        var frames = rail.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "RailEntryFrame").ToArray();
        var accents = rail.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "RailAccent" && border.IsVisible).ToArray();
        Require(accents.Length > 0 && accents.All(accent => Math.Abs(CentreY(accent) - CentreY(frames.Single(frame => ReferenceEquals(frame.Parent, accent.Parent)))) < .5),
            "A rail entry's accent bar must share the vertical centre of its icon frame.");
        Require(frames.Length > 0 && rail.GetLogicalDescendants().OfType<Border>().Where(border => border.Height == 1 && border.Bounds.Width > 0)
                .All(line => Math.Abs(CentreX(line) - CentreX(frames[0])) < .5),
            "Rail separators must share the horizontal centre of the icons.");
        window.Layout.Close(right[1].Id, "review");
        var unplaced = Find<Button>("RailUnplaced_review");
        Require(unplaced.Content is Border { Background: var dimmed } && ReferenceEquals(dimmed, Ui.Hint) && Find<StackPanel>("rightRailEntries").Children.IndexOf(unplaced.GetLogicalAncestors().OfType<Grid>().First()) >
            Find<StackPanel>("rightRailEntries").Children.IndexOf(Find<StackPanel>("RailCluster_" + right[1].Id)),
            "A tool placed nowhere must sit dimmed in a trailing cluster.");
        unplaced.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => window.Layout.Group(right[1].Id).Tools.Any(tab => tab.Id == "review") && window.Layout.Selected(right[1].Id)?.Id == "review",
            "Activating an unplaced tool must restore it to its recorded pane.");
        var files = Find<Button>("Tab_files");
        files.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => window.Layout.Selected(right[0].Id)?.Id == "files" && Equals(Find<Button>("Tab_files").Tag, "pressed"),
            "An unpressed entry must select its tool in its pane.");
        Find<Button>("Tab_files").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => window.Layout.Group(right[0].Id).Folded && Find<Button>("Tab_files").IsFocused &&
            !window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "GroupHeader_" + right[0].Id),
            "The pressed entry must fold its pane, which keeps no interior strip, and focus must stay on the entry.");
        Find<Button>("Tab_files").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => !window.Layout.Group(right[0].Id).Folded && Find<Grid>("GroupHeader_" + right[0].Id).IsFocused,
            "Unfolding through the rail must return focus to the pane.");

        // Mod+B hides the left side onto its rail and brings it back with focus in the pane.
        Find<Grid>("GroupHeader_" + window.Layout.State.Groups.Single(group => group.Region == "left").Id).Focus();
        window.KeyPress(Key.B, command, PhysicalKey.B, null);
        Pump(() => !window.Layout.State.LeftVisible && Find<Button>("Tab_projects").IsFocused, "Mod+B must hide the left side and focus its rail entry.");
        window.KeyPress(Key.B, command, PhysicalKey.B, null);
        Pump(() => window.Layout.State.LeftVisible && Focus().GetFocusedElement() is Grid { Name: var name } && name!.StartsWith("GroupHeader_", StringComparison.Ordinal),
            "Mod+B must show the left side again and focus its pane.");

        // Project Home has its own Projects pane: the same rail entry and Hide, its own visibility, and Mod+B.
        var home = window.OpenProjectHomeAsync(window.ProjectRoot);
        Pump(() => home.IsCompleted && window.AtProjectHome && window.GetLogicalDescendants().Any(item => item is Grid { Name: "HomeProjectsPane" }),
            "Project Home must show its own Projects pane.");
        Require(!window.GetLogicalDescendants().Any(item => item is Grid { Name: "AuxiliaryStack_bottom" or "AuxiliaryStack_left" }) &&
            Find<StackPanel>("leftRailEntries").GetLogicalDescendants().OfType<Button>().Select(button => button.Name).SequenceEqual(new[] { "Tab_projects" }) &&
            Equals(Find<Button>("Tab_projects").Tag, "pressed") && window.Layout.View.Documents.Values.All(tabs => tabs.Count == 0),
            "Project Home shows its own Projects pane and rail entry instead of the frame's left side and bottom, and starts no terminal.");
        var leftVisible = window.Layout.State.LeftVisible;
        Find<Button>("HidePane_home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => !window.Layout.State.HomeProjectsVisible && !window.GetLogicalDescendants().Any(item => item is Grid { Name: "HomeProjectsPane" }) &&
            Find<Button>("Tab_projects").IsFocused && window.Layout.State.LeftVisible == leftVisible,
            "Hide must collapse Project Home's pane onto its rail without touching the workspace frame.");
        window.KeyPress(Key.B, command, PhysicalKey.B, null);
        Pump(() => window.Layout.State.HomeProjectsVisible && Find<Grid>("HomeProjectsHeader").IsFocused, "Mod+B must restore Project Home's pane and focus it.");
        Console.WriteLine("PASS edge rails mirror panes, fold/unfold focus, unplaced tools, Mod+B and Project Home's Projects pane");
    }

    internal static void Run(string root)
    {
        root = E2E.IsolatedGit.Repository(Path.Combine(root, "dock-input-project"),
            ("README.md", File.ReadAllText(Path.Combine(root, "README.md"))),
            ("SPEC.md", File.ReadAllText(Path.Combine(root, "SPEC.md"))),
            ("hello.txt", File.ReadAllText(Path.Combine(root, "hello.txt"))));
        var profile = new ProfileStore(root + "-dock-input");
        E2E.E2eWorkspace.EnableSpecPlugins(profile.OpenState());
        var window = new WorkbenchWindow(new ProjectServices(root), root, profile, E2E.E2eTerminals.Plain);
        window.Show();
        var deadline = Awake.Now.AddSeconds(15);
        while ((!window.WorkspaceMounted || !window.Layout.Tools.Any(tool => tool.Id == "review")) && Awake.Now < deadline)
        {
            // Workspace startup crosses an animation frame before probing Git; draining jobs alone cannot render it.
            using var stop = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(stop.Token); window.UpdateLayout();
        }
        Require(window.WorkspaceMounted && window.Layout.Tools.Any(tool => tool.Id == "review"), "Dock input Git workspace did not mount.");
        T Find<T>(string name) where T : Control
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            return window.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
        }
        Rect Bounds(Control item)
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            return new(item.TranslatePoint(default, window)!.Value, item.Bounds.Size);
        }
        var titleBar = Find<Border>("WindowTitleBar");
        var titlePoint = new Point(Bounds(titleBar).Center.X, Bounds(titleBar).Center.Y);
        window.MouseDown(titlePoint, MouseButton.Left); window.MouseUp(titlePoint, MouseButton.Left);
        window.MouseDown(titlePoint, MouseButton.Left); window.MouseUp(titlePoint, MouseButton.Left);
        Require(window.WindowState == WindowState.Maximized,
            "Double-clicking blank title-bar space did not zoom the window.");
        window.WindowState = WindowState.Normal;
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Require(!window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "RemoveGroup_" + window.Layout.State.Center.Leaves().Single()),
            "The final empty center group offers no remove button; only its menu explains why.");
        var terminalButtons = window.GetLogicalDescendants().OfType<Button>().Count(button => button.IsVisible && button.Name?.StartsWith("NewTerminal_", StringComparison.Ordinal) == true);
        Require(terminalButtons == window.Layout.State.Groups.Count(group => !group.Folded && window.Layout.Selected(group.Id)?.IsTool != true),
            "Only resource and empty views must offer terminal creation.");
        var projectsGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "projects")).Id;
        Require(!window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "NewTerminal_" + projectsGroup) &&
            window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "PaneTitle" && text.Text == "Projects") && Find<Button>("HidePane_" + projectsGroup).IsVisible,
            "A tool pane shows a plain title header with Hide and no terminal opener.");
        window.Layout.NewTerminal(projectsGroup);
        var terminalPane = window.Layout.View.FocusedGroup;
        var leftPanes = window.Layout.State.Groups.Where(group => group.Region == "left").Select(group => group.Id).ToList();
        Require(terminalPane != projectsGroup && leftPanes.IndexOf(terminalPane) == leftPanes.IndexOf(projectsGroup) + 1 &&
            Find<Button>("NewTerminal_" + terminalPane).IsVisible && window.Layout.Tabs(projectsGroup).All(tab => tab.IsTool),
            "New terminal from a tool pane must create a terminal pane directly below it.");
        window.Layout.Close(terminalPane, window.Layout.Selected(terminalPane)!.Id);
        Require(window.Layout.RemoveGroup(terminalPane), "The emptied terminal pane must merge back.");
        foreach (var group in window.Layout.State.Groups.Where(group => !group.Folded && group.Region is "left" or "right"))
        {
            Require(!window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "AddToGroup_" + group.Id),
                "Pane headers must not show an add button; adding lives in the header menu.");
            var add = Find<Grid>("GroupHeader_" + group.Id);
            IEnumerable<DockToolInfo> Hidden() => window.Layout.Tools.Where(tool => tool.Region == group.Region &&
                !window.Layout.State.Groups.Any(pane => pane.Tools.Any(tab => tab.Id == tool.Id)));
            add.ContextMenu!.Open(add);
            Dispatcher.UIThread.RunJobs();
            Require(add.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Name).Where(name => name?.StartsWith("ShowTool_", StringComparison.Ordinal) == true)
                .SequenceEqual(Hidden().Select(tool => "ShowTool_" + tool.Id.Replace(':', '_'))) &&
                add.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Name == "NewTerminalBelow_" + group.Id) == group.Tools.Count > 0,
                "The header menu offers the current catalogue without duplicating already-placed singleton tools, and a tool pane a terminal pane below.");
            add.ContextMenu.Close();
        }
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var projects = Find<Button>("Tab_projects");
        var projectsPoint = Bounds(projects).Center;
        window.MouseDown(projectsPoint, MouseButton.Right); window.MouseUp(projectsPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(projects.ContextMenu?.IsOpen == true, "Right-click did not open the pane tab menu.");
        var closePane = projects.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Close"));
        var projectsPaneId = window.Layout.State.Groups.First(group => group.Tools.Any(tab => tab.Id == "projects")).Id;
        var tabEntries = projects.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray();
        Require(closePane.Icon is not null && tabEntries.Contains("Close group") == window.Layout.CanRemoveGroup(projectsPaneId) &&
            !tabEntries.Any(header => header is not null && (header.StartsWith("New ", StringComparison.Ordinal) || header == "Remove group")) &&
            tabEntries.Last() == "Split",
            "The tab menu must lead with the iconed Close, offer Close group only where a group can be removed, end with Split and carry no corner entries.");
        closePane.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        projects.ContextMenu.Close(); Dispatcher.UIThread.RunJobs();
        Require(!window.Layout.State.Groups.SelectMany(group => group.Tools).Any(tab => tab.Id == "projects"),
            "The pane tab's Close action did not hide the tool.");
        var revealGroup = window.Layout.State.Groups.First(group => group.Region != "center");
        var reveal = Find<Grid>("GroupHeader_" + revealGroup.Id);
        reveal.ContextMenu!.Open(reveal);
        Dispatcher.UIThread.RunJobs();
        reveal.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Show Projects"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        reveal.ContextMenu.Close(); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Group(revealGroup.Id).Tools.Any(tab => tab.Id == "projects"),
            "The header menu did not restore the hidden singleton to that group.");
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var initialCenterGroup = window.Layout.State.Center.Leaves().Single();
        window.Width = 1680; window.Height = 1000;
        var centerHeader = Find<Grid>("GroupHeader_" + initialCenterGroup);
        var headerPoint = Bounds(centerHeader).TopRight + new Vector(-6, 12);
        window.MouseDown(headerPoint, MouseButton.Right); window.MouseUp(headerPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(centerHeader.ContextMenu?.IsOpen == true &&
            !centerHeader.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Remove group")).IsEnabled,
            "Empty pane header context menu is missing or allows removing the final center group.");
        Require(!centerHeader.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Header is string header && header.StartsWith("New split", StringComparison.Ordinal)),
            "The main column header must offer no split actions.");
        centerHeader.ContextMenu!.Close();
        var launcher = window.GetLogicalDescendants().OfType<Button>().First(button => button.Name?.StartsWith("NewTerminal_", StringComparison.Ordinal) == true && button.IsVisible);
        var launcherHeader = Find<Grid>("GroupHeader_" + launcher.Name!["NewTerminal_".Length..]);
        var launcherPoint = Bounds(launcher).Center;
        window.MouseDown(launcherPoint, MouseButton.Right); window.MouseUp(launcherPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(launcherHeader.ContextMenu?.IsOpen != true, "Right-clicking a header launcher icon must not open the group menu.");
        launcherHeader.ContextMenu?.Close();
        // The top bar's window menu belongs to its empty space: the pickers, items and Settings open nothing.
        var topBar = Find<Grid>("MainHeader");
        var barControls = 0;
        foreach (var controlName in new[] { "ScopeProject", "ScopeWorkspace", "ScopeBranch", "RemotePublish", "RemotePush", "RemoteSync", "SettingsButton" })
        {
            var control = window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(item => item.Name == controlName && item.IsVisible);
            if (control is null) continue;
            barControls++;
            var controlPoint = Bounds(control).Center;
            window.MouseDown(controlPoint, MouseButton.Right); window.MouseUp(controlPoint, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Require(topBar.ContextMenu?.IsOpen != true, $"Right-clicking the top bar's {controlName} must not open the window menu.");
            topBar.ContextMenu?.Close();
        }
        Require(barControls >= 3, "The top bar probe found too few controls.");
        var barSpace = new Point(Bounds(Find<Control>("LocationBar")).Right - 4, Bounds(topBar).Center.Y);
        window.MouseDown(barSpace, MouseButton.Right); window.MouseUp(barSpace, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(topBar.ContextMenu?.IsOpen == true && topBar.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Reset frame")),
            "Right-clicking the top bar's empty space must open the window menu.");
        topBar.ContextMenu!.Close();
        var rightGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == DockState.SpecsTool));
        window.Layout.Select(rightGroup.Id, "files");
        projects = Find<Button>("Tab_projects");
        projectsPoint = Bounds(projects).Center;
        window.MouseDown(projectsPoint, MouseButton.Right); window.MouseUp(projectsPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(projects.ContextMenu!.IsOpen && !projects.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane")).IsVisible,
            "A Projects pane tab must not offer to move to another pane.");
        projects.ContextMenu.Close();
        var insertionFrom = Bounds(Find<Button>("Tab_files")).Center;
        var insertionTo = Bounds(Find<Button>("Tab_projects")).TopLeft + new Vector(16, 16);
        window.MouseDown(insertionFrom, MouseButton.Left); window.MouseMove(insertionTo);
        var insertionHints = Find<DockSurface>("WorkspaceWorkbench").Children.OfType<Canvas>().Single().Children.OfType<Border>().ToArray();
        Require(insertionHints.Any(item => item.Height == 2 && item.BorderThickness == new Thickness(0) &&
            item.CornerRadius == new CornerRadius(0) && ReferenceEquals(item.Background, Ui.Accent)),
            "Rail entry insertion must show a solid square 2px primary line, not a pane drop rectangle.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(insertionTo, MouseButton.Left);
        var projectsPane = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "projects"));
        var projectsHeader = Bounds(Find<Grid>("GroupHeader_" + projectsPane.Id));
        var appendPoint = new Point(projectsHeader.Right - 8, projectsHeader.Center.Y);
        insertionFrom = Bounds(Find<Button>("Tab_files")).Center;
        window.MouseDown(insertionFrom, MouseButton.Left); window.MouseMove(appendPoint);
        var dockSurface = Find<DockSurface>("WorkspaceWorkbench");
        var headerInSurface = new Rect(window.TranslatePoint(projectsHeader.TopLeft, dockSurface)!.Value, projectsHeader.Size).Inflate(1);
        var appendHints = dockSurface.Children.OfType<Canvas>().Single().Children.OfType<Border>()
            .Where(item => headerInSurface.Contains(new Rect(Canvas.GetLeft(item), Canvas.GetTop(item), item.Width, item.Height))).ToArray();
        Require(appendHints.Length == 1 && Math.Abs(appendHints[0].Width - projectsHeader.Width) < 2 && appendHints[0].BorderThickness == new Thickness(2),
            $"A tool pane's header is one join target outlined as a whole: {string.Join("; ", appendHints.Select(item => $"{Canvas.GetLeft(item)},{Canvas.GetTop(item)} {item.Width}x{item.Height} bg={item.Background}"))} header={projectsHeader}.");
        window.MouseUp(appendPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Group(projectsPane.Id).Tools.Last().Id == "files", $"Dropping on trailing header space did not append the tool: {string.Join(" | ", window.Layout.State.Groups.Select(g => g.Region + ":" + string.Join(",", window.Layout.Tabs(g.Id).Select(t => t.Id))))} pane={projectsPane.Id}");
        var dragCount = 0;
        void Drag(string tab, Point to)
        {
            var source = Bounds(Find<Button>("Tab_" + tab.Replace(':', '_')));
            var from = new Point(source.Center.X + (dragCount++ % 2 == 0 ? -4 : 4), source.Center.Y);
            window.MouseDown(from, MouseButton.Left); window.MouseMove(from + new Vector(10, 10));
            Require(Find<DockSurface>("WorkspaceWorkbench").IsDragging, "Pointer input did not start a drag draft.");
            window.MouseMove(to); window.MouseUp(to, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Require(LayoutSession.IsValid(window.Layout.State), "Pointer placement broke layout invariants.");
        }
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var fallbackGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == DockState.SpecsTool));
        var fallbackTab = Bounds(Find<Button>("Tab_plugin_spec-dialect_specs"));
        var fallbackEpoch = window.Layout.Epoch;
        Drag(DockState.SpecsTool, new Point(fallbackTab.Center.X, fallbackTab.Top + 4));
        Require(window.Layout.Epoch == fallbackEpoch && window.Layout.Group(fallbackGroup.Id).Tools.Select(tab => tab.Id).SequenceEqual(new[] { DockState.SpecsTool, "files" }),
            "A rail entry's own insertion halves are no-ops that paint and commit nothing.");
        Drag(DockState.SpecsTool, new Point(Bounds(Find<Button>("Tab_files")).Center.X, Bounds(Find<Button>("Tab_files")).Bottom - 4));
        Require(window.Layout.Group(fallbackGroup.Id).Tools.Select(tab => tab.Id).SequenceEqual(new[] { "files", DockState.SpecsTool }) &&
            window.Layout.State.Groups.Count(group => group.Region == "right") == 2,
            "An entry's lower half must reorder within its pane without creating a neighbouring group.");
        window.Width = 2200; window.Height = 920;
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        window.Layout.Geometry(state =>
        {
            var rows = state.Groups.Where(group => group.Region == "right").ToArray();
            rows[0].Weight = .17; rows[1].Weight = .83;
        });
        var compactGroup = window.Layout.State.Groups.First(group => group.Region == "right");
        var compactHeader = Bounds(Find<Grid>("GroupHeader_" + compactGroup.Id));
        var compactTab = window.Layout.State.Groups.Where(group => group.Region == "right").ElementAt(1).Tools[0].Id;
        var compactOrigin = Bounds(Find<Button>("Tab_" + compactTab)).Center;
        window.MouseDown(compactOrigin, MouseButton.Left); window.MouseMove(compactOrigin + new Vector(10, 10));
        window.MouseMove(compactHeader.Center);
        var compactHighlights = Find<DockSurface>("WorkspaceWorkbench").Children.OfType<Canvas>().Single().Children.OfType<Border>()
            .Where(item => item.Background is Avalonia.Media.SolidColorBrush { Color.A: 51 }).ToArray();
        Require(compactHighlights.Length == 1 && compactHighlights[0].Height > 32 && compactHighlights[0].Width < compactHeader.Width,
            "Overlapping compact-pane targets must emphasize the nearer creation target rather than the entire header.");
        window.MouseUp(compactHeader.Center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.State.Groups.Where(group => group.Region == "right").First().Tools.Single().Id == compactTab &&
            window.Layout.Group(compactGroup.Id).Tools.Select(tab => tab.Id).SequenceEqual(new[] { DockState.SpecsTool, "files" }),
            "Corner-distance collision must create a compact pane's nearer neighboring group instead of always preferring its header.");
        window.Width = 1440;
        foreach (var region in new[] { "left" })
            foreach (var before in new[] { true, false })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
                var group = window.Layout.State.Groups.Single(item => item.Region == region);
                var body = Bounds(Find<Border>("DockBody_" + group.Id));
                var point = region == "left" ? new Point(body.Center.X, before ? body.Top + 20 : body.Bottom - 20) :
                    new Point(before ? body.Left + 20 : body.Right - 20, body.Center.Y);
                Drag("review", point);
                var groups = window.Layout.State.Groups.Where(item => item.Region == region).ToArray();
                Require(groups.Length == 2 && groups[before ? 0 : 1].Tools.Any(tab => tab.Id == "review"),
                    $"Pointer {region} {(before ? "before" : "after")} insertion chose the wrong destination.");
            }
        foreach (var region in new[] { "left" })
            foreach (var before in new[] { true, false })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
                var group = window.Layout.State.Groups.First(item => item.Region == region);
                var originalCount = window.Layout.State.Groups.Count(item => item.Region == region);
                var source = window.Layout.State.Groups.First(item => item.Region != region && item.Tools.Count > 0 && item.Tools.All(tab => tab.Id != "projects")).Tools[0].Id;
                window.Layout.HidePane(group.Id);
                Require(!window.GetLogicalDescendants().OfType<Grid>().Any(item => item.Name == "GroupHeader_" + group.Id),
                    "A folded side pane must occupy no interior strip.");
                var zone = Bounds(Find<Border>($"RailBoundary_{region}_{(before ? 0 : 1)}"));
                Drag(source, zone.Center);
                var groups = window.Layout.State.Groups.Where(item => item.Region == region).ToArray();
                Require(groups.Length == originalCount + 1 && groups[before ? 0 : 1].Tools.Any(tab => tab.Id == source) &&
                    window.Layout.Group(group.Id).Folded,
                    "A rail boundary must insert a neighbour while preserving the folded pane.");
            }
        foreach (var region in new[] { "left" })
            foreach (var limited in new[] { false, true })
            {
                var preset = DockState.Preset("balanced");
                if (limited) preset.SideLimit = 1;
                window.Layout.ApplyPreset(preset);
                var group = window.Layout.State.Groups.First(item => item.Region == region);
                var originalCount = window.Layout.State.Groups.Count(item => item.Region == region);
                var source = window.Layout.State.Groups.First(item => item.Region != region && item.Tools.Count > 0 && item.Tools.All(tab => tab.Id != "projects")).Tools[0].Id;
                window.Layout.HidePane(group.Id);
                var entry = Bounds(Find<Button>("Tab_" + group.Tools[0].Id.Replace(':', '_')));
                if (limited)
                {
                    var epochBefore = window.Layout.Epoch;
                    Drag(source, Bounds(Find<Border>($"RailBoundary_{region}_0")).Center);
                    Require(window.Layout.Epoch == epochBefore, "A boundary beyond the group limit must accept nothing.");
                }
                Drag(source, new Point(entry.Center.X, entry.Bottom - 4));
                Require(window.Layout.State.Groups.Count(item => item.Region == region) == originalCount &&
                    window.Layout.Group(group.Id).Tools.Any(tab => tab.Id == source) && !window.Layout.Group(group.Id).Folded,
                    "Dropping on a folded pane's entry must join it and unfold it in the same transition.");
            }
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        window.Layout.Geometry(state => state.SideLimit = 1);
        var left = window.Layout.State.Groups.Single(group => group.Region == "left");
        var bounds = Bounds(Find<Border>("DockBody_" + left.Id));
        var epoch = window.Layout.Epoch;
        Drag("review", new Point(bounds.Center.X, bounds.Top + 20));
        Require(window.Layout.Epoch == epoch && window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "review")).Region == "right",
            "Side limit rejected target still committed a pointer drop.");
        Require(AutomationProperties.GetName(Find<Button>("HidePane_" + window.Layout.State.Groups.First(group => group.Region == "right").Id)) == "Hide",
            "Icon-only pane control has no accessible name.");
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var surface = Find<DockSurface>("WorkspaceWorkbench");
        IPointer? pointer = null;
        window.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        var from = Bounds(Find<Button>("Tab_files")).Center;
        window.MouseDown(from, MouseButton.Left); window.MouseUp(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        from = Bounds(Find<Button>("Tab_files")).Center;
        epoch = window.Layout.Epoch;
        var sourceGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files")).Id;
        window.MouseDown(from, MouseButton.Left); window.MouseMove(from + new Vector(20, 40));
        Require(surface.IsDragging && pointer is not null, "Capture-loss test did not start a drag.");
        var canvas = surface.Children.OfType<Canvas>().Single();
        var preview = canvas.Children.OfType<Panel>().SingleOrDefault(item => item.Name == "DragPreview");
        var previewOrigin = preview is null ? default : canvas.TranslatePoint(new Point(Canvas.GetLeft(preview), Canvas.GetTop(preview)), window)!.Value;
        Require(preview is not null && previewOrigin.X > from.X && previewOrigin.Y > from.Y &&
            preview.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Files"),
            "A dragged tab shows a preview of itself under the pointer.");
        pointer!.Capture(null); Dispatcher.UIThread.RunJobs();
        window.MouseUp(from + new Vector(20, 40), MouseButton.Left);
        Require(!surface.IsDragging && window.Layout.Epoch == epoch &&
            window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files")).Id == sourceGroup,
            "Losing pointer capture changed the source placement or epoch.");
        Drag("files", new Point(-20, -20));
        Require(!surface.IsDragging && window.Layout.Epoch == epoch,
            "Dropping outside the workbench changed the layout.");
        from = Bounds(Find<Button>("Tab_files")).Center;
        var finger = window.TouchBegin(from, RawInputModifiers.None);
        window.TouchMove(finger, from + new Vector(0, 20), RawInputModifiers.None);
        Require(!surface.IsDragging, "A touch swipe on a tab must remain available for scrolling.");
        window.TouchEnd(finger, from + new Vector(0, 20), RawInputModifiers.None);
        epoch = window.Layout.Epoch;
        finger = window.TouchBegin(from, RawInputModifiers.None);
        using (var hold = new CancellationTokenSource(550)) Dispatcher.UIThread.MainLoop(hold.Token);
        window.TouchMove(finger, from + new Vector(0, 20), RawInputModifiers.None);
        Require(surface.IsDragging, "Holding a tab before moving must start a touch drag.");
        window.TouchMove(finger, new Point(-20, -20), RawInputModifiers.None);
        window.TouchEnd(finger, new Point(-20, -20), RawInputModifiers.None);
        Require(!surface.IsDragging && window.Layout.Epoch == epoch, "A cancelled touch drag must preserve placement.");
        foreach (var region in new[] { "left" })
        {
            window.Layout.ApplyPreset(DockState.Preset("balanced"));
            var tab = window.Layout.State.Groups.First(group => group.Region != region && group.Tools.Count > 0).Tools[0].Id;
            window.Layout.Visible(region, false);
            var hiddenRail = Find<Border>(region + "Rail");
            var firstEntry = hiddenRail.GetLogicalDescendants().OfType<Button>().First();
            Require(Bounds(hiddenRail).Width == 40 && Bounds(hiddenRail).Height == Bounds(surface).Height &&
                firstEntry.Bounds.Size == new Size(32, 32) && !hiddenRail.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Tag, "pressed")),
                "A hidden side keeps its full-height 40px rail with 32px entries, none of them pressed.");
            var destination = new Point(Bounds(hiddenRail).Center.X, Bounds(hiddenRail).Bottom - 40);
            Drag(tab, destination);
            Require((region == "left" ? window.Layout.State.LeftVisible : window.Layout.State.RightVisible) &&
                window.Layout.State.Groups.Single(group => group.Tools.Any(item => item.Id == tab)).Region == region,
                "Hidden-side drop did not restore its destination region.");
            window.Layout.Visible(region, false);
            destination = Bounds(Find<Border>(region + "Rail").GetLogicalDescendants().OfType<Button>().First()).Center;
            window.MouseDown(destination, MouseButton.Left); window.MouseUp(destination, MouseButton.Left);
            Require(region == "left" ? window.Layout.State.LeftVisible : window.Layout.State.RightVisible,
                "A hidden side's rail entry did not reveal its region.");
        }
        window.Width = 1680; window.Height = 1000;
        foreach (var region in new[] { "left", "right" })
        {
            window.Layout.ApplyPreset(DockState.Preset("balanced"));
            var handle = Find<ResizeHandle>(region + "Separator");
            var frame = (Grid)handle.Parent!;
            var start = Bounds(handle).Center;
            var workbench = Find<DockSurface>("WorkspaceWorkbench");
            var initial = region == "left" ? window.Layout.State.LeftWidth : window.Layout.State.RightWidth;
            var untouched = region == "left" ? window.Layout.State.RightWidth : window.Layout.State.LeftWidth;
            var epochBefore = window.Layout.Epoch;
            var destination = start + new Vector((region == "left" ? 1 : -1) * (.65 - initial) * (workbench.Bounds.Width - 80), 0);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(destination);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var expectedActive = .65;
            var expectedNeighbor = .15;
            var activeColumn = region == "left" ? 0 : 4;
            var neighborColumn = region == "left" ? 4 : 0;
            Require(Math.Abs(frame.ColumnDefinitions[activeColumn].Width.Value - expectedActive) < .001 &&
                Math.Abs(frame.ColumnDefinitions[neighborColumn].Width.Value - expectedNeighbor) < .001 &&
                Math.Abs(frame.ColumnDefinitions[2].Width.Value - .2) < .001,
                $"Side resize did not project the reference's neighboring panes and minimum center for {region}.");
            Require(window.Layout.Epoch == epochBefore && window.Layout.State.LeftWidth == .18 && window.Layout.State.RightWidth == .28,
                "Live neighbor compression persisted uncommitted widths.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs();
            handle = Find<ResizeHandle>(region + "Separator");
            Require(window.Layout.Epoch == epochBefore && Math.Abs(((Grid)handle.Parent!).ColumnDefinitions[activeColumn].Width.Value - initial) < .001,
                "Canceling side resize did not restore the original live geometry.");
            start = Bounds(handle).Center;
            destination = start + new Vector((region == "left" ? 1 : -1) * (.65 - initial) * (workbench.Bounds.Width - 80), 0);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(destination); window.MouseUp(destination, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Require(Math.Abs((region == "left" ? window.Layout.State.LeftWidth : window.Layout.State.RightWidth) - expectedActive) < .001 &&
                (region == "left" ? window.Layout.State.RightWidth : window.Layout.State.LeftWidth) == untouched,
                "A side separator committed the untouched neighbor's compressed width.");
            var savedLeft = window.Layout.State.LeftWidth;
            var savedRight = window.Layout.State.RightWidth;
            var savedEpoch = window.Layout.Epoch;
            window.Width = 880;
            handle = Find<ResizeHandle>(region + "Separator");
            var narrow = (Grid)handle.Parent!;
            Require(Math.Abs(narrow.ColumnDefinitions[2].Width.Value - .4) < .001 &&
                window.Layout.State.LeftWidth == savedLeft && window.Layout.State.RightWidth == savedRight && window.Layout.Epoch == savedEpoch,
                "Narrow viewport compression rewrote the frame or lost the center minimum.");
            window.Width = 1680;
            handle = Find<ResizeHandle>(region + "Separator");
            Require(Math.Abs(((Grid)handle.Parent!).ColumnDefinitions[2].Width.Value - .2) < .001 && window.Layout.Epoch == savedEpoch,
                "Widening the viewport did not restore the saved side projection.");
        }
        window.Layout.Visible("left", true);
        window.Layout.Visible("right", true);
        foreach (var region in new[] { "left", "right" })
            foreach (var share in new[] { .75, .55, .4 })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
                var minimum = Math.Clamp(220 / (Bounds(Find<DockSurface>("WorkspaceWorkbench")).Width - 80), .08, .35);
                var requested = minimum * share;
                var handle = Find<ResizeHandle>(region + "Separator");
                var start = Bounds(handle).Center;
                var initial = region == "left" ? window.Layout.State.LeftWidth : window.Layout.State.RightWidth;
                var workbenchWidth = Bounds(Find<DockSurface>("WorkspaceWorkbench")).Width - 80;
                var destination = start + new Vector((region == "left" ? 1 : -1) * (requested - initial) * workbenchWidth, 0);
                window.MouseDown(start, MouseButton.Left); window.MouseMove(destination);
                Require((region == "left" ? window.Layout.State.LeftWidth : window.Layout.State.RightWidth) == initial,
                    "Side resize preview must not persist a draft width.");
                window.MouseUp(destination, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                var visible = region == "left" ? window.Layout.State.LeftVisible : window.Layout.State.RightVisible;
                var retained = region == "left" ? window.Layout.State.LeftWidth : window.Layout.State.RightWidth;
                Require(visible == (share > .5) && Math.Abs(retained - (visible ? minimum : initial)) < .001,
                    "Side resize must snap to the 220px side minimum or collapse below half of it while retaining expanded width.");
            }
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var separator = Find<ResizeHandle>("leftSeparator");
        Bounds(separator);
        var peer = ControlAutomationPeer.CreatePeerForElement(separator)!;
        var range = (IRangeValueProvider)peer;
        Require(peer.GetAutomationControlType() == AutomationControlType.Separator &&
            peer.GetName() == "Vertical pane separator" && !range.IsReadOnly &&
            Math.Abs(range.Value - window.Layout.State.LeftWidth * 100) < .001 && range.Minimum < range.Maximum,
            "Separator automation does not expose orientation and canonical size limits.");
        var targetWidth = (range.Minimum + range.Maximum) / 2;
        range.SetValue(targetWidth); Dispatcher.UIThread.RunJobs();
        Require(Math.Abs(window.Layout.State.LeftWidth * 100 - targetWidth) < .001,
            "Accessible resize did not commit the pane width.");
        separator = Find<ResizeHandle>("leftSeparator"); Bounds(separator);
        range = (IRangeValueProvider)ControlAutomationPeer.CreatePeerForElement(separator)!;
        epoch = window.Layout.Epoch;
        try { range.SetValue(range.Maximum + 1); throw new InvalidOperationException("Accessible resize accepted an invalid size."); }
        catch (ArgumentOutOfRangeException) { }
        Require(window.Layout.Epoch == epoch, "Rejected accessible resize changed the layout.");
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        separator = Find<ResizeHandle>("leftSeparator"); Bounds(separator); separator.Focus();
        var initialWidth = window.Layout.State.LeftWidth;
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Require(Math.Abs(window.Layout.State.LeftWidth - initialWidth - .1) < .001,
            "Separator arrow input must resize by ten percent of the group rather than a fixed pixel step.");
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Require(Math.Abs(window.Layout.State.LeftWidth - initialWidth - .2) < .001 && Find<ResizeHandle>("leftSeparator").IsFocused,
            "Repeated keyboard resize must retain focus without manual refocusing.");
        separator = Find<ResizeHandle>("leftSeparator"); Bounds(separator); separator.Focus();
        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Require(Math.Abs(window.Layout.State.LeftWidth * 100 - ((IRangeValueProvider)ControlAutomationPeer.CreatePeerForElement(
                Find<ResizeHandle>("leftSeparator"))!).Maximum) < .001,
            "End must resize to the maximum permitted side width.");
        separator = Find<ResizeHandle>("leftSeparator"); Bounds(separator); separator.Focus();
        window.KeyPress(Key.Left, RawInputModifiers.Shift, PhysicalKey.ArrowLeft, null);
        Require(!window.Layout.State.LeftVisible, "Shift-arrow must move through the full range and collapse the side.");
        window.Width = 2200; window.Height = 1600;
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        foreach (var edge in new[] { "right", "bottom" })
            Require(!window.Layout.NewGroup(window.Layout.State.Center.Leaves().Single(), edge), "The main column must refuse custom splits.");
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var centerGroup = window.Layout.State.Center.Leaves().Single();
        surface.WorkspaceRoot = () => "/work/space";
        window.Layout.Open(new DockTab("probe", "probe.txt", "source", "hello.txt"), true, centerGroup);
        window.Layout.Open(new DockTab("terminal:probe", "Terminal probe", "terminal"), true, centerGroup);
        Dispatcher.UIThread.RunJobs();
        var terminalMenu = Find<Button>("Tab_terminal_probe").ContextMenu!;
        Require(!terminalMenu.Items.OfType<MenuItem>().Any(item => item.Name is "TabCopyPath" or "TabCopyRelativePath"),
            "A terminal tab has no file path to copy.");
        var fileMenu = Find<Button>("Tab_probe").ContextMenu!;
        foreach (var (name, expected) in new[] { ("TabCopyRelativePath", "hello.txt"), ("TabCopyPath", Path.Combine("/work/space", "hello.txt")) })
        {
            fileMenu.Items.OfType<MenuItem>().Single(item => item.Name == name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            E2E.E2eWorkspace.Until(() => window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult() == expected);
        }
        window.Layout.Close(centerGroup, "terminal:probe");
        var probe = Bounds(Find<Button>("Tab_probe"));
        epoch = window.Layout.Epoch;
        Drag("probe", probe.Center);
        Require(window.Layout.Epoch == epoch, "No-op tab drop committed a transition.");
        window.Width = 1680; window.Height = 1000;
        var section = Bounds(Find<Grid>("CenterPanel"));
        // The main column offers tab groups only: no edge of its body is a drop target, from the menu or a drag.
        foreach (var destination in new[] { new Point(section.Right - 8, section.Center.Y), new Point(section.Left + 8, section.Center.Y),
                     new Point(section.Center.X, section.Bottom - 8), new Point(section.Center.X, section.Top + 48) })
        {
            from = Bounds(Find<Button>("Tab_probe")).Center;
            window.MouseDown(from, MouseButton.Left); window.MouseMove(destination);
            Require(!surface.Children.OfType<Canvas>().Single().Children.OfType<Border>().Any(hint =>
                    new Rect(Canvas.GetLeft(hint), Canvas.GetTop(hint), hint.Width, hint.Height).Intersects(section)),
                "The main column must expose no split targets.");
            window.MouseUp(destination, MouseButton.Left);
            Require(window.Layout.State.Center.IsLeaf && window.Layout.Tabs(centerGroup).Count(tab => tab.Id == "probe") == 1, "A drop on the main column must not split it.");
        }
        var mainHeader = Find<Grid>("GroupHeader_" + centerGroup);
        Require(mainHeader.ContextMenu!.Items.OfType<MenuItem>().All(item => item.Header is not string header || !header.Contains("split", StringComparison.OrdinalIgnoreCase)) &&
            Find<Button>("Tab_probe").ContextMenu!.Items.OfType<MenuItem>().All(item => item.Header is not string title || !title.StartsWith("Split", StringComparison.Ordinal)),
            "Main column menus must offer no split actions.");
        window.Layout.Close(centerGroup, "probe");
        Dispatcher.UIThread.RunJobs();
        var focusStart = window.Layout.State.Groups.First(group => group.Region == "left").Id;
        Find<Grid>("GroupHeader_" + focusStart).Focus();
        Require(window.Layout.View.FocusedGroup == focusStart, "Keyboard focus did not update pane attention.");
        window.KeyPress(Key.F6, RawInputModifiers.Control, PhysicalKey.F6, null);
        Dispatcher.UIThread.RunJobs();
        for (var index = 0; index < window.Layout.State.Groups.Count && window.Layout.View.FocusedGroup != centerGroup; index++)
        {
            window.KeyPress(Key.F6, RawInputModifiers.Control, PhysicalKey.F6, null); Dispatcher.UIThread.RunJobs();
        }
        Require(window.Layout.View.FocusedGroup == centerGroup && Find<Grid>("GroupHeader_" + centerGroup).IsFocused,
            "Group traversal did not focus the empty center header.");
        var foldedGroup = window.Layout.State.Groups.First(group => group.Region == "right");
        window.Layout.Fold(foldedGroup.Id);
        Find<Grid>("GroupHeader_" + centerGroup).Focus();
        for (var index = 0; index < window.Layout.State.Groups.Count && window.Layout.View.FocusedGroup != foldedGroup.Id; index++)
        {
            window.KeyPress(Key.F6, RawInputModifiers.Control, PhysicalKey.F6, null); Dispatcher.UIThread.RunJobs();
        }
        Require(window.Layout.View.FocusedGroup == foldedGroup.Id && Find<Button>("Tab_" + window.Layout.Selected(foldedGroup.Id)!.Id.Replace(':', '_')).IsFocused,
            "Group traversal did not focus the folded pane's rail entry.");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Dispatcher.UIThread.RunJobs();
        Require(!window.Layout.Group(foldedGroup.Id).Folded, "Keyboard restore did not expand the folded pane.");
        RailChecks(window, Find<Control>);
        window.Close();
        Console.WriteLine("PASS pointer side insertion, group limits, capture loss, outside cancellation and control names");
    }
}