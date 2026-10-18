using System.Text.Json;

using SharpRail.Checks.E2E;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class ProjectPaneChecks
{
    internal static void Model()
    {
        var layout = new LayoutSession();
        var top = layout.State.Groups.Single(group => group.Tools.Any(tool => tool.Id == "files")).Id;
        var lower = layout.State.Groups.Single(group => group.Tools.Any(tool => tool.Id == "review")).Id;
        var initialOrder = layout.Group(top).Tools.Select(tool => tool.Id).ToArray();
        layout.Geometry(state => { state.LeftWidth = .21; state.RightWidth = .31; });
        layout.SwitchWorkspace("project-a/default", "project-a");
        layout.Close(top, "files");
        layout.HidePane(lower);
        layout.Visible("left", false);
        layout.HomeProjects(false);
        layout.SwitchWorkspace("project-b/default", "project-b");
        Require(Open("files") && !layout.Group(lower).Folded && layout.State.LeftVisible && layout.State.HomeProjectsVisible,
            "Closing or hiding A's panes must not change a new project's defaults.");
        Require(layout.State.LeftWidth == .21 && layout.State.RightWidth == .31,
            "Project visibility changes must retain the window's shared side sizes.");
        foreach (var id in initialOrder) layout.Close(top, id);
        layout.SwitchWorkspace("project-a/worktree", "project-a");
        Require(!Open("files") && Open(DockState.SpecsTool) && layout.Group(lower).Folded && !layout.State.LeftVisible,
            "Workspaces of A must share A's closed tools and hidden panes, independently of B.");
        layout.RestoreTool("files");
        Require(layout.Group(top).Tools.Select(tool => tool.Id).SequenceEqual(initialOrder),
            "Reopening a project tool must restore its shared order after several sibling tools were closed.");
        var restored = JsonSerializer.Deserialize<DockState>(JsonSerializer.Serialize(layout.State))!;
        layout = new LayoutSession(restored);
        layout.SwitchWorkspace("project-b/default", "project-b");
        Require(initialOrder.All(id => !Open(id)), "A's reopen must not reopen B's closed tools after restart.");
        layout.SwitchWorkspace("project-a/default", "project-a");
        Require(initialOrder.All(Open) && layout.Group(lower).Folded, "Restart must preserve A's distinct pane state.");
        layout.ApplyPreset(DockState.Preset("review"));
        layout.SwitchWorkspace("project-b/default", "project-b");
        Require(initialOrder.All(id => !Open(id)), "Applying a layout in A must preserve B's closed tools.");
        layout.SwitchWorkspace("project-c/default", "project-c");
        Require(initialOrder.All(Open), "New projects must use the preset defaults, not the last project's closures.");
        var frame = layout.State.Copy(); frame.Workspaces.Clear(); frame.ActiveWorkspace = "";
        var preset = JsonSerializer.Deserialize<DockState>(SharedState.Serialize(frame))!;
        Require(preset.ActiveProject.Length == 0 && preset.ProjectPanes.Count == 0 && preset.DefaultProjectPanes is null,
            "Shared custom presets must not publish client-local project pane state.");
        var legacy = new LayoutSession();
        var review = legacy.State.Groups.Single(group => group.Tools.Any(tool => tool.Id == "review")).Id;
        legacy.Close(review, "review");
        legacy.SwitchWorkspace("legacy-a", "legacy-a");
        legacy.RestoreTool("review");
        legacy.SwitchWorkspace("legacy-b", "legacy-b");
        Require(!legacy.State.Groups.Any(group => group.Tools.Any(tool => tool.Id == "review")),
            "Legacy global visibility must seed the defaults once, without inheriting a later project's reopen.");
        Console.WriteLine("PASS project pane state: close/hide/reopen, shared workspaces/geometry, restart, preset isolation and legacy defaults");

        bool Open(string id) => layout.State.Groups.Any(group => group.Tools.Any(tool => tool.Id == id));
    }

    internal static void RunUi(string root)
    {
        var first = Path.Combine(root, "project-panes-a");
        var second = Path.Combine(root, "project-panes-b");
        var profilePath = Path.Combine(root, "project-panes-profile");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var profile = new ProfileStore(profilePath);
        var window = OpenWindow(first, profile);
        try
        {
            var group = window.Layout.State.Groups.Single(item => item.Tools.Any(tool => tool.Id == "files")).Id;
            window.Layout.Close(group, "files");
            Switch(second);
            Require(HasFiles(), "Closing Files in one project must leave Files open in another project.");
            Switch(first);
            Require(!HasFiles(), "Returning to the first project must restore its closed Files pane.");
            profile.Save();
        }
        finally { window.Close(); }
        window = OpenWindow(first, new ProfileStore(profilePath));
        try
        {
            Require(!HasFiles(), "The closed pane must survive profile reload.");
            Switch(second);
            Require(HasFiles(), "Profile reload must retain the second project's open Files pane.");
            Console.WriteLine("PASS per-project panes: actual project switching and profile reload retain independent Files visibility");
        }
        finally { window.Close(); }

        bool HasFiles() => window.Layout.State.Groups.Any(group => group.Tools.Any(tool => tool.Id == "files"));
        void Switch(string path)
        {
            var task = window.OpenProjectAsync(path);
            Until(() => task.IsCompleted && window.WorkspaceMounted && window.ProjectRoot == path);
            task.GetAwaiter().GetResult();
        }
        static WorkbenchWindow OpenWindow(string path, ProfileStore profile)
        {
            var window = new WorkbenchWindow(new ProjectServices(path), path, profile, E2eTerminals.Plain);
            window.Show();
            Until(() => window.WorkspaceMounted);
            return window;
        }
    }
}
