namespace SharpRail.UI.Docking;

/// <summary>
/// Where a document or terminal tab sat when the user closed it: its workspace, group, index in the group's flat order,
/// and the pane it shared, so undo can put it back in the same place.
/// </summary>
public sealed record ClosedTab(string Workspace, string GroupId, int Index, DockTab Tab, DockPane? Pane);

public sealed partial class LayoutSession
{
    /// <summary>
    /// Raised by <see cref="Close"/> once a close of a document or terminal tab is certain, before <see cref="Changed"/>
    /// publishes it. Closes the layout makes for other reasons (catalog reconciliation, moves, removed groups) do not raise it.
    /// </summary>
    public event Action<ClosedTab>? TabClosing;

    private ClosedTab? Describe(string groupId, string tabId)
    {
        var view = View;
        if (view.Documents.GetValueOrDefault(groupId) is not { } tabs || tabs.FindIndex(tab => tab.Id == tabId) is var index && index < 0) return null;
        return new(State.ActiveWorkspace, groupId, index, tabs[index], PaneFor(groupId, tabId)?.Copy());
    }

    /// <summary>
    /// Puts a closed tab back where it was in the active workspace and selects it: its group (else the focused centre
    /// group), its index, and its pane when the other members are still there and in no other pane. Refuses a tab
    /// of another workspace or one that is open again.
    /// </summary>
    public bool Reopen(ClosedTab closed) => Change(state =>
    {
        if (state.ActiveWorkspace != closed.Workspace) throw new InvalidOperationException();
        var view = Active(state);
        if (view.Documents.Values.Any(tabs => tabs.Any(tab => tab.Id == closed.Tab.Id))) throw new InvalidOperationException();
        var leaves = state.Center.Leaves().ToArray();
        var group = state.Groups.FirstOrDefault(item => item.Id == closed.GroupId);
        // Side groups hold terminals only, and never beside tools.
        var fits = group is not null && (group.Region == "center" || closed.Tab.Kind == "terminal" && group.Tools.Count == 0);
        var target = fits ? group!.Id : leaves.Contains(view.FocusedCenter) ? view.FocusedCenter : leaves[0];
        group = state.Groups.Single(item => item.Id == target);
        if (!view.Documents.TryGetValue(target, out var documents)) view.Documents[target] = documents = [];
        documents.Insert(Math.Clamp(closed.Index, 0, documents.Count), closed.Tab with { Preview = false });
        if (target == closed.GroupId && closed.Pane is { } pane)
        {
            var panes = view.Panes.GetValueOrDefault(target) ?? [];
            var present = documents.Select(tab => tab.Id).ToHashSet();
            var existing = panes.FirstOrDefault(item => item.Id == pane.Id);
            var members = pane.TabIds.Where(id => present.Contains(id) &&
                (id == closed.Tab.Id || existing?.TabIds.Contains(id) == true || !panes.Any(item => item.TabIds.Contains(id)))).ToList();
            if (members.Count >= DockPane.MinMembers)
            {
                var weights = members.Count == pane.TabIds.Count ? pane.Weights : DockPane.Even(members.Count);
                view.Panes[target] = [.. panes.Where(item => item != existing), new() { Id = pane.Id, TabIds = members, Direction = pane.Direction, Weights = [.. weights] }];
            }
        }
        group.Folded = false;
        view.Selected[target] = closed.Tab.Id; view.FocusedGroup = target;
        if (group.Region == "center") view.FocusedCenter = target;
        else view.FocusedAuxiliary[group.Region] = target;
        if (group.Region == "left") state.LeftVisible = true;
        if (group.Region == "right") state.RightVisible = true;
    });

    /// <summary>The group of the active workspace that holds a tab, if any.</summary>
    public string? GroupOf(string tabId) => View.Documents.FirstOrDefault(pair => pair.Value.Any(tab => tab.Id == tabId)).Key;
}
