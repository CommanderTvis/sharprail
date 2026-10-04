using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class PaneRetentionChecks
{
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    internal static void Run(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "retained-panes"));
        var second = project + "-other";
        IsolatedGit.Run(project, "worktree", "add", "-b", "other", second);
        Directory.CreateDirectory(Path.Combine(project, "changes"));
        for (var index = 0; index < 40; index++) File.WriteAllText(Path.Combine(project, "changes", $"file-{index:00}.txt"), "change\n");
        using var app = new E2eWorkspace(project);
        var window = app.Window;
        WorkspaceChoices(app);
        var registry = app.Workbench.PluginRegistry;
        Until(() => window.WorkspaceMounted);
        var firstWorkspace = window.WorkspaceRoot;
        var builds = new Dictionary<string, int>();
        var actionBuilds = 0;
        void Register()
        {
            registry.RegisterManifest(new("retained", "Retained", "puzzle", "1", PluginApi.Generation, 1)
            { Contributes = new() { SideTools = [new("panel", "Panel", "puzzle", PluginToolSide.Right)] } });
            registry.AddSideTool("retained", new("panel", workspace =>
            {
                builds[workspace] = builds.GetValueOrDefault(workspace) + 1;
                return new Border { Name = "RetainedPane", Tag = workspace, Child = new TextBox { Name = "RetainedInput", Text = workspace } };
            }));
            registry.AddWorkspaceAction("retained", new("retained-action", (workspace, _) =>
            {
                actionBuilds++;
                return new Button { Name = "RetainedAction", Tag = workspace, Content = "Action" };
            }));
            registry.SetActive("retained", true);
        }
        Register();
        window.Layout.RestoreTool("plugin:retained:panel");
        var pane = app.Find<Border>("RetainedPane");
        var input = app.Find<TextBox>("RetainedInput");
        input.Text = "unsaved pane state";
        var action = app.Find<Button>("RetainedAction");
        var routing = app.Host.HoldOpen();
        var switching = window.OpenProjectAsync(second);
        Settle(100);
        Require(!switching.IsCompleted && ReferenceEquals(pane, app.Find<Border>("RetainedPane")),
            "Host routing must keep the prior plugin pane visible.");
        Require(ReferenceEquals(action, app.Find<Button>("RetainedAction")), "Host routing must retain the current launcher action.");
        routing.SetResult();
        Await(switching);
        Until(() => app.Find<Border>("RetainedPane").Tag is string workspace && workspace == window.WorkspaceRoot);
        Require(!ReferenceEquals(pane, app.Find<Border>("RetainedPane")) && builds[window.WorkspaceRoot] == 1,
            "A workspace-specific pane must use its own workspace and be built once.");
        Await(window.OpenProjectAsync(firstWorkspace));
        Require(ReferenceEquals(pane, app.Find<Border>("RetainedPane")) && input.Text == "unsaved pane state" && builds[firstWorkspace] == 1,
            "Returning to a workspace must retain its pane and local input state.");
        Require(ReferenceEquals(action, app.Find<Button>("RetainedAction")) && actionBuilds == 2,
            "Returning to a workspace must reuse its center actions.");
        var actionDetaches = 0;
        action.DetachedFromVisualTree += (_, _) => actionDetaches++;

        window.Layout.RestoreTool("changes");
        Await(window.RefreshAsync());
        Until(() => window.GetLogicalDescendants().OfType<Border>().Any(frame => frame.Classes.Contains("change-row")));
        var changesPanel = app.Find<Grid>("ChangesPanel");
        var scope = app.Find<Button>("ChangesScope");
        var scroll = changesPanel.GetLogicalDescendants().OfType<ScrollViewer>().Single();
        window.UpdateLayout();
        scroll.Offset = new Vector(0, 100);
        var offset = scroll.Offset;
        Require(offset.Y > 0, "The Changes fixture must have a nonzero scroll offset.");
        var row = changesPanel.GetLogicalDescendants().OfType<Border>().First(frame => frame.Classes.Contains("change-row"));
        var rowDetaches = 0;
        row.DetachedFromVisualTree += (_, _) => rowDetaches++;
        Await(window.RefreshAsync());
        Require(ReferenceEquals(changesPanel, app.Find<Grid>("ChangesPanel")), "An identical Git refresh must preserve the pane.");
        Require(ReferenceEquals(scope, app.Find<Button>("ChangesScope")), "An identical Git refresh must preserve the toolbar.");
        Require(rowDetaches == 0, "An identical Git refresh must preserve unchanged rows.");
        Require(scroll.Offset == offset, $"An identical Git refresh must preserve scroll: {offset} became {scroll.Offset}.");
        Require(actionDetaches == 0, "An identical Git refresh must preserve launchers.");
        File.WriteAllText(Path.Combine(project, "new-change.txt"), "new\n");
        Await(window.RefreshAsync());
        Require(ReferenceEquals(changesPanel, app.Find<Grid>("ChangesPanel")) && rowDetaches == 0 && scroll.Offset == offset,
            "A changed Git listing must preserve the pane and unchanged rows.");
        app.Click(app.Find<Button>("ChangesTree"));
        var tree = app.Find<TreeView>("ChangesTree");
        var folder = tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(node => Equals(node.Tag, "changes"));
        folder.IsExpanded = false;
        File.WriteAllText(Path.Combine(project, "another-change.txt"), "another\n");
        Await(window.RefreshAsync());
        Require(ReferenceEquals(tree, app.Find<TreeView>("ChangesTree")) && !folder.IsExpanded && folder.GetLogicalAncestors().Contains(tree),
            "A tree refresh must preserve the tree, unchanged folder controls and collapse state.");

        window.Layout.Select(window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "review")).Id, "review");
        var review = app.Find<StackPanel>("ReviewPanel");
        var summary = app.Find<TextBlock>("ReviewSummary");
        var previousSummary = summary.Text;
        File.WriteAllText(Path.Combine(project, "review-change.txt"), "review\n");
        Await(window.RefreshAsync());
        Until(() => summary.Text != previousSummary);
        Require(ReferenceEquals(review, app.Find<StackPanel>("ReviewPanel")) && ReferenceEquals(summary, app.Find<TextBlock>("ReviewSummary")) && summary.Text != previousSummary,
            "Review must update its summary while preserving the pane and controls.");

        registry.RemovePlugin("retained");
        Register();
        window.Layout.RestoreTool("plugin:retained:panel");
        Require(!ReferenceEquals(pane, app.Find<Border>("RetainedPane")) && builds[firstWorkspace] == 2,
            "Removing a plugin must evict its cached panes.");
        Await(window.OpenProjectAsync(second));
        Require(builds[window.WorkspaceRoot] == 2, "Removing a plugin must also evict its inactive workspace's pane.");
        Console.WriteLine("PASS retained panes: held routing, workspace state, launcher identity, Git rows/scroll/tree and plugin eviction");
    }

    private static void WorkspaceChoices(E2eWorkspace app)
    {
        var launcher = new AgentLauncher("icon-check", "Agent", "asset:agent.svg", _ => "agent", () => new(true))
        { CreateIcon = (_, _) => new Border { Name = "LauncherAsset", Width = 16, Height = 16 } };
        var dialog = new NewWorkspaceDialog(["Project"], "Project", new BranchCatalog(["main"], [], "main"), [], () => { }, [launcher]);
        _ = dialog.ShowAsync(app.Window);
        Settle();
        var selected = dialog.Window.GetLogicalDescendants().OfType<ToggleButton>().Single(button => button.Name == "WsTargetWorktree");
        var other = dialog.Window.GetLogicalDescendants().OfType<ToggleButton>().Single(button => button.Name == "WsTargetDefault");
        dialog.Window.MouseMove(other.TranslatePoint(new Point(other.Bounds.Width / 2, other.Bounds.Height / 2), dialog.Window)!.Value);
        Settle();
        var selectedFrame = selected.GetVisualDescendants().OfType<ContentPresenter>().Single(frame => frame.Name == "PART_ContentPresenter");
        var hoveredFrame = other.GetVisualDescendants().OfType<ContentPresenter>().Single(frame => frame.Name == "PART_ContentPresenter");
        Require(selected.IsChecked == true && other.IsChecked == false && !Equals(selectedFrame.BorderBrush, hoveredFrame.BorderBrush),
            "The selected workspace target must remain distinct from the hovered target.");
        Require(dialog.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "LauncherAsset"), "The launcher must render its supplied asset icon.");
        dialog.Window.Close();
        Console.WriteLine("PASS workspace choices: launcher asset and selected versus hovered styling");
    }
}