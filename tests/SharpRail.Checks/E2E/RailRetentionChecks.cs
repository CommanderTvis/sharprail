using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>Switching between one project's workspaces restyles the rail in place instead of rebuilding its rows.</summary>
internal static class RailRetentionChecks
{
    internal static void Run(string root)
    {
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
        Require(back.Count == settled.Count && back.Zip(settled).All(pair => ReferenceEquals(pair.First, pair.Second)) && reattached == 0,
            "Switching to another project's workspace keeps the rail and its rows instead of rebuilding them.");
        Require(Controls(app).OfType<Button>().Single(button => button.Name == "AddWorkspace" && button.IsEffectivelyVisible).GetLogicalAncestors()
            .OfType<Border>().Any(border => Equals(border.Tag, app.Root)), "The open project's trailing controls follow the switch.");
        Console.WriteLine("PASS switching workspaces keeps the rail's rows");
        app.Window.ShowSettings();
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Layout"));
        settings.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "VerticalCenterTabs").IsChecked = true;
        settings.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "VerticalTabsInProjects").IsChecked = true;
        settings.Close();
        app.Click(app.Find<Button>("Tab_projects"));
        Controls(app).OfType<ScrollViewer>().Single(item => item.Content is StackPanel && item.Parent is Grid { Name: "ProjectsPanel" }).MaxHeight = 110;
        Settle(300);
        var retained = RailControls(app);
        var label = app.Workbench.State.ChangeAsync(HostStateChange.Label(first, "Retained workspace"));
        Until(() => label.IsCompleted && ItemText(app, first, "WorkspaceName") == "Retained workspace");
        Require(retained.All(control => Controls(app).Contains(control)), "Renaming a workspace must retain its rail controls.");
        VerifyRemoval(app, second, first);
        VerifyRemoval(app, first, app.Root);
        retained = RailControls(app);
        _ = CreateWorkspaceViaDialog(app);
        Require(retained.All(control => Controls(app).Contains(control)), "Creating a workspace must insert rows without replacing survivors.");
        Console.WriteLine("PASS rail creation, rename, inactive/active removal retain surviving controls, focus, menus and scroll position");
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
            Require(offset.Y > 0, "The deletion regression must exercise an actually scrolled rail.");
            held.SetResult();
            Until(() => !WorktreePaths(app).Contains(removed));
            Settle(300);
            Require(retained.All(control => Controls(app).Contains(control)) && detached == 0,
                "Removing a workspace must detach only that workspace's row and tab host.");
            Require(menu.IsOpen, "Removing another workspace must preserve the surviving menu.");
            Require(ReferenceEquals(app.Window.FocusManager?.GetFocusedElement(), focused),
                "Removing another workspace must preserve the surviving keyboard focus target.");
            var expected = Math.Min(offset.Y, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
            Require(Math.Abs(scroller.Offset.Y - expected) < .01, "Removing a workspace must preserve scroll position except for viewport clamping.");
            CloseMenu(menu);
        }
        finally { held.TrySetResult(); }
    }

    private static List<Grid> Rows(E2eWorkspace app) => Controls(app).OfType<Grid>().Where(item => item.Name == "WorkspaceItem").ToList();
}