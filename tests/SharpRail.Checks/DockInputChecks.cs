using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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

    internal static void Run(string root)
    {
        var window = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(root + "-dock-input"), E2E.E2eTerminals.Plain);
        window.Show();
        var deadline = Awake.Now.AddSeconds(15);
        while (!window.WorkspaceMounted && Awake.Now < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Require(window.WorkspaceMounted, "Dock input workspace did not mount.");
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
        Require(!Find<Button>("RemoveGroup_" + window.Layout.State.Center.Leaves().Single()).IsEnabled,
            "The final empty center group's remove button must be disabled.");
        var terminalButtons = window.GetLogicalDescendants().OfType<Button>().Count(button => button.IsVisible && button.Name?.StartsWith("NewTerminal_", StringComparison.Ordinal) == true);
        Require(terminalButtons == window.Layout.State.Groups.Count(group => !group.Folded && window.Layout.Selected(group.Id)?.IsTool != true) &&
            !window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name?.StartsWith("AddToGroup_", StringComparison.Ordinal) == true),
            "Only resource and empty views must offer terminal creation, and Add must not duplicate already-placed singleton tools.");
        var projectsGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "projects")).Id;
        Require(!Find<Button>("NewTerminal_" + projectsGroup).IsVisible, "Projects must not show a terminal opener.");
        window.Layout.NewTerminal(projectsGroup);
        var terminalTab = window.Layout.Selected(projectsGroup)!;
        Require(Find<Button>("NewTerminal_" + projectsGroup).IsVisible, "A terminal in a side group must offer terminal creation.");
        window.Layout.Select(projectsGroup, "projects");
        Require(!Find<Button>("NewTerminal_" + projectsGroup).IsVisible, "Selecting Projects in a mixed group must hide terminal creation.");
        window.Layout.Select(projectsGroup, terminalTab.Id);
        Require(Find<Button>("NewTerminal_" + projectsGroup).IsVisible, "Selecting a terminal in a mixed group must restore terminal creation.");
        window.Layout.Close(projectsGroup, terminalTab.Id);
        foreach (var (value, label) in new[]
        {
            ("center-left", "Below center and left"), ("center-right", "Below center and right"),
            ("full", "Full width"), ("center", "Below center")
        })
        {
            var alignment = Find<Button>("BottomAlignment");
            var point = Bounds(alignment).Center;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Require(alignment.ContextMenu?.IsOpen == true, "Bottom alignment menu did not open from pointer input.");
            var checkedItem = alignment.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.IsChecked);
            var check = checkedItem.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "PART_ToggleIconPresenter");
            Require(checkedItem.Bounds.Height == 28 && Grid.GetColumn(check) == 4 && check.Width == 14 &&
                check.Content is Border { Background: var color } && ReferenceEquals(color, Ui.Accent),
                "Alignment menu must retain its radio state with a 14px primary check on the right of a 28px row.");
            alignment.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            alignment.ContextMenu.Close();
            Require(window.Layout.State.BottomAlignment == value &&
                new ProfileStore(root + "-dock-input").Data.Windows[0].Layout.BottomAlignment == value &&
                Find<Button>("BottomAlignment").ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label)).IsChecked,
                "Bottom alignment did not update the layout, checked menu state and persisted profile.");
        }
        window.Layout.Fold(window.Layout.State.Groups.Single(group => group.Region == "bottom").Id);
        var foldedAlignment = Find<Button>("BottomAlignment");
        foldedAlignment.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foldedAlignment.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Hide bottom panel"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        foldedAlignment.ContextMenu.Close();
        Require(!window.Layout.State.BottomVisible && !window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "BottomAlignment"),
            "Folded bottom alignment menu failed to hide the region and its chrome.");
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
        window.Width = 1600; window.Height = 1000;
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
        var rightGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "specs"));
        window.Layout.Select(rightGroup.Id, "files");
        projects = Find<Button>("Tab_projects");
        projectsPoint = Bounds(projects).Center;
        window.MouseDown(projectsPoint, MouseButton.Right); window.MouseUp(projectsPoint, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Require(projects.ContextMenu!.IsOpen && projects.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane"))
            .Items.OfType<MenuItem>().Any(item => Equals(item.Header, "right: Files")),
            "Move menu retained a destination's old selected-tab label.");
        projects.ContextMenu.Close();
        var insertionFrom = Bounds(Find<Button>("Tab_files")).Center;
        var insertionTo = Bounds(Find<Button>("Tab_projects")).TopLeft + new Vector(16, 16);
        window.MouseDown(insertionFrom, MouseButton.Left); window.MouseMove(insertionTo);
        var insertionHints = Find<DockSurface>("WorkspaceWorkbench").Children.OfType<Canvas>().Single().Children.OfType<Border>().ToArray();
        Require(insertionHints.Any(item => item.Width == 2 && item.BorderThickness == new Thickness(0) &&
            item.CornerRadius == new CornerRadius(0) && ReferenceEquals(item.Background, Ui.Accent)),
            "Tab insertion must show a solid square 2px primary line, not a pane drop rectangle.");
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
        Require(appendHints.Length == 1 && appendHints[0].Width == 2 && ReferenceEquals(appendHints[0].Background, Ui.Accent) &&
            !appendHints.Any(item => item.Width >= projectsHeader.Width || item.Width == 20),
            $"Empty strip space must show only the insertion line after the last tab, with no strip frame or append block: {string.Join("; ", appendHints.Select(item => $"{Canvas.GetLeft(item)},{Canvas.GetTop(item)} {item.Width}x{item.Height} bg={item.Background}"))} header={projectsHeader}.");
        window.MouseUp(appendPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Group(projectsPane.Id).Tools.Last().Id == "files", "Dropping on trailing header space did not append the tool.");
        var dragCount = 0;
        void Drag(string tab, Point to)
        {
            var source = Bounds(Find<Button>("Tab_" + tab));
            var from = new Point(source.Left + (dragCount++ % 2 == 0 ? 16 : 36), source.Center.Y);
            window.MouseDown(from, MouseButton.Left); window.MouseMove(from + new Vector(10, 10));
            Require(Find<DockSurface>("WorkspaceWorkbench").IsDragging, "Pointer input did not start a drag draft.");
            window.MouseMove(to); window.MouseUp(to, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Require(LayoutSession.IsValid(window.Layout.State), "Pointer placement broke layout invariants.");
        }
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var fallbackGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "specs"));
        var fallbackTab = Bounds(Find<Button>("Tab_specs"));
        Drag("specs", new Point(fallbackTab.Left + 16, fallbackTab.Center.Y));
        Require(window.Layout.Group(fallbackGroup.Id).Tools.Select(tab => tab.Id).SequenceEqual(new[] { "files", "specs" }) &&
            window.Layout.State.Groups.Count(group => group.Region == "right") == 2,
            "A disabled self-insertion target must fall back to the legal enclosing header append rather than creating a neighboring group.");
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
        Require(compactHighlights.Length == 1 && compactHighlights[0].Height > 32 &&
            compactHighlights[0].Width < compactHeader.Width,
            "Overlapping compact-pane targets must emphasize the nearer creation target rather than the entire header.");
        window.MouseUp(compactHeader.Center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.State.Groups.Where(group => group.Region == "right").First().Tools.Single().Id == "projects" &&
            window.Layout.State.Groups.Count(group => group.Region == "right") == 3 &&
            window.Layout.Group(compactGroup.Id).Tools.Select(tab => tab.Id).SequenceEqual(new[] { "specs", "files" }),
            "Corner-distance collision must create a compact pane's nearer neighboring group instead of always preferring its header.");
        window.Width = 1440;
        foreach (var region in new[] { "left", "bottom" })
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
        var bottomFixture = window.Layout.State.Copy();
        foreach (var region in new[] { "left", "right" })
            foreach (var before in new[] { true, false })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
                var group = window.Layout.State.Groups.First(item => item.Region == region);
                var originalCount = window.Layout.State.Groups.Count(item => item.Region == region);
                var source = window.Layout.State.Groups.First(item => item.Region != region && item.Tools.Count > 0).Tools[0].Id;
                window.Layout.Fold(group.Id);
                var header = Find<Grid>("GroupHeader_" + group.Id);
                var frame = Bounds((Grid)header.Parent!.Parent!);
                Drag(source, new Point(frame.Center.X, before ? frame.Top + 6 : frame.Bottom - 6));
                var groups = window.Layout.State.Groups.Where(item => item.Region == region).ToArray();
                Require(groups.Length == originalCount + 1 && groups[before ? 0 : 1].Tools.Any(tab => tab.Id == source) &&
                    window.Layout.Group(group.Id).Folded,
                    "Folded side creation must insert a neighbor while preserving the folded destination.");
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
                window.Layout.Fold(group.Id);
                var header = Find<Grid>("GroupHeader_" + group.Id);
                var frame = Bounds((Grid)header.Parent!.Parent!);
                Drag(source, limited ? new Point(frame.Center.X, frame.Top + 6) : new Point(frame.Left + 2, frame.Center.Y));
                Require(window.Layout.State.Groups.Count(item => item.Region == region) == originalCount &&
                    window.Layout.Group(group.Id).Tools.Any(tab => tab.Id == source),
                    "Folded side margins and group limits must preserve joining without creating a neighbor.");
            }
        window.Layout.ApplyPreset(bottomFixture);
        var folded = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "review"));
        var fold = Find<Button>("FoldRestore_" + folded.Id);
        var center = Bounds(fold).Center;
        window.MouseDown(center, MouseButton.Left); window.MouseUp(center, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        var rail = Find<Button>("FoldRestore_" + folded.Id);
        Require(window.Layout.Group(folded.Id).Folded && rail.IsFocused && Bounds(rail).Height == 27,
            "Folded bottom rail did not preserve its compact axis and restore focus.");
        var foldedLabel = (TextBlock)rail.Content!;
        var labelCenter = foldedLabel.TranslatePoint(new Point(foldedLabel.Bounds.Width / 2, foldedLabel.Bounds.Height / 2), window)!.Value;
        Require(Math.Abs(labelCenter.Y - Bounds(Find<Border>("FoldedDockGroup_" + folded.Id)).Center.Y) < 1 &&
            rail.BorderThickness == new Thickness(0) &&
            rail.GetLogicalAncestors().OfType<LayoutTransformControl>().First().LayoutTransform is Avalonia.Media.RotateTransform { Angle: 90 },
            $"Folded labels must read downward, be centered in the rail, and omit standalone button borders: label={labelCenter.Y}, rail={Bounds(Find<Border>("FoldedDockGroup_" + folded.Id)).Center.Y}, border={rail.BorderThickness}.");
        var changesSource = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "changes"));
        window.Layout.Move("changes", changesSource.Id, folded.Id, window.Layout.Tabs(folded.Id).Count);
        window.Layout.Select(folded.Id, "review");
        window.Layout.Fold(folded.Id);
        rail = Find<Button>("FoldRestore_" + folded.Id);
        window.Layout.Select(folded.Id, "changes");
        Require(((TextBlock)rail.Content!).Text == "Changes" &&
            ReferenceEquals(rail, Find<Button>("FoldRestore_" + folded.Id)),
            "Folded bottom selection did not update its existing restore label.");
        window.Layout.Select(folded.Id, "review");
        var target = Bounds(Find<Border>("FoldedDockGroup_" + folded.Id));
        Drag("files", new Point(target.Right - 6, target.Bottom - 10));
        Require(window.Layout.State.Groups.Count(group => group.Region == "bottom") == 3 &&
            window.Layout.State.Groups.Last(group => group.Region == "bottom").Tools.Any(tab => tab.Id == "files"),
            "The inner right half of a folded bottom pane must create a trailing group.");
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        var foldedBottom = window.Layout.State.Groups.Single(group => group.Region == "bottom");
        window.Layout.Fold(foldedBottom.Id);
        var foldedFrame = Bounds(Find<Border>("FoldedDockGroup_" + foldedBottom.Id));
        var foldedPoint = new Point(foldedFrame.Left + 2, foldedFrame.Top + 16);
        var filesPoint = Bounds(Find<Button>("Tab_files")).Center;
        window.MouseDown(filesPoint, MouseButton.Left); window.MouseMove(foldedPoint);
        var foldedHint = Find<DockSurface>("WorkspaceWorkbench").Children.OfType<Canvas>().Single().Children.OfType<Border>()
            .Single(item => item.Width == foldedFrame.Width && item.Height == foldedFrame.Height);
        Require(foldedHint.BorderThickness == new Thickness(0) && foldedHint.CornerRadius == new CornerRadius(0) &&
            foldedHint.Background is Avalonia.Media.SolidColorBrush { Color.A: 51 },
            "The whole folded bottom frame, including its alignment button, must show an active background-only hint.");
        window.MouseUp(foldedPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Group(foldedBottom.Id).Tools.Any(tab => tab.Id == "files") && !window.Layout.Group(foldedBottom.Id).Folded,
            "Dropping over the folded alignment button must join and unfold that pane.");
        foreach (var limited in new[] { false, true })
        {
            window.Layout.ApplyPreset(DockState.Preset("balanced"));
            if (limited) window.Layout.Geometry(state => state.BottomLimit = 1);
            var group = window.Layout.State.Groups.Single(item => item.Region == "bottom");
            window.Layout.Fold(group.Id);
            var frame = Bounds(Find<Border>("FoldedDockGroup_" + group.Id));
            Drag("files", new Point(frame.Left + 6, frame.Center.Y));
            var bottomGroups = window.Layout.State.Groups.Where(item => item.Region == "bottom").ToArray();
            Require(bottomGroups.Length == (limited ? 1 : 2) && bottomGroups[0].Tools.Any(tab => tab.Id == "files"),
                "Folded left-half drops must create before the pane, or join it when the group limit prevents creation.");
        }
        window.Layout.ApplyPreset(DockState.Preset("balanced"));
        window.Layout.Geometry(state => state.SideLimit = 1);
        var left = window.Layout.State.Groups.Single(group => group.Region == "left");
        var bounds = Bounds(Find<Border>("DockBody_" + left.Id));
        var epoch = window.Layout.Epoch;
        Drag("review", new Point(bounds.Center.X, bounds.Top + 20));
        Require(window.Layout.Epoch == epoch && window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "review")).Region == "right",
            "Side limit rejected target still committed a pointer drop.");
        Require(AutomationProperties.GetName(Find<Button>("FoldRestore_" + window.Layout.State.Groups.First(group => group.Region == "right").Id)) == "Fold pane",
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
        var preview = surface.Children.OfType<Canvas>().Single().Children.OfType<Panel>().SingleOrDefault(item => item.Name == "DragPreview");
        Require(preview is not null && Canvas.GetLeft(preview) > from.X && Canvas.GetTop(preview) > from.Y &&
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
        foreach (var alignment in new[] { "center-left", "center-right", "full" })
        {
            window.Layout.ApplyPreset(DockState.Preset("balanced"));
            window.Layout.Geometry(state => state.BottomAlignment = alignment);
            window.Layout.Visible("bottom", false);
            var region = alignment == "center-right" ? "right" : "left";
            var source = region == "left" ? "files" : "projects";
            var targetGroup = window.Layout.State.Groups.Last(group => group.Region == region);
            var body = Bounds(Find<Border>("DockBody_" + targetGroup.Id));
            var destination = new Point(body.Center.X, body.Bottom - 8);
            var sourceBounds = Bounds(Find<Button>("Tab_" + source));
            var origin = new Point(sourceBounds.Left + 16, sourceBounds.Center.Y);
            window.MouseDown(origin, MouseButton.Left); window.MouseMove(origin + new Vector(10, 10));
            window.MouseMove(destination);
            var highlights = surface.Children.OfType<Canvas>().Single().Children.OfType<Border>()
                .Where(item => item.Background is Avalonia.Media.SolidColorBrush { Color.A: 51 }).ToArray();
            Require(highlights.Length == 1 && highlights[0].Height == 24 && highlights[0].BorderThickness == new Thickness(0, 2, 0, 0),
                "Hidden bottom collision must emphasize exactly one destination over an overlapping side creation target.");
            window.MouseUp(destination, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Require(window.Layout.State.BottomVisible &&
                window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == source)).Region == "bottom",
                "The hidden-bottom band did not win its overlapping side creation collision.");
        }
        foreach (var alignment in new[] { "center", "center-left", "center-right", "full" })
            foreach (var region in new[] { "left", "right" })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
                window.Layout.Geometry(state => state.BottomAlignment = alignment);
                window.Layout.Visible("bottom", false);
                var source = region == "left" ? "files" : "projects";
                window.Layout.Visible(region, false);
                var hiddenRailBounds = Bounds(Find<Border>(region + "HiddenSideRail"));
                Drag(source, new Point(hiddenRailBounds.Center.X, hiddenRailBounds.Bottom - 8));
                Require(window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == source)).Region == region &&
                    !window.Layout.State.BottomVisible,
                    $"Hidden bottom drop zone stole the {region} restore rail's lower corner for {alignment} alignment.");
            }
        foreach (var region in new[] { "left", "right" })
        {
            window.Layout.ApplyPreset(DockState.Preset("balanced"));
            var tab = window.Layout.State.Groups.First(group => group.Region != region && group.Tools.Count > 0).Tools[0].Id;
            window.Layout.Visible(region, false);
            var hiddenRail = Find<Border>(region + "HiddenSideRail");
            var restoreButton = Find<Button>(region + "RestoreRail");
            Require(Bounds(hiddenRail).Width == 28 && Bounds(hiddenRail).Height == Bounds(surface).Height &&
                restoreButton.Bounds.Size == new Size(24, 24) && ((Border)restoreButton.Content!).Bounds.Size == new Size(14, 14),
                "Hidden side rail must expose the reference's full-height 28px strip and 24px restore button with a 14px icon.");
            var destination = Bounds(Find<Button>(region + "RestoreRail")).Center;
            Drag(tab, destination);
            Require((region == "left" ? window.Layout.State.LeftVisible : window.Layout.State.RightVisible) &&
                window.Layout.State.Groups.Single(group => group.Tools.Any(item => item.Id == tab)).Region == region,
                "Hidden-side drop did not restore its destination region.");
            window.Layout.Visible(region, false);
            destination = Bounds(Find<Button>(region + "RestoreRail")).Center;
            window.MouseDown(destination, MouseButton.Left); window.MouseUp(destination, MouseButton.Left);
            Require(region == "left" ? window.Layout.State.LeftVisible : window.Layout.State.RightVisible,
                "Hidden side restore button did not reveal its region.");
        }
        window.Width = 1600; window.Height = 1000;
        foreach (var alignment in new[] { "center", "full", "center-left", "center-right" })
            foreach (var region in new[] { "left", "right" })
            {
                window.Layout.ApplyPreset(DockState.Preset("balanced"));
                window.Layout.Geometry(state => state.BottomAlignment = alignment);
                var handle = Find<ResizeHandle>(region + "Separator");
                var frame = (Grid)handle.Parent!;
                var start = Bounds(handle).Center;
                var workbench = Find<DockSurface>("WorkspaceWorkbench");
                var initial = region == "left" ? window.Layout.State.LeftWidth : window.Layout.State.RightWidth;
                var untouched = region == "left" ? window.Layout.State.RightWidth : window.Layout.State.LeftWidth;
                var epochBefore = window.Layout.Epoch;
                var destination = start + new Vector((region == "left" ? 1 : -1) * (.65 - initial) * workbench.Bounds.Width, 0);
                window.MouseDown(start, MouseButton.Left); window.MouseMove(destination);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var pinned = alignment == (region == "left" ? "center-left" : "center-right");
                var expectedActive = pinned ? 1 - untouched - .2 : .65;
                var expectedNeighbor = pinned ? untouched : .15;
                var activeColumn = region == "left" ? 0 : 4;
                var neighborColumn = region == "left" ? 4 : 0;
                Require(Math.Abs(frame.ColumnDefinitions[activeColumn].Width.Value - expectedActive) < .001 &&
                    Math.Abs(frame.ColumnDefinitions[neighborColumn].Width.Value - expectedNeighbor) < .001 &&
                    Math.Abs(frame.ColumnDefinitions[2].Width.Value - .2) < .001,
                    $"Side resize did not project the reference's neighboring panes and minimum center for {alignment}/{region}.");
                Require(window.Layout.Epoch == epochBefore && window.Layout.State.LeftWidth == .18 && window.Layout.State.RightWidth == .28,
                    "Live neighbor compression persisted uncommitted widths.");
                var bottom = Bounds(Find<Button>("BottomAlignment").GetLogicalAncestors().OfType<Grid>().First(grid => grid.Name?.StartsWith("GroupHeader_", StringComparison.Ordinal) == true)).Right;
                var expectedRight = alignment is "full" or "center-right" ? Bounds(workbench).Right :
                    Bounds(workbench).Right - frame.ColumnDefinitions[4].ActualWidth - 1;
                Require(Math.Abs(bottom - expectedRight) <= 1,
                    "Bottom alignment did not follow the compressed side's live span.");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs();
                handle = Find<ResizeHandle>(region + "Separator");
                Require(window.Layout.Epoch == epochBefore && Math.Abs(((Grid)handle.Parent!).ColumnDefinitions[activeColumn].Width.Value - initial) < .001,
                    "Canceling side resize did not restore the original live geometry.");
                start = Bounds(handle).Center;
                destination = start + new Vector((region == "left" ? 1 : -1) * (.65 - initial) * workbench.Bounds.Width, 0);
                window.MouseDown(start, MouseButton.Left); window.MouseMove(destination); window.MouseUp(destination, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Require(Math.Abs((region == "left" ? window.Layout.State.LeftWidth : window.Layout.State.RightWidth) - expectedActive) < .001 &&
                    (region == "left" ? window.Layout.State.RightWidth : window.Layout.State.LeftWidth) == untouched,
                    "A side separator committed the untouched neighbor's compressed width.");
                var savedLeft = window.Layout.State.LeftWidth;
                var savedRight = window.Layout.State.RightWidth;
                var savedEpoch = window.Layout.Epoch;
                window.Width = 800;
                handle = Find<ResizeHandle>(region + "Separator");
                var narrow = (Grid)handle.Parent!;
                Require(Math.Abs(narrow.ColumnDefinitions[2].Width.Value - .4) < .001 &&
                    window.Layout.State.LeftWidth == savedLeft && window.Layout.State.RightWidth == savedRight && window.Layout.Epoch == savedEpoch,
                    "Narrow viewport compression rewrote the frame or lost the center minimum.");
                window.Width = 1600;
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
                var workbenchWidth = Bounds(Find<DockSurface>("WorkspaceWorkbench")).Width;
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
        foreach (var fraction in new[] { .75, .5, .25, 5d })
        {
            window.Layout.ApplyPreset(DockState.Preset("balanced"));
            var bottomHandle = Find<ResizeHandle>("bottomSeparator");
            var start = Bounds(bottomHandle).Center;
            var height = Bounds(Find<DockSurface>("WorkspaceWorkbench")).Height;
            var minimum = Math.Min(.7, 147 / height);
            var initial = window.Layout.State.BottomHeight;
            var requested = minimum * fraction;
            var destination = start + new Vector(0, (initial - requested) * height);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(destination);
            Require(window.Layout.State.BottomHeight == initial, "Bottom preview must not persist a draft height.");
            window.MouseUp(destination, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            var visible = window.Layout.State.BottomVisible;
            Require(visible == (fraction >= .5) && Math.Abs(window.Layout.State.BottomHeight -
                    (visible ? Math.Clamp(requested, minimum, .7) : initial)) < .001,
                "Bottom resize must snap to its 147px minimum, cap at 70%, or collapse while retaining expanded height.");
            if (!visible)
            {
                window.KeyPress(Key.J, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.J, null);
                Require(window.Layout.State.BottomVisible && window.Layout.State.BottomHeight == initial,
                    "Restoring a collapsed bottom region must recover its expanded height.");
            }
        }
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
            Require(Math.Abs(outerRange.Maximum - Math.Min(.7, Math.Min(1 - window.Layout.State.RightWidth - 1e-6, 1 - 320 / workbench.Bounds.Width)) * 100) < .001,
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
        window.Layout.Open(new DockTab("probe", "probe.txt", "source", "hello.txt"), true, centerGroup);
        var probe = Bounds(Find<Button>("Tab_probe"));
        epoch = window.Layout.Epoch;
        Drag("probe", probe.Center);
        Require(window.Layout.Epoch == epoch, "No-op tab drop committed a transition.");
        window.Width = 1600; window.Height = 1000;
        for (var index = 0; index < 8; index++)
        {
            var path = $"clipped-document-{index}.txt";
            File.WriteAllText(Path.Combine(root, path), "Clipping fixture");
            window.Layout.Open(new DockTab("clip" + index, path, "source", path), true, centerGroup);
        }
        var stripHeader = Find<Grid>("GroupHeader_" + centerGroup);
        var stripScroll = stripHeader.GetLogicalDescendants().OfType<ScrollViewer>().Single();
        stripScroll.Offset = new Vector(20, 0);
        var clippedFrom = Bounds(Find<Button>("Tab_clip2")).Center;
        var baseExtent = stripScroll.Extent.Width;
        var clippedTo = Bounds(stripHeader).TopLeft + new Vector(2, 16);
        window.MouseDown(clippedFrom, MouseButton.Left); window.MouseMove(clippedTo);
        Require(Math.Abs(stripScroll.Extent.Width - baseExtent) < .01,
            "Dragging must not add an append block to the strip's scrollable extent.");
        Require(!surface.Children.OfType<Canvas>().Single().Children.OfType<Border>().Any(item =>
            ReferenceEquals(item.Background, Ui.Accent)),
            "An insertion boundary outside the scrolled viewport must be clipped, not moved to its edge.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(clippedTo, MouseButton.Left);
        Bounds(stripHeader);
        Require(Math.Abs(stripScroll.Extent.Width - baseExtent) < .01,
            "Cancelled dragging must restore the original tab-strip extent.");
        for (var index = 0; index < 8; index++) window.Layout.Close(centerGroup, "clip" + index);
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
            var menuPoint = new Point(Bounds(boundaryHeader).Right - 100, Bounds(boundaryHeader).Center.Y);
            window.MouseDown(menuPoint, MouseButton.Right); window.MouseUp(menuPoint, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var splitItems = boundaryHeader.ContextMenu!.Items.OfType<MenuItem>()
                .Where(item => Equals(item.Header, "New split right") || Equals(item.Header, "New split below")).ToArray();
            Require(boundaryHeader.ContextMenu.IsOpen && splitItems.Length == 2 && splitItems.All(item => item.IsEnabled == allowed),
                "Split menus must use the reference's inclusive 640 by 360 whole-pane thresholds.");
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
        window.Width = 900; window.Height = 440;
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
        Require(window.Layout.View.FocusedGroup == foldedGroup.Id && Find<Button>("FoldRestore_" + foldedGroup.Id).IsFocused,
            "Group traversal did not focus the folded pane's restore control.");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Dispatcher.UIThread.RunJobs();
        Require(!window.Layout.Group(foldedGroup.Id).Folded, "Keyboard restore did not expand the folded pane.");
        window.Close();
        Console.WriteLine("PASS pointer side/bottom insertion, folded-bottom target, group limits, capture loss, outside cancellation and control names");
    }
}