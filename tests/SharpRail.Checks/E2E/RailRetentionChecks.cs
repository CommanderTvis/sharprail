using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>Switching between one project's workspaces restyles the rail in place instead of rebuilding its rows.</summary>
internal static class RailRetentionChecks
{
    private static void TabGeometry(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "rail-tab-geometry"));
        app.Open("README.md", true);
        app.Open("notes.txt", true);
        Directory.CreateDirectory(Path.Combine(app.Root, "folder"));
        File.WriteAllText(Path.Combine(app.Root, "folder", "notes.txt"), "duplicate-name fixture");
        app.Window.Layout.Open(new("file:folder/notes.txt", "notes.txt", "file", "folder/notes.txt"), true);
        app.ShowTool("projects");
        var original = app.Window.WorkspaceRoot;
        ContentControl Host() => app.Window.GetLogicalDescendants().OfType<ContentControl>()
            .Single(control => control.Name == "WorkspaceTabs" && Equals(control.Tag, original));
        var host = Host();
        var outgoing = host.Content;
        var rail = app.Find<Grid>("ProjectsPanel");
        var row = app.Find<Grid>("DockTab_file_notes.txt");
        Settle();
        var height = row.Bounds.Height;
        var plainHeight = app.Find<Grid>("DockTab_markdown_README.md").Bounds.Height;
        Require(height > 0, "The active tab must be laid out before comparing its preview.");
        var other = Path.Combine(app.Root, "other-project");
        Directory.CreateDirectory(other);
        var gate = app.Host.HoldOpen();
        var opening = app.Window.OpenProjectAsync(other);
        Settle();
        Require(!opening.IsCompleted && ReferenceEquals(host.Content, outgoing) && ReferenceEquals(app.Find<Grid>("ProjectsPanel"), rail),
            "Routing must leave the outgoing Projects rows mounted until the destination is resolved.");
        gate.SetResult();
        Until(() => opening.IsCompleted && app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == other);
        opening.GetAwaiter().GetResult();
        Settle();
        var preview = host.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WorkspaceTabPreview" && Equals(button.Tag, "file:notes.txt"));
        Require(Math.Abs(preview.Bounds.Height - height) < .1 && ReferenceEquals(Host(), host) && ReferenceEquals(app.Find<Grid>("ProjectsPanel"), rail),
            "An inactive tab must retain its row height and the Projects pane and workspace host must survive switching.");
        var plain = host.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WorkspaceTabPreview" && Equals(button.Tag, "markdown:README.md"));
        Require(Math.Abs(plain.Bounds.Height - plainHeight) < .1 &&
            host.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.Name == "TabSubtitle").Select(text => text.Text).ToHashSet().SetEquals(["./", "folder"]),
            "Plain rows and duplicate-name folder subtitles must match the active strip in inactive previews.");
        var retained = app.Window.GetLogicalDescendants().OfType<ContentControl>().Where(control => control.Name == "WorkspaceTabs")
            .ToDictionary(control => control, control => control.Content);
        gate = app.Host.HoldOpen();
        opening = app.Window.OpenProjectAsync(original);
        Settle();
        Require(!opening.IsCompleted && retained.All(pair => ReferenceEquals(pair.Key.Content, pair.Value)),
            "Routing to a retained workspace must not place its live strips under the workspace being left.");
        gate.SetResult();
        Until(() => opening.IsCompleted && app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == original);
        opening.GetAwaiter().GetResult();
        Settle();
        Require(Math.Abs(app.Find<Grid>("DockTab_file_notes.txt").Bounds.Height - height) < .1,
            "Returning to a workspace must preserve its tab height.");
        Console.WriteLine("PASS Projects tab geometry and retained routing views");
    }

    internal static void Run(string root)
    {
        TreeChevrons();
        TabGeometry(root);
        using var git = new IsolatedGit(Path.Combine(root, "rail-retention-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "rail-retention"));
        var first = CreateWorkspaceViaDialog(app);
        var second = CreateWorkspaceViaDialog(app);
        Settle(500);
        var rows = Rows(app);
        var scroller = Controls(app).OfType<ScrollViewer>().First(item => item.Content is StackPanel && item.Parent is Grid { Name: "ProjectsPanel" });
        var add = Controls(app).OfType<Button>().Single(button => button.Name == "AddWorkspace");
        var addRight = add.TranslatePoint(new Point(add.Bounds.Width, 0), scroller)!.Value.X;
        Require(addRight <= scroller.Bounds.Width - 12,
            $"Projects keeps a gutter for its scrollbar beside the rows' buttons ({addRight} of {scroller.Bounds.Width}).");
        var panel = Controls(app).OfType<Grid>().Single(control => control.Name == "ProjectsPanel");
        // Project, workspace and its tabs nest visibly: each level starts further right than the one above it.
        double Left(Control control) => control.TranslatePoint(new Point(0, 0), panel)!.Value.X;
        var projectRow = Controls(app).OfType<Grid>().First(control => control.Name == "ProjectRow");
        var workspaceRow = Controls(app).OfType<Grid>().First(control => control.Name == "WorkspaceItem");
        var tabsHost = Controls(app).OfType<ContentControl>().First(control => control.Name == "WorkspaceTabs" && Equals(control.Tag, workspaceRow.Tag));
        Require(Left(workspaceRow) >= Left(projectRow) + 12 && Left(tabsHost) >= Left(workspaceRow) + 12,
            $"Rail levels indent under each other (project {Left(projectRow)}, workspace {Left(workspaceRow)}, tabs {Left(tabsHost)}).");
        var detached = 0;
        panel.DetachedFromVisualTree += (_, _) => detached++;
        foreach (var target in new[] { first, app.Root, second })
        {
            app.Click(Select(app, target));
            Until(() => Active(app, target) && app.Find<TextBlock>("BranchLabel").Text is { Length: > 0 });
            Settle(500);
            var now = Rows(app);
            Require(now.Count == rows.Count && now.Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)),
                $"Switching to {Path.GetFileName(target)} keeps the workspace rows instead of rebuilding the list.");
            Require(detached == 0, $"Switching to {Path.GetFileName(target)} keeps the rail mounted instead of re-attaching it.");
        }
        // Clicking the workspace already shown is a no-op: no unmount, and no rail row or tab strip rebuilt.
        var strips = Controls(app).OfType<ContentControl>().Where(control => control.Name == "WorkspaceTabs").ToDictionary(control => control, control => control.Content);
        var held = app.Host.HoldOpen();
        app.Click(Select(app, second));
        Settle(300);
        var remounting = !app.Window.WorkspaceMounted;
        held.SetResult();
        Require(!remounting, "Clicking the current workspace must not start a remount.");
        Settle(500);
        Require(app.Window.WorkspaceMounted && Active(app, second) && detached == 0 && Rows(app).Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
            strips.All(pair => ReferenceEquals(pair.Key.Content, pair.Value)),
            "Clicking the current workspace keeps its rail rows and tab strips.");
        Console.WriteLine("PASS clicking the current workspace is a no-op");
        // Folding a project hides its rows in place: nothing in the rail is rebuilt, so nothing flickers.
        var fold = Controls(app).OfType<Button>().Single(button => button.Name == "ProjectExpand");
        app.Click(fold);
        Until(() => Rows(app).All(row => !row.IsEffectivelyVisible));
        app.Click(fold);
        Until(() => Rows(app).All(row => row.IsEffectivelyVisible));
        Require(Rows(app).Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)) && detached == 0,
            "Folding and unfolding a project keeps its workspace rows and the rail mounted.");
        // Opening another project and coming back to this one's workspace restyles the rail; nothing is rebuilt.
        var other = IsolatedGit.Repository(Path.Combine(Path.GetDirectoryName(app.Root)!, "rail-retention-other"));
        AddProject(app, "Open project", other);
        Until(() => app.Window.ProjectRoot == other && Controls(app).OfType<Grid>().Any(item => item.Name == "WorkspaceItem" && Equals(item.Tag, other)));
        Settle(500);
        var settled = Rows(app);
        var rail = Controls(app).OfType<Grid>().Single(control => control.Name == "ProjectsPanel");
        var reattached = 0;
        rail.DetachedFromVisualTree += (_, _) => reattached++;
        app.Click(Select(app, first));
        Until(() => Active(app, first) && app.Window.ProjectRoot == app.Root && app.Find<TextBlock>("BranchLabel").Text is { Length: > 0 });
        Settle(800);
        var back = Rows(app);
        // The other project opened at its Project Home, whose own Projects pane hosts the same panel; coming back
        // moves that one panel into the frame again (one reattachment) without rebuilding its rows.
        Require(back.Count == settled.Count && back.Zip(settled).All(pair => ReferenceEquals(pair.First, pair.Second)) && reattached <= 1,
            "Switching to another project's workspace keeps the rail and its rows instead of rebuilding them.");
        Require(Controls(app).OfType<Button>().Single(button => button.Name == "AddWorkspace" && button.IsEffectivelyVisible).GetLogicalAncestors()
            .OfType<Border>().Any(border => Equals(border.Tag, app.Root)), "The open project's trailing controls follow the switch.");
        Console.WriteLine("PASS switching workspaces keeps the rail's rows");
        app.ShowTool("projects");
        Controls(app).OfType<ScrollViewer>().Single(item => item.Content is StackPanel && item.Parent is Grid { Name: "ProjectsPanel" }).MaxHeight = 110;
        Settle(300);
        var retained = RailControls(app);
        var label = app.Workbench.State.ChangeAsync(HostStateChange.Label(first, "Retained workspace"));
        Until(() => label.IsCompleted && ItemText(app, first, "WorkspaceName") == "Retained workspace");
        Require(retained.All(control => Controls(app).Contains(control)), "Renaming a workspace must retain its rail controls.");
        VerifyRemoval(app, second, first);
        VerifyRemoval(app, first, app.Root);
        retained = RailControls(app);
        var created = CreateWorkspaceViaDialog(app);
        Require(retained.All(control => Controls(app).Contains(control)), "Creating a workspace must insert rows without replacing survivors.");
        Console.WriteLine("PASS rail creation, rename, inactive/active removal retain surviving controls, focus, menus and scroll position");
        VerifySwitchScroll(app, app.Root, created);
        void Publish(string[] paths) => app.State!.ChangeWorkspaces(current => current.Where(workspace => workspace.ProjectRoot != other)
            .Concat(paths.Select(path => new WorkspaceRecord(Guid.NewGuid().ToString(), other,
                path == other ? WorkspaceKinds.Default : WorkspaceKinds.External, path, path == other ? "main" : "", "main"))));
        var stale = Path.Combine(Path.GetDirectoryName(other)!, "rail-stale-workspace");
        IsolatedGit.Run(other, "worktree", "add", "--detach", stale);
        Publish([other, stale]);
        Until(() => Controls(app).Any(control => control.Name == "WorkspaceItem" && Equals(control.Tag, stale)));
        IsolatedGit.Run(other, "worktree", "remove", stale);
        using (var peer = app.NewWindow())
        {
            peer.Window.Activate();
            app.Window.Activate();
            Until(() => !Controls(app).Any(control => control.Name == "WorkspaceItem" && Equals(control.Tag, stale)));
        }
        Publish([other]);
        Until(() => app.Workbench.State.Current.WorkspacesOf(other).Count() == 1);
        IsolatedGit.Run(other, "worktree", "add", "--detach", stale);
        Publish([other, stale]);
        Until(() => Controls(app).Any(control => control.Name == "WorkspaceItem" && Equals(control.Tag, stale)));
        IsolatedGit.Run(other, "worktree", "remove", stale);
        Publish([other]);
        Until(() => !Controls(app).Any(control => control.Name == "WorkspaceItem" && Equals(control.Tag, stale)));
        Console.WriteLine("PASS deleted workspaces leave an inactive project's rail after activation and host catalog broadcasts");
    }

    private static void TreeChevrons()
    {
        var item = new TreeViewItem { Header = "Folder", ItemsSource = new[] { "Child" } };
        var window = new Window { Width = 240, Height = 160, Content = new TreeView { ItemsSource = new[] { item } } };
        window.Show();
        try
        {
            Settle();
            var toggle = item.GetVisualDescendants().OfType<ToggleButton>().Single(control => control.Name == "PART_ExpandCollapseChevron");
            var glyph = toggle.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "TreeChevron");
            Require(glyph.OpacityMask is DrawingBrush && toggle.Width == 16 && toggle.Height == 16,
                "Tree expanders must use the shared SVG vector mask with its full viewbox, not Fluent's narrow polygon.");
            var slot = item.GetVisualDescendants().OfType<Panel>().Single(control => control.Name == "PART_ExpandCollapseChevronContainer");
            Require(slot.Margin.Left + toggle.Width + slot.Margin.Right == 20, "Vector expanders must retain the compact 20 DIP tree slot.");
            toggle.IsChecked = true;
            Settle();
            Require(item.IsExpanded && glyph.RenderTransform is RotateTransform { Angle: 90 }, "Expanding a tree must rotate its shared glyph down and keep the toggle wired to the tree.");
            toggle.IsChecked = false;
            item.Classes.Add("gitignored");
            Settle();
            Require(!item.IsExpanded && glyph.RenderTransform is not RotateTransform { Angle: 90 } && ReferenceEquals(glyph.Background, SharpRail.Plugins.UI.Kit.Ui.Hint),
                "Collapsed ignored rows must restore the right-pointing vector and preserve their muted tint.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS shared vector tree chevrons, expansion, tint and compact slot");
    }

    private static Control[] RailControls(E2eWorkspace app) => Controls(app).Where(control => control.Name is
        "ProjectsPanel" or "ProjectRow" or "ProjectName" or "ProjectExpand" or "WorkspaceItem" or "WorkspaceSelect" or
        "WorkspaceTabs" or "WorkspaceTabPreview" or "AddProjectMenu" or "AddWorkspace").ToArray();

    private static void VerifyRemoval(E2eWorkspace app, string removed, string survivor)
    {
        var retained = RailControls(app).Where(control => !Equals(control.Tag, removed) &&
            !control.GetLogicalAncestors().Any(parent => parent is Control { Name: "WorkspaceItem" or "WorkspaceTabs", Tag: var path } && Equals(path, removed))).ToArray();
        var detached = 0;
        foreach (var control in retained) control.DetachedFromVisualTree += (_, _) => detached++;
        var held = app.Host.HoldGitAction();
        try
        {
            RemoveWorkspace(app, removed);
            Until(() => app.Host.GitActionPending && !app.Window.OwnedWindows.Any(window => window.IsVisible));
            Until(() => ItemText(app, removed, "WorkspaceBranch") == "Removing…");
            Require(!Select(app, removed).IsEnabled, "A workspace being removed must not open or offer its actions.");
            var button = Select(app, survivor);
            Require(button.Focus(), "A surviving workspace must accept focus while deletion is pending.");
            var menu = button.ContextMenu!;
            menu.Open(button);
            Until(() => menu.IsOpen);
            Settle(100);
            var focused = app.Window.FocusManager?.GetFocusedElement();
            Require(focused is Control, "The surviving workspace menu must have a keyboard focus target.");
            var scroller = Controls(app).OfType<ScrollViewer>().Single(item => item.Content is StackPanel && item.Parent is Grid { Name: "ProjectsPanel" });
            scroller.Offset = new Vector(0, 12);
            Settle(100);
            var offset = scroller.Offset;
            double? survivorTop = button.TranslatePoint(new Point(0, 0), scroller)?.Y;
            Require(offset.Y > 0, "The deletion regression must exercise an actually scrolled rail.");
            held.SetResult();
            Until(() => !WorktreePaths(app).Contains(removed));
            Settle(300);
            // Removing the active workspace with no earlier one lands on Project Home, whose own Projects pane takes the
            // one Projects panel: it moves there whole, so its controls detach once but none is replaced.
            Require(retained.All(control => Controls(app).Contains(control)) && (detached == 0 || app.Window.AtProjectHome),
                "Removing a workspace must detach only that workspace's row and tab host.");
            Require(menu.IsOpen, "Removing another workspace must preserve the surviving menu.");
            Require(ReferenceEquals(app.Window.FocusManager?.GetFocusedElement(), focused),
                "Removing another workspace must preserve the surviving keyboard focus target.");
            var expected = Math.Min(offset.Y, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
            // The offset is kept except for viewport clamping, or moved by exactly the removed rows' height so the visible rows stay still.
            var survivorNow = Select(app, survivor).TranslatePoint(new Point(0, 0), scroller)?.Y;
            Require(Math.Abs(scroller.Offset.Y - expected) < .01 || (survivorTop is { } before && survivorNow is { } now && Math.Abs(now - before) < .5),
                "Removing a workspace must preserve scroll position except for viewport clamping, and must not move the visible rows.");
            CloseMenu(menu);
        }
        finally { held.TrySetResult(); }
    }

    /// <summary>Switching workspaces moves a scrolled rail along one path: its offset is never written back toward where it was.</summary>
    private static void VerifySwitchScroll(E2eWorkspace app, params string[] targets)
    {
        var scroller = Controls(app).OfType<ScrollViewer>().Single(item => item.Content is StackPanel && item.Parent is Grid { Name: "ProjectsPanel" });
        foreach (var target in targets)
        {
            app.Click(Select(app, target));
            Until(() => Active(app, target) && app.Find<TextBlock>("BranchLabel").Text is { Length: > 0 });
            for (var index = 0; index < 4; index++)
            {
                File.WriteAllText(Path.Combine(app.Window.WorkspaceRoot, $"rail-tab-{index}.txt"), "tab");
                app.Window.Layout.Open(new($"file:rail-tab-{index}.txt", $"rail-tab-{index}.txt", "file", $"rail-tab-{index}.txt"), true);
            }
            Settle(500);
            var middle = Math.Max(0, (scroller.Extent.Height - scroller.Viewport.Height) / 2);
            scroller.Offset = new Vector(0, middle);
            Settle(100);
            var other = targets.First(path => path != target);
            var seen = new List<double> { scroller.Offset.Y };
            using var watch = scroller.GetObservable(ScrollViewer.OffsetProperty).Subscribe(new Recorder(offset => seen.Add(offset.Y)));
            app.Click(Select(app, other));
            Until(() => Active(app, other) && app.Find<TextBlock>("BranchLabel").Text is { Length: > 0 });
            Settle(500);
            var path = seen.Zip(seen.Skip(1), (a, b) => Math.Sign(Math.Round(b - a, 2))).Where(sign => sign != 0).ToList();
            Require(path.Distinct().Count() <= 1,
                $"Switching to {Path.GetFileName(other)} must move the rail's scroll offset one way, not back and forth ({string.Join(" > ", seen.Select(value => Math.Round(value, 1)))}).");
        }
        Console.WriteLine("PASS switching workspaces never moves the rail's scroll offset back and forth");
    }

    private sealed class Recorder(Action<Vector> on) : IObserver<Vector>
    {
        public void OnNext(Vector value) => on(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static List<Grid> Rows(E2eWorkspace app) => Controls(app).OfType<Grid>().Where(item => item.Name == "WorkspaceItem").ToList();
}