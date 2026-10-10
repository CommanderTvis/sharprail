using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Docking;

public sealed partial class LayoutSession
{
    public DockState State { get; private set; }
    public long Epoch { get; private set; }
    public Func<string, string, bool>? CanRemoveDocument { get; set; }
    // Raised with the documents that vetoed a transition and a callback that retries it.
    public event Action<IReadOnlyList<(string Workspace, string TabId)>, Action>? RemovalBlocked;
    public event Action? Changed;
    public event Action<string>? SelectionChanged;
    public event Action<string>? Navigating;
    public event Action? Focused;
    public LayoutSession(DockState? state = null)
    {
        State = state is not null && IsValid(state) ? state : DockState.Preset("balanced");
    }

    public static readonly IReadOnlyList<DockToolInfo> CoreTools =
    [
        .. DockState.ToolNames.Select(id => new DockToolInfo(id, DockState.Tool(id).Title, id switch
        {
            "projects" => "folderTab",
            "files" => "file",
            "changes" => "fileDiff",
            _ => "discuss"
        }, DockState.ToolRegion(id)))
    ];
    private IReadOnlyList<DockToolInfo> tools = CoreTools;

    /// <summary>Every tool this layout can name and restore: core's, then the extra ones the workbench composes in.</summary>
    public IReadOnlyList<DockToolInfo> Tools => tools;
    public event Action? ToolsChanged;

    /// <summary>Replaces the composed tools after core's; a tab naming a tool absent from the list keeps its slot.</summary>
    public void SetExtraTools(IReadOnlyList<DockToolInfo> extra)
    {
        if (tools.Skip(CoreTools.Count).SequenceEqual(extra)) return;
        tools = [.. CoreTools, .. extra];
        ToolsChanged?.Invoke();
    }

    public DockToolInfo? Tool(string id) => tools.FirstOrDefault(tool => tool.Id == id);
    public string ToolTitle(DockTab tab) => tab.IsTool ? Tool(tab.Id)?.Title ?? tab.Title : tab.Title;
    private string ToolRegion(string id) => Tool(id)?.Region ?? DockState.ToolRegion(id);

    public WorkspaceView View => State.Workspaces.GetValueOrDefault(State.ActiveWorkspace) ?? new();
    public DockGroup Group(string id) => State.Groups.Single(group => group.Id == id);
    public IReadOnlyList<DockTab> Tabs(string groupId) => ProjectTabs(State, groupId);

    private static DockTab[] ProjectTabs(DockState state, string groupId) =>
        ProjectTabs(state, state.Workspaces.GetValueOrDefault(state.ActiveWorkspace) ?? new(), groupId);

    private static DockTab[] ProjectTabs(DockState state, WorkspaceView view, string groupId)
    {
        var tools = state.Groups.Single(group => group.Id == groupId).Tools;
        var resources = view.Documents.GetValueOrDefault(groupId) ?? [];
        return tools.SelectMany(tool => resources.Where(tab => view.BeforeToolByTabId.GetValueOrDefault(tab.Id) == tool.Id)
            .Append(tool)).Concat(resources.Where(tab => !tools.Any(tool => view.BeforeToolByTabId.GetValueOrDefault(tab.Id) == tool.Id))).ToArray();
    }
    public DockTab? Selected(string groupId)
    {
        var tabs = Tabs(groupId);
        return tabs.FirstOrDefault(tab => tab.Id == View.Selected.GetValueOrDefault(groupId)) ?? tabs.FirstOrDefault();
    }

    public void Focus(string groupId)
    {
        var view = Active(State);
        view.FocusedGroup = groupId;
        var region = Group(groupId).Region;
        if (region != "center") view.FocusedAuxiliary[region] = groupId;
        if (State.Center.Leaves().Contains(groupId)) view.FocusedCenter = groupId;
        Focused?.Invoke();
    }

    private bool Change(Action<DockState> mutation, string? selectionGroup = null)
    {
        var candidate = State.Copy();
        try { mutation(candidate); }
        catch (InvalidOperationException) { return false; }
        NormalizePanes(candidate);
        if (!IsValid(candidate)) return false;
        if (CanRemoveDocument is not null)
        {
            var blocked = new List<(string Workspace, string TabId)>();
            foreach (var (workspace, view) in State.Workspaces)
            {
                var remaining = candidate.Workspaces.GetValueOrDefault(workspace)?.Documents.Values.SelectMany(tabs => tabs).Select(tab => tab.Id).ToHashSet() ?? [];
                foreach (var tab in view.Documents.Values.SelectMany(tabs => tabs))
                    if (!remaining.Contains(tab.Id) && !CanRemoveDocument(workspace, tab.Id)) blocked.Add((workspace, tab.Id));
            }
            if (blocked.Count > 0) { RemovalBlocked?.Invoke(blocked, () => Change(mutation, selectionGroup)); return false; }
        }
        State = candidate; Epoch++;
        if (selectionGroup is null) Changed?.Invoke();
        else { SelectionChanged?.Invoke(selectionGroup); Focused?.Invoke(); }
        return true;
    }

    private static WorkspaceView Active(DockState state)
    {
        if (!state.Workspaces.TryGetValue(state.ActiveWorkspace, out var view))
            state.Workspaces[state.ActiveWorkspace] = view = new();
        return view;
    }

    /// <summary>Drops the view of a workspace that no longer exists; nothing is asked, since its documents went with it.</summary>
    public void DropWorkspace(string path)
    {
        if (path == State.ActiveWorkspace || !State.Workspaces.ContainsKey(path)) return;
        var next = State.Copy();
        next.Workspaces.Remove(path);
        State = next; Epoch++;
        Changed?.Invoke();
    }

    public void SwitchWorkspace(string path) => Change(state =>
    {
        var previous = Active(state);
        var fresh = path.Length > 0 && !state.Workspaces.ContainsKey(path);
        state.ActiveWorkspace = path;
        var view = Active(state);
        // A workspace opens one terminal once; closing it later never brings it back.
        if (fresh)
        {
            var bottom = state.Groups.FirstOrDefault(group => group.Region == "bottom");
            if (bottom is null)
            {
                bottom = new DockGroup { Region = "bottom" };
                PrepareAuxiliaryCreation(state, bottom); state.Groups.Add(bottom);
            }
            // A workspace's first terminal has the key every client gives it; a project's home is not a shared workspace.
            var terminal = new DockTab(path.StartsWith("home:", StringComparison.Ordinal) ? "terminal:" + Guid.NewGuid().ToString("N") : TerminalTab.InitialKey,
                "Terminal " + view.NextTerminalNumber++, "terminal");
            view.Documents[bottom.Id] = [terminal];
            view.Selected[bottom.Id] = terminal.Id;
        }
        foreach (var group in state.Groups.Where(group => group.Region != "center"))
        {
            var selected = previous.Selected.GetValueOrDefault(group.Id) ?? group.Tools.FirstOrDefault()?.Id;
            if (selected is not null && group.Tools.Any(tab => tab.Id == selected)) view.Selected[group.Id] = selected;
        }
        if (!state.Center.Leaves().Contains(view.FocusedCenter)) view.FocusedCenter = state.Center.Leaves().First();
    });

    /// <summary>
    /// Makes a workspace's terminal tabs those of the host's catalog. A tab the catalog lacks goes without
    /// asking, unless its reservation is still <paramref name="pending"/>; one this view lacks lands in the
    /// last-focused (else last) bottom group, else the last-focused centre group, without selecting it,
    /// revealing its region or taking focus; titles follow the catalog. Returns whether anything changed.
    /// With <paramref name="center"/>, for a window whose only tab list is the centre's, the last-focused centre
    /// group takes them instead, and with them the terminals of every hidden region.
    /// </summary>
    public bool ReconcileTerminals(string workspace, IReadOnlyList<TerminalTab> catalog, IReadOnlySet<string> pending, bool center = false)
    {
        if (!State.Workspaces.ContainsKey(workspace)) return false;
        var next = State.Copy();
        var view = next.Workspaces[workspace];
        var changed = !view.TerminalsShared;
        view.TerminalsShared = true;
        var known = catalog.ToDictionary(tab => tab.Key, tab => tab.Title);
        foreach (var (groupId, tabs) in view.Documents.ToArray())
        {
            foreach (var tab in tabs.Where(tab => tab.Kind == "terminal" && !known.ContainsKey(tab.Id) && !pending.Contains(tab.Id)).ToArray())
            {
                var before = next.Groups.Any(group => group.Id == groupId) ? ProjectTabs(next, view, groupId) : [.. tabs];
                var position = Array.FindIndex(before, item => item.Id == tab.Id);
                var remaining = before.Where(item => item.Id != tab.Id).ToArray();
                tabs.Remove(tab); view.BeforeToolByTabId.Remove(tab.Id); changed = true;
                if ((view.Selected.GetValueOrDefault(groupId) ?? before.FirstOrDefault()?.Id) != tab.Id) continue;
                if (remaining.Length == 0) view.Selected.Remove(groupId);
                else view.Selected[groupId] = remaining[Math.Clamp(position, 0, remaining.Length - 1)].Id;
            }
            for (var i = 0; i < tabs.Count; i++)
                if (tabs[i].Kind == "terminal" && known.TryGetValue(tabs[i].Id, out var title) && tabs[i].Title != title) { tabs[i] = tabs[i] with { Title = title }; changed = true; }
        }
        var placed = view.Documents.Values.SelectMany(tabs => tabs).Select(tab => tab.Id).ToHashSet();
        var bottoms = next.Groups.Where(group => group.Region == "bottom").Select(group => group.Id).ToArray();
        var leaves = next.Center.Leaves().ToArray();
        var focused = leaves.Contains(view.FocusedCenter) ? view.FocusedCenter : leaves[0];
        var target = center ? focused : bottoms.Contains(view.FocusedAuxiliary.GetValueOrDefault("bottom")) ? view.FocusedAuxiliary["bottom"] : bottoms.LastOrDefault() ?? focused;
        if (center)
            foreach (var group in next.Groups.Where(group => group.Region switch { "left" => !next.LeftVisible, "right" => !next.RightVisible, "bottom" => !next.BottomVisible, _ => false }))
                foreach (var tab in view.Documents.GetValueOrDefault(group.Id)?.Where(tab => tab.Kind == "terminal").ToArray() ?? [])
                {
                    view.Documents[group.Id].Remove(tab); view.BeforeToolByTabId.Remove(tab.Id); changed = true;
                    if (view.Selected.GetValueOrDefault(group.Id) == tab.Id) view.Selected.Remove(group.Id);
                    if (!view.Documents.TryGetValue(target, out var tabs)) view.Documents[target] = tabs = [];
                    tabs.Add(tab);
                }
        foreach (var tab in catalog)
        {
            // New terminals here keep counting past every numbered title a peer already used.
            if (tab.Title.StartsWith("Terminal ", StringComparison.Ordinal) && int.TryParse(tab.Title.AsSpan(9), out var number) && number < int.MaxValue)
                view.NextTerminalNumber = Math.Max(view.NextTerminalNumber, number + 1);
            if (!placed.Add(tab.Key)) continue;
            if (!view.Documents.TryGetValue(target, out var tabs)) view.Documents[target] = tabs = [];
            tabs.Add(new DockTab(tab.Key, tab.Title, "terminal")); changed = true;
        }
        if (!changed) return false;
        NormalizePanes(next);
        if (!IsValid(next)) return false;
        State = next; Epoch++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves what a group opens on without navigating or taking focus.</summary>
    public void Reseat(string groupId, string tabId) => Change(state => Active(state).Selected[groupId] = tabId, groupId);

    public void Select(string groupId, string tabId)
    {
        Navigating?.Invoke(groupId);
        if (Selected(groupId)?.Id == tabId)
        {
            Focus(groupId);
            return;
        }
        Change(state =>
        {
            var view = Active(state);
            view.Selected[groupId] = tabId; view.FocusedGroup = groupId;
            var region = state.Groups.Single(group => group.Id == groupId).Region;
            if (region != "center") view.FocusedAuxiliary[region] = groupId;
            if (state.Center.Leaves().Contains(groupId)) view.FocusedCenter = groupId;
        }, groupId);
    }

    public void Open(DockTab tab, bool keep, string? groupId = null, bool claimPreview = false, bool activate = true)
    {
        Change(state =>
        {
            var view = Active(state);
            foreach (var pair in view.Documents)
            {
                var existing = pair.Value.FindIndex(item => item.Id == tab.Id);
                if (existing < 0) continue;
                pair.Value[existing] = tab with { Preview = !keep && pair.Value[existing].Preview };
                if (activate) { view.Selected[pair.Key] = tab.Id; view.FocusedCenter = pair.Key; view.FocusedGroup = pair.Key; }
                return;
            }
            var requested = groupId ?? view.FocusedCenter;
            var target = state.Center.Leaves().Contains(requested) ? requested : state.Center.Leaves().First();
            if (!view.Documents.TryGetValue(target, out var documents)) view.Documents[target] = documents = [];
            var selected = view.Selected.GetValueOrDefault(target) ?? documents.FirstOrDefault()?.Id;
            var preview = documents.FindIndex(item => item.Preview);
            if ((!keep || claimPreview) && preview >= 0)
            {
                ReplacePaneMember(view, target, documents[preview].Id, tab.Id);
                documents[preview] = tab with { Preview = !keep };
            }
            else documents.Add(tab with { Preview = !keep });
            view.Selected[target] = !activate && selected is not null && documents.Any(item => item.Id == selected) ? selected : tab.Id;
            if (activate) { view.FocusedCenter = target; view.FocusedGroup = target; }
        });
    }

    /// <summary>
    /// Gives terminal tabs the titles their agents chose, in whichever workspace shows them. The host owns the
    /// titles; the tab keeps the last one it was given, as it keeps the "Terminal N" it was created with.
    /// </summary>
    public void TitleTerminals(IReadOnlyDictionary<(string Workspace, string TabId), string> titles)
    {
        bool Stale(string workspace, DockTab tab) => tab.Kind == "terminal" && titles.TryGetValue((workspace, tab.Id), out var title) && tab.Title != title;
        if (!State.Workspaces.Any(pair => pair.Value.Documents.Values.Any(tabs => tabs.Any(tab => Stale(pair.Key, tab))))) return;
        Change(state =>
        {
            foreach (var (workspace, view) in state.Workspaces)
                foreach (var tabs in view.Documents.Values)
                    for (var index = 0; index < tabs.Count; index++)
                        if (Stale(workspace, tabs[index])) tabs[index] = tabs[index] with { Title = titles[(workspace, tabs[index].Id)] };
        });
    }

    public void Keep(string groupId, string tabId) => Change(state =>
    {
        var docs = Active(state).Documents.GetValueOrDefault(groupId);
        var index = docs?.FindIndex(tab => tab.Id == tabId) ?? -1;
        if (index >= 0) docs![index] = docs[index] with { Preview = false };
    });

    public void NewTerminal(string groupId, string? id = null) => Change(state =>
    {
        var group = state.Groups.Single(item => item.Id == groupId);
        var view = Active(state);
        if (!view.Documents.TryGetValue(groupId, out var tabs)) view.Documents[groupId] = tabs = [];
        var tab = new DockTab(id ?? "terminal:" + Guid.NewGuid().ToString("N"), "Terminal " + view.NextTerminalNumber++, "terminal");
        tabs.Add(tab); group.Folded = false;
        view.Selected[groupId] = tab.Id; view.FocusedGroup = groupId;
        if (group.Region == "center") view.FocusedCenter = groupId;
        else view.FocusedAuxiliary[group.Region] = groupId;
        if (group.Region == "left") state.LeftVisible = true;
        if (group.Region == "right") state.RightVisible = true;
        if (group.Region == "bottom") state.BottomVisible = true;
    });

    public void Close(string groupId, string tabId)
    {
        Navigating?.Invoke(groupId);
        Change(state =>
        {
            var group = state.Groups.Single(item => item.Id == groupId);
            var view = Active(state);
            var before = ProjectTabs(state, groupId).ToList();
            var position = before.FindIndex(tab => tab.Id == tabId);
            var selected = view.Selected.GetValueOrDefault(groupId) ?? before.FirstOrDefault()?.Id;
            if (group.Tools.Any(tab => tab.Id == tabId))
            {
                state.ToolRestorePositions[tabId] = position;
                group.Tools.RemoveAll(tab => tab.Id == tabId); state.ToolRestore[tabId] = groupId;
                AnchorResources(view, before.Where(tab => tab.Id != tabId).ToArray());
            }
            else view.Documents.GetValueOrDefault(groupId)?.RemoveAll(tab => tab.Id == tabId);
            view.BeforeToolByTabId.Remove(tabId);
            if (selected != tabId) return;
            var remaining = before.Where(tab => tab.Id != tabId).ToArray();
            if (remaining.Length == 0) view.Selected.Remove(groupId);
            else view.Selected[groupId] = remaining[Math.Clamp(position, 0, remaining.Length - 1)].Id;
        });
    }

    /// <summary>
    /// Closes the selected (else first) tab of the keyboard-focused group, otherwise of the last-focused
    /// center group, through <see cref="Close"/>. A tool tab, folded group or hidden region has no target
    /// and no fallback. Returns whether a target was found.
    /// </summary>
    public bool RequestClose()
    {
        var view = View;
        var groupId = State.Groups.Any(item => item.Id == view.FocusedGroup) ? view.FocusedGroup : view.FocusedCenter;
        var group = State.Groups.FirstOrDefault(item => item.Id == groupId);
        if (group is null || group.Folded) return false;
        var visible = group.Region switch { "left" => State.LeftVisible, "right" => State.RightVisible, "bottom" => State.BottomVisible, _ => true };
        if (!visible || Selected(groupId) is not { IsTool: false } tab) return false;
        Close(groupId, tab.Id);
        return true;
    }

    public bool CanMove(string tabId, string source, string destination, int index, string edge = "")
    {
        var candidate = State.Copy();
        try { MoveIn(candidate, tabId, source, destination, index, edge); return IsValid(candidate); }
        catch (InvalidOperationException) { return false; }
    }

    public bool Move(string tabId, string source, string destination, int index, string edge = "")
        => Change(state => MoveIn(state, tabId, source, destination, index, edge));

    public bool CanMoveToRegion(string tabId, string source, string region)
    {
        var candidate = State.Copy();
        try { MoveToRegionIn(candidate, tabId, source, region); return IsValid(candidate); }
        catch (InvalidOperationException) { return false; }
    }

    public bool MoveToRegion(string tabId, string source, string region)
        => Change(state => MoveToRegionIn(state, tabId, source, region));

    public bool MoveToNewRegionGroup(string tabId, string source, string region, bool atStart)
        => Change(state =>
        {
            if (region is not ("left" or "right" or "bottom")) throw new InvalidOperationException();
            var groups = state.Groups.Where(group => group.Region == region).ToArray();
            if (groups.Length > 0)
                MoveIn(state, tabId, source, atStart ? groups[0].Id : groups[^1].Id, 0, atStart ? "before" : "after");
            else
            {
                var created = new DockGroup { Region = region };
                PrepareAuxiliaryCreation(state, created); state.Groups.Add(created);
                MoveIn(state, tabId, source, created.Id, 0, "");
            }
        });

    private static void MoveToRegionIn(DockState state, string tabId, string source, string region)
    {
        if (region is not ("left" or "right" or "bottom")) throw new InvalidOperationException();
        var remembered = Active(state).FocusedAuxiliary.GetValueOrDefault(region);
        var group = state.Groups.FirstOrDefault(item => region == "bottom" && item.Region == region && item.Id == remembered)
            ?? state.Groups.LastOrDefault(item => item.Region == region);
        if (region != "bottom" && group is not null)
        {
            MoveIn(state, tabId, source, group.Id, 0, "after"); return;
        }
        if (group is null)
        {
            group = new() { Region = region }; PrepareAuxiliaryCreation(state, group); state.Groups.Add(group);
        }
        MoveIn(state, tabId, source, group.Id, group.Tools.Count, "");
    }

    private static void MoveIn(DockState state, string id, string source, string destination, int index, string edge)
    {
        var from = state.Groups.Single(group => group.Id == source);
        var to = state.Groups.Single(group => group.Id == destination);
        var view = Active(state);
        var tool = from.Tools.FirstOrDefault(tab => tab.Id == id);
        var document = view.Documents.GetValueOrDefault(source)?.FirstOrDefault(tab => tab.Id == id);
        var tab = tool ?? document ?? throw new InvalidOperationException();
        if (tab.IsTool ? to.Region == "center" : tab.Kind != "terminal" && to.Region != "center") throw new InvalidOperationException();
        if (edge.Length > 0)
        {
            var created = new DockGroup { Region = to.Region };
            if (to.Region == "center")
            {
                if (state.Center.Leaves().Count() >= 4) throw new InvalidOperationException();
                state.Center = Split(state.Center, destination, created.Id, edge);
            }
            else
            {
                PrepareAuxiliaryCreation(state, created);
                var offset = edge is "before" or "left" or "top" ? 0 : 1;
                state.Groups.Insert(state.Groups.IndexOf(to) + offset, created);
            }
            if (!state.Groups.Contains(created)) state.Groups.Add(created);
            to = created; destination = created.Id; index = 0;
        }
        if (tab.IsTool)
        {
            var sourceOrder = ProjectTabs(state, source).ToList();
            var order = ProjectTabs(state, destination).ToList();
            var prior = sourceOrder.FindIndex(item => item.Id == id);
            if (source == destination && index > prior) index--;
            if (source == destination && index == prior) throw new InvalidOperationException();
            order.RemoveAll(item => item.Id == id);
            order.Insert(Math.Clamp(index, 0, order.Count), tab);
            from.Tools.Remove(tab); to.Tools = order.Where(item => item.IsTool).ToList();
            AnchorResources(view, order);
            if (source != destination) AnchorResources(view, sourceOrder.Where(item => item.Id != id).ToList());
            state.ToolRestore.Remove(id);
            state.ToolRestorePositions.Remove(id);
        }
        else
        {
            var documents = view.Documents[source];
            var original = documents.Select(item => item.Id).ToArray();
            var prior = Array.FindIndex(ProjectTabs(state, source), item => item.Id == id);
            if (source == destination && index > prior) index--;
            if (source == destination && index == prior) throw new InvalidOperationException();
            var order = ProjectTabs(state, destination).Where(item => item.Id != id).ToArray();
            index = Math.Clamp(index, 0, order.Length);
            documents.Remove(tab);
            if (!view.Documents.TryGetValue(destination, out var target)) view.Documents[destination] = target = [];
            target.Insert(order.Take(index).Count(item => !item.IsTool), tab with { Preview = false });
            if (source == destination && to.Region == "center")
            {
                // A pane's members stay one run, so a drop between them is pulled aside; one that ends where it began is refused.
                PaneAfterMemberDrop(view, source, id, order, index, target);
                NormalizePanes(state);
                if (view.Documents[source].Select(item => item.Id).SequenceEqual(original)) throw new InvalidOperationException();
            }
            var anchor = order.Skip(index).FirstOrDefault(item => item.IsTool)?.Id;
            if (anchor is null) view.BeforeToolByTabId.Remove(id);
            else view.BeforeToolByTabId[id] = anchor;
        }
        view.Selected.Remove(source);
        view.Selected[destination] = id; view.FocusedGroup = destination;
        if (to.Region != "center") view.FocusedAuxiliary[to.Region] = destination;
        if (to.Region == "center") view.FocusedCenter = destination;
        if (to.Region == "left") state.LeftVisible = true;
        if (to.Region == "right") state.RightVisible = true;
        if (to.Region == "bottom") state.BottomVisible = true;
        to.Folded = false;
    }

    private static void AnchorResources(WorkspaceView view, IReadOnlyList<DockTab> order)
    {
        string? anchor = null;
        for (var index = order.Count - 1; index >= 0; index--)
        {
            var tab = order[index];
            if (tab.IsTool) { anchor = tab.Id; continue; }
            if (anchor is null) view.BeforeToolByTabId.Remove(tab.Id);
            else view.BeforeToolByTabId[tab.Id] = anchor;
        }
    }

    private static CenterNode Split(CenterNode node, string target, string created, string edge)
    {
        if (node.IsLeaf)
        {
            if (node.GroupId != target) return node;
            var first = edge is "left" or "top";
            return new()
            {
                Axis = edge is "left" or "right" ? "horizontal" : "vertical",
                First = new() { GroupId = first ? created : target },
                Second = new() { GroupId = first ? target : created }
            };
        }
        node.First = Split(node.First!, target, created, edge);
        node.Second = Split(node.Second!, target, created, edge);
        return node;
    }

    public bool NewGroup(string target, string edge) => Change(state =>
    {
        var prior = state.Groups.Single(group => group.Id == target);
        var next = new DockGroup { Region = prior.Region };
        if (prior.Region == "center")
        {
            if (state.Center.Leaves().Count() >= 4) throw new InvalidOperationException();
            state.Center = Split(state.Center, target, next.Id, edge);
            state.Groups.Add(next);
        }
        else
        {
            PrepareAuxiliaryCreation(state, next);
            state.Groups.Insert(state.Groups.IndexOf(prior) + (edge == "before" ? 0 : 1), next);
        }
    });

    public bool RemoveGroup(string id) => Change(state =>
    {
        var group = state.Groups.Single(item => item.Id == id);
        var candidates = state.Groups.Where(item => item.Region == group.Region && item.Id != id).ToArray();
        if (candidates.Length == 0 && (group.Region == "center" || group.Tools.Count > 0)) throw new InvalidOperationException();
        var regional = state.Groups.Where(item => item.Region == group.Region).ToArray();
        var position = Array.IndexOf(regional, group);
        var leaves = state.Center.Leaves().ToArray();
        var centerPosition = Array.IndexOf(leaves, id);
        var target = group.Region == "center" ? state.Groups.Single(item => item.Id == leaves[centerPosition > 0 ? centerPosition - 1 : 1]) :
            candidates.Length == 0 ? null : regional[position > 0 ? position - 1 : 1];
        if (target is not null)
        {
            target.Tools.AddRange(group.Tools);
            if (group.Region != "center")
            {
                target.Weight += group.Weight;
                var total = candidates.Sum(item => item.Weight);
                foreach (var candidate in candidates) candidate.Weight /= total;
            }
        }
        foreach (var view in state.Workspaces.Values)
        {
            var docs = view.Documents.GetValueOrDefault(id) ?? [];
            if (target is not null && docs.Count > 0)
            {
                if (!view.Documents.TryGetValue(target.Id, out var destination)) view.Documents[target.Id] = destination = [];
                destination.AddRange(docs.Select(tab => tab with { Preview = false }));
            }
            view.Documents.Remove(id); view.Selected.Remove(id);
            if (view.FocusedCenter == id) view.FocusedCenter = target?.Id ?? "";
            if (view.FocusedGroup == id) view.FocusedGroup = target?.Id ?? view.FocusedCenter;
            if (view.FocusedAuxiliary.GetValueOrDefault(group.Region) == id)
            {
                if (target is null) view.FocusedAuxiliary.Remove(group.Region);
                else view.FocusedAuxiliary[group.Region] = target.Id;
            }
        }
        if (group.Region == "center") state.Center = Remove(state.Center, id)!;
        foreach (var tool in state.ToolRestore.Keys.ToArray())
            if (state.ToolRestore[tool] == id && target is not null) state.ToolRestore[tool] = target.Id;
        state.Groups.Remove(group);
        if (candidates.Length == 0)
        {
            if (group.Region == "left") state.LeftVisible = false;
            if (group.Region == "right") state.RightVisible = false;
            if (group.Region == "bottom") state.BottomVisible = false;
        }
    });

    private static void PrepareAuxiliaryCreation(DockState state, DockGroup created)
    {
        var retained = state.Groups.Where(group => group.Region == created.Region).ToArray();
        var limit = created.Region == "bottom" ? state.BottomLimit : state.SideLimit;
        if (retained.Length >= limit) throw new InvalidOperationException();
        created.Weight = 1d / (retained.Length + 1);
        var total = retained.Sum(group => group.Weight);
        foreach (var group in retained) group.Weight = group.Weight / total * (1 - created.Weight);
    }

    private static CenterNode? Remove(CenterNode node, string id)
    {
        if (node.IsLeaf) return node.GroupId == id ? null : node;
        var first = Remove(node.First!, id); var second = Remove(node.Second!, id);
        if (first is null) return second;
        if (second is null) return first;
        node.First = first; node.Second = second; return node;
    }

    public void Fold(string id) => Change(state =>
    {
        var group = state.Groups.Single(item => item.Id == id);
        if (group.Region != "center") group.Folded = !group.Folded;
    });

    public void Visible(string region, bool visible) => Change(state =>
    {
        if (region == "left") state.LeftVisible = visible;
        if (region == "right") state.RightVisible = visible;
        if (region == "bottom") state.BottomVisible = visible;
    });

    public void RestoreTool(string id, string? target = null) => Change(state =>
    {
        if (state.Groups.Any(group => group.Tools.Any(tab => tab.Id == id))) return;
        target ??= state.ToolRestore.GetValueOrDefault(id);
        var group = state.Groups.FirstOrDefault(item => item.Id == target && item.Region != "center");
        if (group is null)
        {
            group = state.Groups.FirstOrDefault(item => item.Region == ToolRegion(id));
            if (group is null) { group = new() { Region = ToolRegion(id) }; state.Groups.Add(group); }
        }
        var order = ProjectTabs(state, group.Id).ToList();
        order.Insert(Math.Clamp(state.ToolRestorePositions.GetValueOrDefault(id, order.Count), 0, order.Count),
            new DockTab(id, Tool(id)?.Title ?? DockState.Tool(id).Title, "tool"));
        group.Tools = order.Where(tab => tab.IsTool).ToList();
        AnchorResources(Active(state), order);
        group.Folded = false; state.ToolRestore.Remove(id); state.ToolRestorePositions.Remove(id);
        Active(state).Selected[group.Id] = id;
        if (group.Region == "left") state.LeftVisible = true;
        if (group.Region == "right") state.RightVisible = true;
        if (group.Region == "bottom") state.BottomVisible = true;
    });

    public void Geometry(Action<DockState> mutation) => Change(mutation);

    public void ApplyPreset(DockState preset)
    {
        var next = preset.Copy();
        next.ActiveWorkspace = State.ActiveWorkspace; next.Workspaces = State.Workspaces.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
        foreach (var view in next.Workspaces.Values)
        {
            var documents = view.Documents.Values.SelectMany(tabs => tabs).ToArray();
            view.Documents.Clear(); view.Selected.Clear(); view.BeforeToolByTabId.Clear();
            var leaves = next.Center.Leaves().ToArray();
            var centerIndex = 0;
            for (var i = 0; i < documents.Length; i++)
            {
                var target = documents[i].Kind == "terminal" ? next.Groups.FirstOrDefault(group => group.Region == "bottom")?.Id ?? leaves[0]
                    : leaves[centerIndex++ % leaves.Length];
                if (!view.Documents.TryGetValue(target, out var list)) view.Documents[target] = list = [];
                list.Add(documents[i] with { Preview = false });
            }
            view.FocusedCenter = leaves[0]; view.FocusedGroup = leaves[0];
        }
        foreach (var tool in tools.Select(tool => tool.Id))
            if (!next.Groups.Any(group => group.Tools.Any(tab => tab.Id == tool))) next.ToolRestore[tool] = "";
        if (!IsValid(next)) return;
        State = next; Epoch++; Changed?.Invoke();
    }

    public static bool IsValid(DockState state)
    {
        try
        {
            if (!ValidTree(state.Center, 0)) return false;
            var leaves = state.Center.Leaves().ToArray();
            var groups = state.Groups.Select(group => group.Id).ToHashSet();
            var tools = state.Groups.SelectMany(group => group.Tools).ToArray();
            if (state.Schema != 1 || state.ToolRestorePositions is null || groups.Count != state.Groups.Count || leaves.Length is < 1 or > 4 ||
                leaves.Distinct().Count() != leaves.Length || leaves.Any(id => !groups.Contains(id)) ||
                state.Groups.Any(group => group.Region == "center" != leaves.Contains(group.Id)) ||
                state.Groups.Any(group => string.IsNullOrWhiteSpace(group.Id) ||
                    group.Region is not ("center" or "left" or "right" or "bottom") ||
                    !double.IsFinite(group.Weight) || group.Weight <= 0 || group.Region == "center" && group.Folded) ||
                state.Groups.GroupBy(group => group.Region).Any(region => region.Count() > 32) ||
                tools.Any(tab => !tab.IsTool || !DockState.IsToolId(tab.Id)) ||
                tools.Select(tab => tab.Id).Distinct().Count() != tools.Length ||
                state.Groups.Any(group => group.Region == "center" && group.Tools.Count > 0) ||
                state.SideLimit is < 1 or > 32 || state.BottomLimit is < 1 or > 32 ||
                !double.IsFinite(state.LeftWidth + state.RightWidth + state.BottomHeight) ||
                state.LeftWidth is <= 0 or >= 1 || state.RightWidth is <= 0 or >= 1 ||
                state.BottomHeight is <= 0 or > .7 ||
                !new[] { "center", "center-left", "center-right", "full" }.Contains(state.BottomAlignment)) return false;
            return state.Workspaces.Values.All(view =>
            {
                var documents = view.Documents.Values.SelectMany(tabs => tabs).ToArray();
                return view.BeforeToolByTabId is not null && documents.Select(tab => tab.Id).Distinct().Count() == documents.Length &&
                    documents.All(tab => !tab.IsTool) && view.Documents.All(pair =>
                        groups.Contains(pair.Key) && (leaves.Contains(pair.Key) ? pair.Value.Count(tab => tab.Preview) <= 1 :
                            pair.Value.All(tab => tab.Kind == "terminal" && !tab.Preview)));
            });
        }
        catch (Exception error) when (error is NullReferenceException or InvalidOperationException or ArgumentException) { return false; }
    }

    private static bool ValidTree(CenterNode node, int depth) => depth <= 3 &&
        (node.IsLeaf ? node.Second is null && !string.IsNullOrWhiteSpace(node.GroupId) :
            node.Second is not null && node.Axis is "horizontal" or "vertical" &&
            double.IsFinite(node.Ratio) && node.Ratio is > 0 and < 1 &&
            ValidTree(node.First!, depth + 1) && ValidTree(node.Second, depth + 1));
}