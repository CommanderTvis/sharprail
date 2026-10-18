namespace SharpRail.UI.Docking;

public sealed partial class LayoutSession
{
    private static ProjectPaneState CaptureProjectPanes(DockState state, WorkspaceView? view = null)
    {
        view ??= state.Workspaces.GetValueOrDefault(state.ActiveWorkspace) ?? new();
        return new()
        {
            OpenTools = state.Groups.SelectMany(group => group.Tools).Select(tool => tool.Id).ToList(),
            FoldedGroups = state.Groups.Where(group => group.Folded).Select(group => group.Id).ToHashSet(),
            SelectedTools = state.Groups.Where(group => group.Tools.Count > 0).ToDictionary(group => group.Id,
                group => group.Tools.FirstOrDefault(tool => tool.Id == view.Selected.GetValueOrDefault(group.Id))?.Id ?? group.Tools[0].Id),
            LeftVisible = state.LeftVisible,
            RightVisible = state.RightVisible,
            HomeProjectsVisible = state.HomeProjectsVisible
        };
    }

    private static void RememberProjectPanes(DockState state, WorkspaceView? view = null)
    {
        if (state.DefaultProjectPanes is null) return;
        var panes = CaptureProjectPanes(state, view);
        if (state.ActiveProject.Length == 0) state.DefaultProjectPanes = panes;
        else state.ProjectPanes[state.ActiveProject] = panes;
    }

    private void SwitchProjectPanes(DockState state, string project, WorkspaceView previous)
    {
        if (state.ActiveProject == project) return;
        state.DefaultProjectPanes ??= CaptureProjectPanes(state, previous);
        RememberProjectPanes(state, previous);
        state.ActiveProject = project;
        var panes = state.ProjectPanes.GetValueOrDefault(project) ?? state.DefaultProjectPanes;
        var open = panes.OpenTools.ToHashSet();
        var closing = state.Groups.SelectMany(group => ProjectTabs(state, group.Id).Select((tab, position) => (group.Id, Tab: tab, Position: position)))
            .Where(item => item.Tab.IsTool && !open.Contains(item.Tab.Id)).ToArray();
        foreach (var item in closing) CloseIn(state, item.Id, item.Tab.Id);
        foreach (var id in panes.OpenTools.OrderBy(id => state.ToolRestorePositions.GetValueOrDefault(id, int.MaxValue)))
            RestoreToolIn(state, id);
        foreach (var group in state.Groups.Where(group => group.Region != "center"))
        {
            group.Folded = panes.FoldedGroups.Contains(group.Id);
            if (panes.SelectedTools.TryGetValue(group.Id, out var selected) && group.Tools.Any(tool => tool.Id == selected))
                Active(state).Selected[group.Id] = selected;
        }
        state.LeftVisible = panes.LeftVisible;
        state.RightVisible = panes.RightVisible;
        state.HomeProjectsVisible = panes.HomeProjectsVisible;
    }

    private static bool ValidProjectPanes(ProjectPaneState panes) =>
        panes.OpenTools is not null && panes.FoldedGroups is not null && panes.SelectedTools is not null &&
        panes.OpenTools.All(DockState.IsToolId) && panes.OpenTools.Distinct().Count() == panes.OpenTools.Count &&
        panes.FoldedGroups.All(id => !string.IsNullOrWhiteSpace(id)) && panes.SelectedTools.Values.All(DockState.IsToolId);
}
