using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

// Projects and workspaces as plugins read them (H12), and the lifecycle events derived from shared state.
public sealed partial class PluginRuntime
{
    internal IReadOnlyList<HostProject> Projects() =>
        Seams.State.Current.Projects.Select(path => new HostProject(path, DirectoryName(path), path)
        { WorktreesDirectory = WorktreePaths.ProjectDirectory(path, (Seams.State as HostStateStore)?.DirectoryPath ?? Seams.StateDirectory) }).ToArray();

    internal async ValueTask<IReadOnlyList<HostWorkspace>> WorkspacesAsync(string? projectId, CancellationToken cancellationToken)
    {
        var workspaces = new List<HostWorkspace>();
        foreach (var project in Projects().Where(project => projectId is null || project.Id == projectId))
        {
            var trees = await Seams.Worktrees(project.Path, cancellationToken);
            if (trees.Count == 0) workspaces.Add(Workspace(project.Id, project.Path, "", isDefault: true));
            else workspaces.AddRange(trees.Select(tree => Workspace(project.Id, tree.Path, tree.Branch, tree.IsMain)));
        }
        return workspaces;
    }

    internal async ValueTask<HostWorkspace?> WorkspaceAsync(string id, CancellationToken cancellationToken) =>
        (await WorkspacesAsync(null, cancellationToken)).FirstOrDefault(workspace => workspace.Id == id);

    private HostWorkspace Workspace(string projectId, string path, string branch, bool isDefault) =>
        new(path, projectId, Seams.State.Current.WorkspaceLabels.GetValueOrDefault(path) ?? DirectoryName(path),
            branch == "detached HEAD" ? "" : branch, path, isDefault);

    private static string DirectoryName(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path;

    // The host publishes a project's workspace list after creating or removing one, and labels on rename.
    private async Task WorkspaceEventsAsync(HostState previous, HostState state)
    {
        var events = new List<WorkspaceEvent>();
        foreach (var project in state.Workspaces.Select(workspace => workspace.ProjectRoot).Union(previous.Workspaces.Select(workspace => workspace.ProjectRoot)))
        {
            var paths = state.WorkspacesOf(project).Select(workspace => workspace.Path).ToArray();
            var before = previous.WorkspacesOf(project).Select(workspace => workspace.Path).ToArray();
            foreach (var removed in before.Except(paths)) events.Add(new WorkspaceRemoved(project, removed));
            foreach (var created in paths.Except(before))
                if (await WorkspaceAsync(created, lifetime.Token) is { } workspace) events.Add(new WorkspaceCreated(workspace));
        }
        foreach (var path in state.WorkspaceLabels.Keys.Union(previous.WorkspaceLabels.Keys))
            if (state.WorkspaceLabels.GetValueOrDefault(path) != previous.WorkspaceLabels.GetValueOrDefault(path) &&
                !events.Any(change => change is WorkspaceRemoved { Id: var id } && id == path) &&
                await WorkspaceAsync(path, lifetime.Token) is { } workspace)
                events.Add(new WorkspaceUpdated(workspace));
        foreach (var change in events)
            foreach (var (entry, tables) in Active())
                foreach (var observer in tables.WorkspaceObservers) Guard(entry.Id, () => observer(change));
    }
}