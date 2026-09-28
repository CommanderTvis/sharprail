namespace SharpRail.UI.Docking;

public sealed class LayoutSession
{
    public DockState State { get; private set; }
    public long Epoch { get; private set; }
    public event Action? Changed;
    public event Action<string>? SelectionChanged;
    public event Action<string>? Navigating;
    public event Action? Focused;
    public LayoutSession(DockState? state = null)
    {
        State = state is not null && IsValid(state) ? state : DockState.Preset("balanced");
    }

    public WorkspaceView View => State.Workspaces.GetValueOrDefault(State.ActiveWorkspace) ?? new();
    public DockGroup Group(string id) => State.Groups.Single(group => group.Id == id);
    public IReadOnlyList<DockTab> Tabs(string groupId) => Group(groupId).Tools.Concat(
        View.Documents.GetValueOrDefault(groupId) ?? []).ToArray();
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
        if (!IsValid(candidate)) return false;
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

    public void SwitchWorkspace(string path) => Change(state =>
    {
        state.ActiveWorkspace = path;
        var view = Active(state);
        if (!state.Center.Leaves().Contains(view.FocusedCenter)) view.FocusedCenter = state.Center.Leaves().First();
    });

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
            var preview = documents.FindIndex(item => item.Preview);
            if ((!keep || claimPreview) && preview >= 0) documents[preview] = tab with { Preview = !keep };
            else documents.Add(tab with { Preview = !keep });
            if (activate || !view.Selected.ContainsKey(target)) view.Selected[target] = tab.Id;
            if (activate) { view.FocusedCenter = target; view.FocusedGroup = target; }
        });
    }

    public void Keep(string groupId, string tabId) => Change(state =>
    {
        var docs = Active(state).Documents.GetValueOrDefault(groupId);
        var index = docs?.FindIndex(tab => tab.Id == tabId) ?? -1;
        if (index >= 0) docs![index] = docs[index] with { Preview = false };
    });

    public void Close(string groupId, string tabId)
    {
        Navigating?.Invoke(groupId);
        Change(state =>
        {
            var group = state.Groups.Single(item => item.Id == groupId);
            var view = Active(state);
            var before = group.Tools.Concat(view.Documents.GetValueOrDefault(groupId) ?? []).ToList();
            var position = before.FindIndex(tab => tab.Id == tabId);
            var selected = view.Selected.GetValueOrDefault(groupId) ?? before.FirstOrDefault()?.Id;
            if (group.Tools.Any(tab => tab.Id == tabId))
            {
                group.Tools.RemoveAll(tab => tab.Id == tabId); state.ToolRestore[tabId] = groupId;
            }
            else view.Documents.GetValueOrDefault(groupId)?.RemoveAll(tab => tab.Id == tabId);
            if (selected != tabId) return;
            var remaining = before.Where(tab => tab.Id != tabId).ToArray();
            if (remaining.Length == 0) view.Selected.Remove(groupId);
            else view.Selected[groupId] = remaining[Math.Clamp(position, 0, remaining.Length - 1)].Id;
        });
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
        if (tab.IsTool == (to.Region == "center")) throw new InvalidOperationException();
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
            var prior = from.Tools.IndexOf(tab);
            if (source == destination && index > prior) index--;
            if (source == destination && index == prior) throw new InvalidOperationException();
            from.Tools.Remove(tab); to.Tools.Insert(Math.Clamp(index, 0, to.Tools.Count), tab);
            state.ToolRestore.Remove(id);
        }
        else
        {
            var documents = view.Documents[source];
            var prior = documents.IndexOf(tab);
            if (source == destination && index > prior) index--;
            if (source == destination && index == prior) throw new InvalidOperationException();
            documents.Remove(tab);
            if (!view.Documents.TryGetValue(destination, out var target)) view.Documents[destination] = target = [];
            target.Insert(Math.Clamp(index, 0, target.Count), tab with { Preview = false });
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
            if (view.FocusedGroup == id) view.FocusedGroup = target?.Id ?? "";
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
            group = state.Groups.FirstOrDefault(item => item.Region == (id == "projects" ? "left" : "right"));
            if (group is null) { group = new() { Region = id == "projects" ? "left" : "right" }; state.Groups.Add(group); }
        }
        group.Tools.Add(DockState.Tool(id)); group.Folded = false; state.ToolRestore.Remove(id);
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
            view.Documents.Clear(); view.Selected.Clear();
            var leaves = next.Center.Leaves().ToArray();
            for (var i = 0; i < documents.Length; i++)
            {
                var target = leaves[i % leaves.Length];
                if (!view.Documents.TryGetValue(target, out var list)) view.Documents[target] = list = [];
                list.Add(documents[i] with { Preview = false });
            }
            view.FocusedCenter = leaves[0]; view.FocusedGroup = leaves[0];
        }
        foreach (var tool in DockState.ToolNames)
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
            if (state.Schema != 1 || groups.Count != state.Groups.Count || leaves.Length is < 1 or > 4 ||
                leaves.Distinct().Count() != leaves.Length || leaves.Any(id => !groups.Contains(id)) ||
                state.Groups.Any(group => group.Region == "center" != leaves.Contains(group.Id)) ||
                state.Groups.Any(group => string.IsNullOrWhiteSpace(group.Id) ||
                    group.Region is not ("center" or "left" or "right" or "bottom") ||
                    !double.IsFinite(group.Weight) || group.Weight <= 0 || group.Region == "center" && group.Folded) ||
                state.Groups.GroupBy(group => group.Region).Any(region => region.Count() > 32) ||
                tools.Any(tab => !tab.IsTool || !DockState.ToolNames.Contains(tab.Id)) ||
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
                return documents.Select(tab => tab.Id).Distinct().Count() == documents.Length &&
                    documents.All(tab => !tab.IsTool) && view.Documents.All(pair =>
                        leaves.Contains(pair.Key) && pair.Value.Count(tab => tab.Preview) <= 1);
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
