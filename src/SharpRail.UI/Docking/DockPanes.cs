namespace SharpRail.UI.Docking;

/// <summary>
/// Tabs of one centre group shown together as resizable panes instead of one at a time. It is metadata on the group
/// rather than a node in the centre tree: with vertical tabs off, a pane's members stay ordinary tabs in the strip, so
/// the tab list has to stay flat. A centre split means "both at once, always"; a pane means "one entry in the strip".
/// </summary>
public sealed class DockPane
{
    public const int MinMembers = 2;
    public const int MaxMembers = 4;

    public string Id { get; set; } = "pane:" + Guid.NewGuid().ToString("N");
    /// <summary>At least two tabs of the owning group, in the order the pane renders them; a tab is in at most one pane.</summary>
    public List<string> TabIds { get; set; } = [];
    /// <summary><c>horizontal</c> lays members out as columns, <c>vertical</c> stacks them as rows.</summary>
    public string Direction { get; set; } = "horizontal";
    /// <summary>One weight per member, in the same order, each in (0, 1).</summary>
    public List<double> Weights { get; set; } = [];

    public DockPane Copy() => new() { Id = Id, TabIds = [.. TabIds], Direction = Direction, Weights = [.. Weights] };

    internal static List<double> Even(int count) => [.. Enumerable.Repeat(1.0 / count, count)];
}

/// <summary>The vertical tab column's width bounds, in pixels.</summary>
public static class VerticalTabs
{
    public const double MinWidth = 120;
    public const double MaxWidth = 480;
    public const double DefaultWidth = 200;

    public static double ClampWidth(double width) => double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : DefaultWidth;
}

public sealed partial class LayoutSession
{
    /// <summary>Selects a tab of another workspace's centre group without switching to it; the switch shows it.</summary>
    public void SelectIn(string workspace, string groupId, string tabId) => Change(state =>
    {
        if (!state.Workspaces.TryGetValue(workspace, out var view) || view.Documents.GetValueOrDefault(groupId)?.Any(tab => tab.Id == tabId) != true)
            throw new InvalidOperationException();
        view.Selected[groupId] = tabId;
        view.FocusedCenter = view.FocusedGroup = groupId;
    });

    /// <summary>Whether a tool's body is on screen: in a visible region, an unfolded group, and that group's shown tab.</summary>
    public bool IsToolShowing(string toolId) => State.Groups.Any(group => group.Region switch
    {
        "left" => State.LeftVisible,
        "right" => State.RightVisible,
        "bottom" => State.BottomVisible,
        _ => false
    } && !group.Folded && Selected(group.Id) is { IsTool: true } shown && shown.Id == toolId);

    /// <summary>The pane a tab of a centre group belongs to in the active workspace, if any.</summary>
    public DockPane? PaneFor(string groupId, string tabId) =>
        View.Panes.GetValueOrDefault(groupId)?.FirstOrDefault(pane => pane.TabIds.Contains(tabId));

    /// <summary>
    /// Puts <paramref name="tabId"/> in the same pane as <paramref name="targetId"/>, creating one when the target is
    /// still on its own. Joining an existing pane keeps its direction and its members' proportions; the newcomer takes
    /// an equal share. A tab already in another pane leaves it first.
    /// </summary>
    public bool GroupTabs(string groupId, string tabId, string targetId, string direction) => Change(state =>
    {
        if (tabId == targetId || direction is not ("horizontal" or "vertical")) throw new InvalidOperationException();
        var view = Active(state);
        var documents = view.Documents.GetValueOrDefault(groupId) ?? throw new InvalidOperationException();
        if (!documents.Any(tab => tab.Id == tabId) || !documents.Any(tab => tab.Id == targetId)) throw new InvalidOperationException();
        var panes = view.Panes.GetValueOrDefault(groupId) ?? [];
        var existing = panes.FirstOrDefault(pane => pane.TabIds.Contains(targetId));
        if (existing?.TabIds.Contains(tabId) == true) return;
        if ((existing?.TabIds.Count ?? 1) + 1 > DockPane.MaxMembers) throw new InvalidOperationException();
        var others = panes.Where(pane => pane != existing).Select(pane => Without(pane, tabId)).OfType<DockPane>().ToList();
        DockPane joined;
        if (existing is null)
            joined = new() { TabIds = [targetId, tabId], Direction = direction, Weights = DockPane.Even(2) };
        else
        {
            var count = existing.TabIds.Count + 1;
            var total = existing.Weights.Sum();
            joined = existing.Copy();
            joined.TabIds.Add(tabId);
            joined.Weights = total <= 0 ? DockPane.Even(count) : [.. existing.Weights.Select(weight => weight / total * (1 - 1.0 / count)), 1.0 / count];
        }
        view.Panes[groupId] = [.. others, joined];
        view.Selected[groupId] = tabId;
    });

    /// <summary>Takes a tab out of its pane; a pane left with fewer than two members dissolves.</summary>
    public bool Ungroup(string groupId, string tabId) => Change(state =>
    {
        var view = Active(state);
        var panes = view.Panes.GetValueOrDefault(groupId);
        if (panes?.Any(pane => pane.TabIds.Contains(tabId)) != true) throw new InvalidOperationException();
        view.Panes[groupId] = [.. panes.Select(pane => pane.TabIds.Contains(tabId) ? Without(pane, tabId) : pane).OfType<DockPane>()];
    });

    /// <summary>Moves a member one place inside its pane; the strip order and the split order are one order.</summary>
    public bool ReorderPaneMember(string groupId, string tabId, int delta) => Change(state =>
    {
        var pane = Active(state).Panes.GetValueOrDefault(groupId)?.FirstOrDefault(item => item.TabIds.Contains(tabId)) ?? throw new InvalidOperationException();
        var from = pane.TabIds.IndexOf(tabId);
        var to = from + delta;
        if (delta is not (-1 or 1) || to < 0 || to >= pane.TabIds.Count) throw new InvalidOperationException();
        (pane.TabIds[from], pane.TabIds[to]) = (pane.TabIds[to], pane.TabIds[from]);
        (pane.Weights[from], pane.Weights[to]) = (pane.Weights[to], pane.Weights[from]);
    });

    /// <summary>Flips a pane between columns and rows; only how it renders changes.</summary>
    public bool SetPaneDirection(string groupId, string paneId, string direction) => Change(state =>
    {
        if (direction is not ("horizontal" or "vertical")) throw new InvalidOperationException();
        var pane = Active(state).Panes.GetValueOrDefault(groupId)?.FirstOrDefault(item => item.Id == paneId) ?? throw new InvalidOperationException();
        pane.Direction = direction;
    }, groupId);

    /// <summary>Persists a divider drag; ignored unless the weights still describe the same members.</summary>
    public bool SetPaneWeights(string groupId, string paneId, IReadOnlyList<double> weights) => Change(state =>
    {
        var pane = Active(state).Panes.GetValueOrDefault(groupId)?.FirstOrDefault(item => item.Id == paneId) ?? throw new InvalidOperationException();
        if (pane.TabIds.Count != weights.Count || weights.Any(weight => !(weight > 0 && weight < 1))) throw new InvalidOperationException();
        pane.Weights = [.. weights];
    });

    private static DockPane? Without(DockPane pane, string tabId)
    {
        if (!pane.TabIds.Contains(tabId)) return pane;
        var members = pane.TabIds.Where(id => id != tabId).ToList();
        return members.Count < DockPane.MinMembers ? null : new() { Id = pane.Id, TabIds = members, Direction = pane.Direction, Weights = DockPane.Even(members.Count) };
    }

    // The preview slot can be a pane's member; the newcomer takes its place instead of leaving the pane a member short.
    private static void ReplacePaneMember(WorkspaceView view, string groupId, string replaced, string tabId)
    {
        foreach (var pane in view.Panes.GetValueOrDefault(groupId) ?? [])
            for (var index = 0; index < pane.TabIds.Count; index++)
                if (pane.TabIds[index] == replaced) pane.TabIds[index] = tabId;
    }

    // A member dropped inside its own pane's run reorders the pane; anywhere else it leaves the pane.
    private static void PaneAfterMemberDrop(WorkspaceView view, string groupId, string tabId, IReadOnlyList<DockTab> without, int insertion,
        IReadOnlyList<DockTab> after)
    {
        var panes = view.Panes.GetValueOrDefault(groupId);
        var pane = panes?.FirstOrDefault(item => item.TabIds.Contains(tabId));
        if (panes is null || pane is null) return;
        var positions = without.Select((tab, position) => (tab, position)).Where(item => pane.TabIds.Contains(item.tab.Id)).Select(item => item.position).ToArray();
        if (positions.Length == 0 || insertion < positions.Min() || insertion > positions.Max() + 1)
        {
            view.Panes[groupId] = [.. panes.Select(item => item == pane ? Without(item, tabId) : item).OfType<DockPane>()];
            return;
        }
        var order = after.Select((tab, position) => (tab.Id, position)).ToDictionary(item => item.Id, item => item.position);
        var weightOf = pane.TabIds.Zip(pane.Weights).ToDictionary(item => item.First, item => item.Second);
        pane.TabIds = [.. pane.TabIds.OrderBy(id => order.GetValueOrDefault(id))];
        pane.Weights = [.. pane.TabIds.Select(id => weightOf.GetValueOrDefault(id))];
    }

    /// <summary>
    /// Re-derives every pane from the tabs that remain, as every mutation must: a tab closed or moved away leaves no
    /// membership behind, a pane below two members dissolves, and a pane's members are pulled into one contiguous run
    /// anchored where its first member already sat, in the pane's order.
    /// </summary>
    internal static void NormalizePanes(DockState state)
    {
        foreach (var view in state.Workspaces.Values)
        {
            foreach (var groupId in view.Panes.Keys.ToArray())
            {
                var documents = view.Documents.GetValueOrDefault(groupId) ?? [];
                var present = documents.Select(tab => tab.Id).ToHashSet();
                var claimed = new HashSet<string>();
                var kept = new List<DockPane>();
                foreach (var pane in view.Panes[groupId])
                {
                    var members = pane.TabIds.Where(id => present.Contains(id) && claimed.Add(id)).Distinct().Take(DockPane.MaxMembers).ToList();
                    if (members.Count < DockPane.MinMembers) continue;
                    var weights = members.Count == pane.TabIds.Count && pane.Weights.Count == members.Count && pane.Weights.All(weight => weight > 0 && weight < 1)
                        ? pane.Weights : DockPane.Even(members.Count);
                    kept.Add(new() { Id = pane.Id, TabIds = members, Direction = pane.Direction is "vertical" ? "vertical" : "horizontal", Weights = [.. weights] });
                }
                if (kept.Count == 0) { view.Panes.Remove(groupId); continue; }
                view.Panes[groupId] = kept;
                var byId = documents.ToDictionary(tab => tab.Id);
                var paneOf = kept.SelectMany(pane => pane.TabIds.Select(id => (id, pane))).ToDictionary(item => item.id, item => item.pane);
                var ordered = new List<DockTab>();
                var taken = new HashSet<string>();
                foreach (var tab in documents)
                {
                    if (!taken.Add(tab.Id)) continue;
                    if (!paneOf.TryGetValue(tab.Id, out var pane)) { ordered.Add(tab); continue; }
                    taken.Remove(tab.Id);
                    foreach (var id in pane.TabIds.Where(taken.Add)) ordered.Add(byId[id]);
                }
                view.Documents[groupId] = ordered;
            }
        }
    }
}