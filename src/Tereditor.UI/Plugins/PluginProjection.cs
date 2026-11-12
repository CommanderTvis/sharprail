using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Plugins;

/// <summary>Builds the read-only slice of app state a UI half sees (W3); window-scoped members follow the active window.</summary>
internal static class PluginProjection
{
    public static PluginHostProjection Build(PluginLoader loader)
    {
        var workbench = loader.Workbench;
        var state = workbench.State.Current;
        var preferences = workbench.State.Preferences;
        var window = workbench.ActiveWindow;
        var projects = state.Projects.Select(path => new HostProject(path, workbench.State.Label(path), path)).ToArray();
        var workspaces = state.Projects.ToDictionary(project => project, project =>
            (IReadOnlyList<HostWorkspace>)[.. new[] { project }.Concat(state.WorkspacesOf(project).Select(workspace => workspace.Path))
                .Concat(workbench.Windows.Where(source => source.ProjectRoot == project && source.WorkspaceRoot.Length > 0).Select(source => source.WorkspaceRoot)).Distinct()
                .Select(path => new HostWorkspace(path, project, workbench.State.Label(path), window?.WorkspaceRoot == path ? window.BranchName : "", path, path == project))]);
        var terminals = new Dictionary<string, List<TerminalTabInfo>>();
        var shown = new Dictionary<string, List<string>>();
        foreach (var source in workbench.Windows)
            foreach (var (workspace, tabs, visible) in source.TerminalTabs())
            {
                if (!terminals.TryGetValue(workspace, out var list)) terminals[workspace] = list = [];
                foreach (var tab in tabs.Where(tab => list.All(known => known.TabKey != tab.Id)))
                    list.Add(new(tab.Id, tab.Title, state.TerminalAgents.FirstOrDefault(agent => agent.Terminal == new TerminalRef(workspace, tab.Id))?.Record));
                if (!shown.TryGetValue(workspace, out var keys)) shown[workspace] = keys = [];
                keys.AddRange(visible.Where(key => !keys.Contains(key)));
            }
        var activeWorkspace = window is { WorkspaceMounted: true, AtHome: false } ? window.WorkspaceRoot : null;
        return new(
            projects,
            workspaces,
            activeWorkspace,
            window is { WorkspaceMounted: true } && window.ProjectRoot.Length > 0 ? window.ProjectRoot : null,
            window?.ActiveEditor(),
            new(preferences.Theme, Ui.Theme?.IsLight == true, preferences.FileLineWidth, preferences.FileLineWidthBounded,
                preferences.MarkdownLineWidth, preferences.MarkdownLineWidthBounded, state.PluginPaths),
            terminals.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TerminalTabInfo>)pair.Value),
            shown.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value),
            workbench.WorkspaceRevisions,
            loader.Registry.Roster,
            state.Platform);
    }
}