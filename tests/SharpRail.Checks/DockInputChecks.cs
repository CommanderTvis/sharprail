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
        var window = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(root + "-dock-input"), E2E.E2eTerminals.Plain);
        window.Show();
        var deadline = Awake.Now.AddSeconds(15);
        while ((!window.WorkspaceMounted || !window.Layout.Tools.Any(tool => tool.Id == "changes")) && Awake.Now < deadline)
        {
            // Workspace startup crosses an animation frame before probing Git; draining jobs alone cannot render it.
            using var stop = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(stop.Token); window.UpdateLayout();
        }
        Require(window.WorkspaceMounted && window.Layout.Tools.Any(tool => tool.Id == "changes"), "Dock input Git workspace did not mount.");
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
            var add = Find<Button>("AddToGroup_" + group.Id);
            IEnumerable<DockToolInfo> Hidden() => window.Layout.Tools.Where(tool => tool.Region == group.Region &&
                !window.Layout.State.Groups.Any(pane => pane.Tools.Any(tab => tab.Id == tool.Id)));
            Require(add.IsVisible == (group.Tools.Count > 0 || Hidden().Any()), "Add is visible when it has a tool to show or a terminal pane to create.");
            if (!add.IsVisible) continue;
            add.ContextMenu!.Open(add);
            Dispatcher.UIThread.RunJobs();
            Require(add.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Name).Where(name => name?.StartsWith("ShowTool_", StringComparison.Ordinal) == true)
                .SequenceEqual(Hidden().Select(tool => "ShowTool_" + tool.Id.Replace(':', '_'))) &&
                add.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Name == "NewTerminalBelow_" + group.Id) == group.Tools.Count > 0,
                "Add offers the current catalogue without duplicating already-placed singleton tools, and a tool pane a terminal pane below.");
            add.ContextMenu.Close();
        }
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var projects = Find<Button>("Tab_projects");
        var projectsPoint = Bounds(projects).Center;
        window.MouseDown(projectsPoint, MouseButton.Right); window.MouseUp(projectsPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(projects.ContextMenu?.IsOpen == true, "Right-click did not open the pane tab menu.");
        var closePane = projects.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Close"));
        closePane.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        projects.ContextMenu.Close(); Dispatcher.UIThread.RunJobs();
        Require(!window.Layout.State.Groups.SelectMany(group => group.Tools).Any(tab => tab.Id == "projects"),
            "The pane tab's Close action did not hide the tool.");
        var revealGroup = window.Layout.State.Groups.First(group => group.Region != "center");
        var reveal = Find<Button>("AddToGroup_" + revealGroup.Id);
        reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        reveal.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Show Projects"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        reveal.ContextMenu.Close(); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Group(revealGroup.Id).Tools.Any(tab => tab.Id == "projects"),
            "Add to this group did not restore the hidden singleton to that group.");
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var initialCenterGroup = window.Layout.State.Center.Leaves().Single();
        window.Width = 1680; window.Height = 1000;
        var centerHeader = Find<Grid>("GroupHeader_" + initialCenterGroup);
        var headerPoint = Bounds(centerHeader).TopLeft + new Vector(12, 12);
        window.MouseDown(headerPoint, MouseButton.Right); window.MouseUp(headerPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(centerHeader.ContextMenu?.IsOpen == true &&
            !centerHeader.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Remove group")).IsEnabled,
            "Empty pane header context menu is missing or allows removing the final center group.");
        Require(centerHeader.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "New split right")).IsEnabled &&
            centerHeader.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "New split below")).IsEnabled,
            "Split actions must use the measured pane size when the context menu opens.");
        centerHeader.ContextMenu!.Close();
        var rightGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == DockState.SpecsTool));
        window.Layout.Select(rightGroup.Id, "files");
        projects = Find<Button>("Tab_projects");
        projectsPoint = Bounds(projects).Center;
        window.MouseDown(projectsPoint, MouseButton.Right); window.MouseUp(projectsPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(projects.ContextMenu!.IsOpen && projects.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane"))
            .Items.OfType<MenuItem>().Any(item => Equals(item.Header, "right pane (Specs, Files)")),
            "Move menu must name a destination pane by its contents.");
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
        var compactOrigin = Bounds(Find<Button>("Tab_projects")).Center;
        window.MouseDown(compactOrigin, MouseButton.Left); window.MouseMove(compactOrigin + new Vector(10, 10));
        window.MouseMove(compactHeader.Center);
        var compactHighlights = Find<DockSurface>("WorkspaceWorkbench").Children.OfType<Canvas>().Single().Children.OfType<Border>()
            .Where(item => item.Background is Avalonia.Media.SolidColorBrush { Color.A: 51 }).ToArray();
        Require(compactHighlights.Length == 1 && compactHighlights[0].Height > 32 && compactHighlights[0].Width < compactHeader.Width,
            "Overlapping compact-pane targets must emphasize the nearer creation target rather than the entire header.");
        window.MouseUp(compactHeader.Center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.State.Groups.Where(group => group.Region == "right").First().Tools.Single().Id == "projects" &&
            window.Layout.State.Groups.Count(group => group.Region == "right") == 3 &&
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
        foreach (var region in new[] { "left", "right" })
            foreach (var before in new[] { true, false })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
                var group = window.Layout.State.Groups.First(item => item.Region == region);
                var originalCount = window.Layout.State.Groups.Count(item => item.Region == region);
                var source = window.Layout.State.Groups.First(item => item.Region != region && item.Tools.Count > 0).Tools[0].Id;
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
        foreach (var region in new[] { "left", "right" })
            foreach (var limited in new[] { false, true })
            {
                var preset = DockState.Preset("balanced");
                if (limited) preset.SideLimit = 1;
                window.Layout.ApplyPreset(preset);
                var group = window.Layout.State.Groups.First(item => item.Region == region);
                var originalCount = window.Layout.State.Groups.Count(item => item.Region == region);
                var source = window.Layout.State.Groups.First(item => item.Region != region && item.Tools.Count > 0).Tools[0].Id;
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
        foreach (var region in new[] { "left", "right" })
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
            foreach (var requested in new[] { .06, .04, .03 })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
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
                Require(visible == (requested >= .04) && Math.Abs(retained - (visible ? .08 : initial)) < .001,
                    "Side resize must snap to eight percent or collapse below four percent while retaining expanded width.");
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
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        foreach (var (edge, horizontal, minimum) in new[] { ("right", true, 320d), ("bottom", false, 180d) })
        {
            window.Layout.ApplyPreset(DockState.Preset("balanced"));
            var firstGroup = window.Layout.State.Center.Leaves().Single();
            Require(window.Layout.NewGroup(firstGroup, edge) && window.Layout.NewGroup(firstGroup, edge),
                "Nested resize fixture could not create three center panes.");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var centerSeparator = window.GetLogicalDescendants().OfType<ResizeHandle>()
                .Where(item => item.Name?.StartsWith("CenterSeparator_", StringComparison.Ordinal) == true && item.Parent is Grid grid &&
                    grid.GetLogicalDescendants().OfType<Grid>().Any(child => child.Name == "CenterPanel") &&
                    (horizontal ? grid.ColumnDefinitions.Count == 3 && grid.RowDefinitions.Count == 0 :
                        grid.RowDefinitions.Count == 3 && grid.ColumnDefinitions.Count == 0))
                .OrderByDescending(item => horizontal ? ((Grid)item.Parent!).Bounds.Width : ((Grid)item.Parent!).Bounds.Height).First();
            var splitFrame = (Grid)centerSeparator.Parent!;
            var outer = Find<ResizeHandle>("leftSeparator");
            var outerRange = (IRangeValueProvider)ControlAutomationPeer.CreatePeerForElement(outer)!;
            var workbench = Find<DockSurface>("WorkspaceWorkbench");
            Require(Math.Abs(outerRange.Maximum - Math.Min(.7, Math.Min(1 - window.Layout.State.RightWidth - 1e-6, 1 - 320 / (workbench.Bounds.Width - 80))) * 100) < .001,
                "Nested center topology must not increase the outer separator's reserved center width.");
            var dimension = horizontal ? splitFrame.Bounds.Width : splitFrame.Bounds.Height;
            var firstExtent = horizontal ? splitFrame.ColumnDefinitions[0].ActualWidth : splitFrame.RowDefinitions[0].ActualHeight;
            Require(Math.Abs(firstExtent - (dimension - 1) / 2) <= 1,
                $"Decimal-comma split halves are unequal: {edge}, frame={dimension}, first={firstExtent}, ratio={window.Layout.State.Center.Ratio}.");
            var centerRange = (IRangeValueProvider)ControlAutomationPeer.CreatePeerForElement(centerSeparator)!;
            Require(Math.Abs(centerRange.Minimum - minimum / dimension * 100) < .001 &&
                Math.Abs(centerRange.Maximum - (1 - minimum / dimension) * 100) < .001,
                "Nested center resize must use direct-child minima and the whole split dimension.");
            centerRange.SetValue(centerRange.Minimum); Dispatcher.UIThread.RunJobs();
            Require(Math.Abs(window.Layout.State.Center.Ratio - minimum / dimension) < .001,
                "Resizing a nested center branch must commit the reference minimum ratio.");
        }
        System.Globalization.CultureInfo.CurrentCulture = previousCulture;
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
        epoch = window.Layout.Epoch;
        var section = Bounds(Find<Grid>("CenterPanel"));
        from = Bounds(Find<Button>("Tab_probe")).Center;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(section.Right - 8, section.Center.Y));
        var hints = surface.Children.OfType<Canvas>().Single().Children.OfType<Border>().ToArray();
        var halfPreview = hints.Single(item => item.BorderThickness.Left == 2);
        Require(Math.Abs(halfPreview.Width - section.Width / 2) < .01 && halfPreview.Height == section.Height &&
            halfPreview.CornerRadius.TopLeft == 4 &&
            halfPreview.Background is Avalonia.Media.SolidColorBrush { Color.A: 51 } &&
            halfPreview.BorderBrush is Avalonia.Media.SolidColorBrush { Color.A: 255 } &&
            hints.Any(item => Math.Abs(item.Width - section.Width / 5) < .01 && item.Height == section.Height / 2 &&
                item.Background is Avalonia.Media.SolidColorBrush { Color.A: 26 }),
            "Center drag targets must use reference edge rectangles, tint and a rounded full-height half preview.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(new Point(section.Right - 8, section.Center.Y), MouseButton.Left);
        Drag("probe", new Point(section.Right - 8, section.Bottom - 8));
        Require(window.Layout.Epoch == epoch && window.Layout.State.Center.Leaves().Count() == 1,
            "A center corner outside the reference split target must not accept a drop.");
        window.MinWidth = 0; window.MinHeight = 0;
        foreach (var (width, height, allowed) in new[] { (640d, 360d, true), (639d, 359d, false) })
        {
            var panel = Find<Grid>("CenterPanel");
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var measured = Bounds(panel);
                if (Math.Abs(measured.Width - width) < .001 && Math.Abs(measured.Height - height) < .001) break;
                window.Width += (width - measured.Width) * window.Width / measured.Width;
                window.Height += (height - measured.Height) * window.Height / measured.Height;
            }
            section = Bounds(panel);
            Require(Math.Abs(section.Width - width) < .01 && Math.Abs(section.Height - height) < .01,
                "Split boundary fixture did not reach the requested whole-pane dimensions.");
            var boundaryHeader = Find<Grid>("GroupHeader_" + centerGroup);
            var menuPoint = Bounds(boundaryHeader).BottomRight - new Vector(2, 2);
            window.MouseDown(menuPoint, MouseButton.Right); window.MouseUp(menuPoint, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var splitItems = boundaryHeader.ContextMenu!.Items.OfType<MenuItem>()
                .Where(item => Equals(item.Header, "New split right") || Equals(item.Header, "New split below")).ToArray();
            Require(boundaryHeader.ContextMenu.IsOpen && splitItems.Length == 2 && splitItems.All(item => item.IsEnabled == allowed),
                $"Split menus must use the inclusive 640 by 360 whole-pane thresholds: pane={section.Size}, open={boundaryHeader.ContextMenu.IsOpen}, allowed={allowed}, items={string.Join(", ", splitItems.Select(item => $"{item.Header}={item.IsEnabled}"))}.");
            boundaryHeader.ContextMenu.Close();
            foreach (var destination in new[] { new Point(section.Right - 8, section.Center.Y),
                         new Point(section.Center.X, section.Bottom - 8) })
            {
                var source = Bounds(Find<Button>("Tab_probe"));
                from = new Point(source.Left + (dragCount++ % 2 == 0 ? 16 : 36), source.Center.Y);
                window.MouseDown(from, MouseButton.Left); window.MouseMove(destination);
                Require(surface.Children.OfType<Canvas>().Single().Children.OfType<Border>()
                        .Any(item => item.BorderThickness.Left == 2) == allowed,
                    "Drag split targets must use the same whole-pane thresholds as the menu.");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.MouseUp(destination, MouseButton.Left);
            }
        }
        window.Width = 900; window.Height = 360;
        var centerBody = Bounds(Find<Border>("DockBody_" + centerGroup));
        var constrained = Bounds(Find<Grid>("CenterPanel"));
        Require(constrained.Width < 640 && constrained.Height < 360, "Minimum-size fixture was not constrained.");
        foreach (var destination in new[] { new Point(centerBody.Right - 6, centerBody.Center.Y),
                     new Point(centerBody.Center.X, centerBody.Bottom - 6) })
        {
            Drag("probe", destination);
            Require(window.Layout.State.Center.Leaves().Count() == 1 && window.Layout.Epoch == epoch,
                "Undersized center accepted a split or mutated a rejected drop.");
        }
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