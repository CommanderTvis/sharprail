using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

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
        state?.RemoveBottomZone();
        state?.MergeCenter();
        state?.MigrateLegacyTools();
        State = state is not null && IsValid(state) ? state : DockState.Preset("balanced");
    }

    public static readonly IReadOnlyList<DockToolInfo> CoreTools =
    [
        .. DockState.ToolNames.Select(id => new DockToolInfo(id, DockState.Tool(id).Title, id switch
        {
            "projects" => "folderTab",
            "files" => "file",
            "lsp" => "code",
            _ => "discuss"
        }, DockState.ToolRegion(id), id is "review"))
    ];
    private IReadOnlyList<DockToolInfo> tools = CoreTools;

    /// <summary>Every tool this layout can name and restore: core's, then the extra ones the workbench composes in.</summary>
    public IReadOnlyList<DockToolInfo> Tools => [.. tools.Where(tool => IsToolAvailable(tool.Id))];
    private bool gitAvailable = true;
    // A plugin's tool is shown only while its plugin offers it: a hidden or unknown one keeps its saved slot but has no tab, strip entry or drawer.
    public bool IsToolAvailable(string id) => tools.FirstOrDefault(tool => tool.Id == id) is { } known
        ? !known.Hidden && (gitAvailable || !known.RequiresGit)
        : !catalogKnown || PluginIdentity.ParseToolId(id) is null;
    // False until the workbench has composed any plugin tool, so a saved plugin tab is not dropped from the layout before the catalog arrives.
    private bool catalogKnown;

    public void SetGitAvailable(bool available)
    {
        if (gitAvailable == available) return;
        gitAvailable = available;
        ToolsChanged?.Invoke();
        Changed?.Invoke();
    }
    public event Action? ToolsChanged;

    /// <summary>Replaces the composed tools after core's; a tab naming a tool absent from the list keeps its slot.</summary>
    public void SetExtraTools(IReadOnlyList<DockToolInfo> extra)
    {
        if (tools.Skip(CoreTools.Count).SequenceEqual(extra)) return;
        tools = [.. CoreTools, .. extra];
        catalogKnown |= extra.Count > 0;
        ToolsChanged?.Invoke();
        Changed?.Invoke();
    }

    public DockToolInfo? Tool(string id) => Tools.FirstOrDefault(tool => tool.Id == id);
    public string ToolTitle(DockTab tab) => tab.IsTool ? Tool(tab.Id)?.Title ?? tab.Title : tab.Title;
    private string ToolRegion(string id) => Tool(id)?.Region ?? DockState.ToolRegion(id);

    public WorkspaceView View => State.Workspaces.GetValueOrDefault(State.ActiveWorkspace) ?? new();
    public DockGroup Group(string id) => State.Groups.Single(group => group.Id == id);
    public IReadOnlyList<DockTab> Tabs(string groupId) => [.. ProjectTabs(State, groupId).Where(tab => !tab.IsTool || IsToolAvailable(tab.Id))];

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

    // committing runs once the change is certain, before it is published, so listeners of Changed already know why.
    private bool Change(Action<DockState> mutation, string? selectionGroup = null, Action? committing = null)
    {
        var candidate = State.Copy();
        try { mutation(candidate); }
        catch (InvalidOperationException) { return false; }
        candidate.SeparatePanes();
        NormalizePanes(candidate);
        RememberProjectPanes(candidate);
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
            if (blocked.Count > 0) { RemovalBlocked?.Invoke(blocked, () => Change(mutation, selectionGroup, committing)); return false; }
        }
        committing?.Invoke();
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

    /// <summary>
    /// Drops the views of workspaces missing from the host's registry (removed by another client or while the app was closed, which
    /// <see cref="DropWorkspace"/> never saw). The active workspace stays, as does everything while the registry is still empty.
    /// </summary>
    public void PruneWorkspaces(IReadOnlySet<string> live)
    {
        if (live.Count == 0) return;
        var stale = State.Workspaces.Keys.Where(path => path != State.ActiveWorkspace && !live.Contains(path)).ToArray();
        if (stale.Length == 0) return;
        var next = State.Copy();
        foreach (var path in stale) next.Workspaces.Remove(path);
        State = next; Epoch++;
        Changed?.Invoke();
    }

    public void SwitchWorkspace(string path, string? project = null) => Change(state =>
    {
        var previous = Active(state);
        var sameProject = project is null || state.ActiveProject == project;
        state.ActiveWorkspace = path;
        var view = Active(state);
        if (project is not null) SwitchProjectPanes(state, project, previous);
        foreach (var group in state.Groups.Where(group => group.Region != "center"))
        {
            var selected = previous.Selected.GetValueOrDefault(group.Id) ?? group.Tools.FirstOrDefault()?.Id;
            if (sameProject && selected is not null && group.Tools.Any(tab => tab.Id == selected)) view.Selected[group.Id] = selected;
        }
        if (!state.Center.Leaves().Contains(view.FocusedCenter)) view.FocusedCenter = state.Center.Leaves().First();
    });

    /// <summary>
    /// Makes terminal tabs follow the host catalog, placing new tabs in the last-focused center group
    /// without taking selection or focus. Saved agent titles override catalog titles.
    /// </summary>
    public bool ReconcileTerminals(string workspace, IReadOnlyList<TerminalTab> catalog, IReadOnlySet<string> pending, IReadOnlyDictionary<string, string>? titles = null)
    {
        if (!State.Workspaces.ContainsKey(workspace)) return false;
        var next = State.Copy();
        var view = next.Workspaces[workspace];
        var changed = !view.TerminalsShared;
        view.TerminalsShared = true;
        var known = catalog.ToDictionary(tab => tab.Key, tab => titles?.GetValueOrDefault(tab.Key) ?? tab.Title);
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
        var leaves = next.Center.Leaves().ToArray();
        var focused = leaves.Contains(view.FocusedCenter) ? view.FocusedCenter : leaves[0];
        var target = focused;
        foreach (var group in next.Groups.Where(group => group.Region switch { "left" => !next.LeftVisible, "right" => !next.RightVisible, _ => false }))
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
            tabs.Add(new DockTab(tab.Key, known[tab.Key], "terminal")); changed = true;
        }
        if (!changed) return false;
        next.SeparatePanes();
        NormalizePanes(next);
        if (!IsValid(next)) return false;
        State = next; Epoch++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves what a group opens on without navigating or taking focus.</summary>
    public void Reseat(string groupId, string tabId)
    {
        if (IsToolAvailable(tabId)) Change(state => Active(state).Selected[groupId] = tabId, groupId);
    }

    public void Select(string groupId, string tabId)
    {
        if (!IsToolAvailable(tabId)) return;
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
        if (tab.IsTool && !IsToolAvailable(tab.Id)) return;
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

    /// <summary>
    /// Points a workspace's document tab at its file's new path after a rename, keeping its id, place, selection and
    /// preview state; the title becomes the new file name.
    /// </summary>
    public void Retarget(string workspace, string tabId, string path)
    {
        bool Stale(DockTab tab) => tab.Id == tabId && tab.Path != path;
        if (State.Workspaces.GetValueOrDefault(workspace)?.Documents.Values.Any(tabs => tabs.Any(Stale)) != true) return;
        Change(state =>
        {
            foreach (var tabs in state.Workspaces[workspace].Documents.Values)
                for (var index = 0; index < tabs.Count; index++)
                    if (Stale(tabs[index])) tabs[index] = tabs[index] with { Path = path, Title = System.IO.Path.GetFileName(path) };
        });
    }

    public void Keep(string groupId, string tabId) => Change(state =>
    {
        var docs = Active(state).Documents.GetValueOrDefault(groupId);
        var index = docs?.FindIndex(tab => tab.Id == tabId) ?? -1;
        if (index >= 0) docs![index] = docs[index] with { Preview = false };
    });

    private readonly Dictionary<(string Workspace, string Tab), string> terminalSources = [];

    /// <summary>The terminal tab a new terminal was opened from, once; null when it was opened from none.</summary>
    public string? TakeTerminalSource(string workspace, string tabId) => terminalSources.Remove((workspace, tabId), out var source) ? source : null;

    /// <summary>Adds a terminal to a group; from a tool pane it creates a tool-free pane directly below and places it there.</summary>
    public void NewTerminal(string groupId, string? id = null) => Change(state =>
    {
        var group = state.Groups.Single(item => item.Id == groupId);
        var view = Active(state);
        if (group.Region != "center" && group.Tools.Count > 0)
        {
            var below = new DockGroup { Region = group.Region };
            PrepareAuxiliaryCreation(state, below);
            state.Groups.Insert(state.Groups.IndexOf(group) + 1, below);
            group = below; groupId = below.Id;
        }
        // The terminal it is opened from: the one its group shows, else the focused one (Ghostty's tab- and
        // split-inherit-working-directory follow the previously focused terminal).
        string? Terminal(string group) => view.Selected.GetValueOrDefault(group) is { } shown &&
            view.Documents.GetValueOrDefault(group)?.Any(tab => tab.Id == shown && tab.Kind == "terminal") == true ? shown : null;
        var source = Terminal(groupId) ?? Terminal(view.FocusedGroup);
        if (!view.Documents.TryGetValue(groupId, out var tabs)) view.Documents[groupId] = tabs = [];
        var tab = new DockTab(id ?? "terminal:" + Guid.NewGuid().ToString("N"), "Terminal " + view.NextTerminalNumber++, "terminal");
        if (source is not null) terminalSources[(state.ActiveWorkspace, tab.Id)] = source;
        tabs.Add(tab); group.Folded = false;
        view.Selected[groupId] = tab.Id; view.FocusedGroup = groupId;
        if (group.Region == "center") view.FocusedCenter = groupId;
        else view.FocusedAuxiliary[group.Region] = groupId;
        if (group.Region == "left") state.LeftVisible = true;
        if (group.Region == "right") state.RightVisible = true;
    });

    public void Close(string groupId, string tabId)
    {
        Navigating?.Invoke(groupId);
        var closed = Describe(groupId, tabId);
        Change(state => CloseIn(state, groupId, tabId), committing: closed is null ? null : () => TabClosing?.Invoke(closed));
    }

    private static void CloseIn(DockState state, string groupId, string tabId)
    {
        var group = state.Groups.Single(item => item.Id == groupId);
        var view = Active(state);
        var before = ProjectTabs(state, groupId).ToList();
        var position = before.FindIndex(tab => tab.Id == tabId);
        var selected = view.Selected.GetValueOrDefault(groupId) ?? before.FirstOrDefault()?.Id;
        if (group.Tools.Any(tab => tab.Id == tabId))
        {
            var restorePosition = position;
            if (state.DefaultProjectPanes is not null)
                foreach (var closed in state.ToolRestore.Where(pair => pair.Value == groupId && pair.Key != tabId)
                    .Select(pair => state.ToolRestorePositions.GetValueOrDefault(pair.Key)).Order())
                    if (closed <= restorePosition) restorePosition++;
            state.ToolRestorePositions[tabId] = restorePosition;
            group.Tools.RemoveAll(tab => tab.Id == tabId); state.ToolRestore[tabId] = groupId;
            AnchorResources(view, before.Where(tab => tab.Id != tabId).ToArray());
        }
        else view.Documents.GetValueOrDefault(groupId)?.RemoveAll(tab => tab.Id == tabId);
        view.BeforeToolByTabId.Remove(tabId);
        if (selected != tabId) return;
        var remaining = before.Where(tab => tab.Id != tabId).ToArray();
        if (remaining.Length == 0) view.Selected.Remove(groupId);
        else view.Selected[groupId] = remaining[Math.Clamp(position, 0, remaining.Length - 1)].Id;
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
        var visible = group.Region switch { "left" => State.LeftVisible, "right" => State.RightVisible, _ => true };
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
            if (region is not ("left" or "right")) throw new InvalidOperationException();
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
        if (region is not ("left" or "right")) throw new InvalidOperationException();
        var view = Active(state);
        var tab = ProjectTabs(state, source).FirstOrDefault(item => item.Id == tabId) ?? throw new InvalidOperationException();
        var group = state.Groups.LastOrDefault(item => item.Region == region);
        if (group is not null && state.CanJoin(view, group, tab))
        {
            MoveIn(state, tabId, source, group.Id, 0, "after"); return;
        }
        if (group is null || !state.CanJoin(view, group, tab))
        {
            group = new() { Region = region }; PrepareAuxiliaryCreation(state, group); state.Groups.Add(group);
        }
        MoveIn(state, tabId, source, group.Id, ProjectTabs(state, group.Id).Length, "");
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
            // The main column holds one group; tabs there combine only as tab groups (panes).
            if (to.Region == "center") throw new InvalidOperationException();
            var created = new DockGroup { Region = to.Region };
            PrepareAuxiliaryCreation(state, created);
            var offset = edge is "before" or "left" or "top" ? 0 : 1;
            state.Groups.Insert(state.Groups.IndexOf(to) + offset, created);
            to = created; destination = created.Id; index = 0;
        }
        // Tools and terminals never share a pane.
        if (!state.CanJoin(view, to, tab)) throw new InvalidOperationException();
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

    public bool NewGroup(string target, string edge) => Change(state =>
    {
        var prior = state.Groups.Single(group => group.Id == target);
        if (prior.Region == "center") throw new InvalidOperationException();
        var next = new DockGroup { Region = prior.Region };
        PrepareAuxiliaryCreation(state, next);
        state.Groups.Insert(state.Groups.IndexOf(prior) + (edge == "before" ? 0 : 1), next);
    });

    public bool RemoveGroup(string id) => Change(state => RemoveGroupIn(state, id));

    private static void RemoveGroupIn(DockState state, string id)
    {
        var group = state.Groups.Single(item => item.Id == id);
        var candidates = state.Groups.Where(item => item.Region == group.Region && item.Id != id).ToArray();
        if (candidates.Length == 0 && (group.Region == "center" || group.Tools.Count > 0)) throw new InvalidOperationException();
        var regional = state.Groups.Where(item => item.Region == group.Region).ToArray();
        var position = Array.IndexOf(regional, group);
        var leaves = state.Center.Leaves().ToArray();
        var centerPosition = Array.IndexOf(leaves, id);
        // An auxiliary pane merges into the nearest sibling (previous, then next) that can take all its tabs.
        bool Takes(DockGroup sibling) => (group.Tools.Count == 0 || state.Workspaces.Values.All(view => (view.Documents.GetValueOrDefault(sibling.Id)?.Count ?? 0) == 0)) &&
            (sibling.Tools.Count == 0 || state.Workspaces.Values.All(view => (view.Documents.GetValueOrDefault(id)?.Count ?? 0) == 0));
        var target = group.Region == "center" ? state.Groups.Single(item => item.Id == leaves[centerPosition > 0 ? centerPosition - 1 : 1]) :
            new[] { position - 1, position + 1 }.Where(index => index >= 0 && index < regional.Length).Select(index => regional[index]).FirstOrDefault(Takes);
        if (target is null && candidates.Length > 0) throw new InvalidOperationException();
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
        }
    }

    private static void PrepareAuxiliaryCreation(DockState state, DockGroup created)
    {
        var retained = state.Groups.Where(group => group.Region == created.Region).ToArray();
        var limit = state.SideLimit;
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

    /// <summary>Whether <see cref="RemoveGroup"/> would succeed.</summary>
    public bool CanRemoveGroup(string id) => Try(state => RemoveGroupIn(state, id));

    private bool Try(Action<DockState> mutation)
    {
        var candidate = State.Copy();
        try { mutation(candidate); candidate.SeparatePanes(); return IsValid(candidate); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Hides an auxiliary pane: it folds, keeping its weight, and its rail entries restore it.</summary>
    public void HidePane(string id) => Change(state =>
    {
        var group = state.Groups.Single(item => item.Id == id);
        if (group.Region == "center" || group.Folded) throw new InvalidOperationException();
        group.Folded = true;
    });

    /// <summary>
    /// Activates a rail entry of a group: <paramref name="tabId"/> (a tool), or the group itself for a terminal or empty
    /// pane. The entry already shown folds its pane; any other reveals the region, unfolds the pane and selects it, in one
    /// transition. Returns whether the pane folded.
    /// </summary>
    public bool ActivateEntry(string groupId, string? tabId)
    {
        var group = Group(groupId);
        if (group.Region == "center") return false;
        var shown = RegionVisible(group.Region) && !group.Folded;
        if (shown && (tabId is null || Selected(groupId)?.Id == tabId))
        {
            HidePane(groupId);
            return true;
        }
        // Within a pane already shown this is an ordinary selection, which swaps only that pane's body.
        if (shown && tabId is not null) { Select(groupId, tabId); return false; }
        Navigating?.Invoke(groupId);
        Change(state =>
        {
            var target = state.Groups.Single(item => item.Id == groupId);
            var view = Active(state);
            target.Folded = false;
            Reveal(state, target.Region);
            if (tabId is not null) view.Selected[groupId] = tabId;
            view.FocusedGroup = groupId; view.FocusedAuxiliary[target.Region] = groupId;
        });
        return false;
    }

    public bool RegionVisible(string region) => region switch
    {
        "left" => State.LeftVisible,
        "right" => State.RightVisible,
        _ => true
    };

    private static void Reveal(DockState state, string region)
    {
        if (region == "left") state.LeftVisible = true;
        if (region == "right") state.RightVisible = true;
    }

    /// <summary>Whether <see cref="MoveToNewGroupAt"/> would succeed.</summary>
    public bool CanMoveToNewGroupAt(string tabId, string source, string region, int boundary) =>
        Try(state => MoveToNewGroupAtIn(state, tabId, source, region, boundary));

    /// <summary>
    /// Moves a tab into a new pane created at a cluster boundary of a region's rail: before its
    /// <paramref name="boundary"/>-th group, or after the last. Moving a pane's only tab next to itself is refused.
    /// </summary>
    public bool MoveToNewGroupAt(string tabId, string source, string region, int boundary) =>
        Change(state => MoveToNewGroupAtIn(state, tabId, source, region, boundary));

    private static void MoveToNewGroupAtIn(DockState state, string tabId, string source, string region, int boundary)
    {
        if (region is not ("left" or "right")) throw new InvalidOperationException();
        var regional = state.Groups.Where(group => group.Region == region).ToList();
        if (boundary < 0 || boundary > regional.Count) throw new InvalidOperationException();
        var from = state.Groups.Single(group => group.Id == source);
        var at = regional.IndexOf(from);
        if (at >= 0 && ProjectTabs(state, source).Length == 1 && (boundary == at || boundary == at + 1)) throw new InvalidOperationException();
        var created = new DockGroup { Region = region };
        PrepareAuxiliaryCreation(state, created);
        state.Groups.Insert(boundary < regional.Count ? state.Groups.IndexOf(regional[boundary]) :
            regional.Count > 0 ? state.Groups.IndexOf(regional[^1]) + 1 : state.Groups.Count, created);
        MoveIn(state, tabId, source, created.Id, 0, "");
    }

    /// <summary>Shows or hides Project Home's Projects pane, which keeps its own visibility and width.</summary>
    public void HomeProjects(bool visible) => Change(state => state.HomeProjectsVisible = visible);

    public void Fold(string id) => Change(state =>
    {
        var group = state.Groups.Single(item => item.Id == id);
        if (group.Region != "center") group.Folded = !group.Folded;
    });

    public void Visible(string region, bool visible) => Change(state =>
    {
        if (region == "left") state.LeftVisible = visible;
        if (region == "right") state.RightVisible = visible;
    });

    /// <summary>Shows one side region, or none, and hides the other in a single change.</summary>
    public void ShowSide(string? region) => Change(state =>
    {
        state.LeftVisible = region == "left";
        state.RightVisible = region == "right";
    });

    public void RestoreTool(string id, string? target = null) => Change(state =>
    {
        if (IsToolAvailable(id)) RestoreToolIn(state, id, target);
    });

    private void RestoreToolIn(DockState state, string id, string? target = null)
    {
        if (state.Groups.Any(group => group.Tools.Any(tab => tab.Id == id))) return;
        target ??= state.ToolRestore.GetValueOrDefault(id);
        var view = Active(state);
        var tool = DockState.Tool(id);
        var group = state.Groups.FirstOrDefault(item => item.Id == target && item.Region != "center");
        if (group is not null && !state.CanJoin(view, group, tool))
        {
            // The recorded pane shows terminals now: the tool gets a pane of its own beside it, when the limit allows.
            var beside = new DockGroup { Region = group.Region };
            try
            {
                PrepareAuxiliaryCreation(state, beside);
                state.Groups.Insert(state.Groups.IndexOf(group) + 1, beside);
                group = beside;
            }
            catch (InvalidOperationException) { }
        }
        if (group is null)
        {
            group = state.Groups.FirstOrDefault(item => item.Region == ToolRegion(id) && state.CanJoin(view, item, tool))
                ?? state.Groups.FirstOrDefault(item => item.Region == ToolRegion(id));
            if (group is null) { group = new() { Region = ToolRegion(id) }; state.Groups.Add(group); }
        }
        var order = ProjectTabs(state, group.Id).ToList();
        var position = state.ToolRestorePositions.GetValueOrDefault(id, order.Count);
        if (state.DefaultProjectPanes is not null)
            position -= state.ToolRestore.Count(pair => pair.Value == group.Id && pair.Key != id &&
                state.ToolRestorePositions.GetValueOrDefault(pair.Key) < position);
        order.Insert(Math.Clamp(position, 0, order.Count),
            new DockTab(id, Tool(id)?.Title ?? DockState.Tool(id).Title, "tool"));
        group.Tools = order.Where(tab => tab.IsTool).ToList();
        AnchorResources(Active(state), order);
        group.Folded = false; state.ToolRestore.Remove(id); state.ToolRestorePositions.Remove(id);
        Active(state).Selected[group.Id] = id;
        if (group.Region == "left") state.LeftVisible = true;
        if (group.Region == "right") state.RightVisible = true;
    }

    public void Geometry(Action<DockState> mutation) => Change(mutation);

    public void ApplyPreset(DockState preset)
    {
        var next = preset.Copy();
        next.RemoveBottomZone();
        next.MergeCenter();
        next.ActiveProject = State.ActiveProject;
        next.DefaultProjectPanes = CaptureProjectPanes(next);
        next.ProjectPanes = State.ProjectPanes.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
        next.ActiveWorkspace = State.ActiveWorkspace; next.Workspaces = State.Workspaces.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
        foreach (var view in next.Workspaces.Values)
        {
            var documents = view.Documents.Values.SelectMany(tabs => tabs).ToArray();
            view.Documents.Clear(); view.Selected.Clear(); view.BeforeToolByTabId.Clear();
            var leaves = next.Center.Leaves().ToArray();
            var centerIndex = 0;
            for (var i = 0; i < documents.Length; i++)
            {
                var target = leaves[centerIndex++ % leaves.Length];
                if (!view.Documents.TryGetValue(target, out var list)) view.Documents[target] = list = [];
                list.Add(documents[i] with { Preview = false });
            }
            view.FocusedCenter = leaves[0]; view.FocusedGroup = leaves[0];
        }
        foreach (var tool in tools.Select(tool => tool.Id))
            if (!next.Groups.Any(group => group.Tools.Any(tab => tab.Id == tool))) next.ToolRestore[tool] = "";
        next.SeparatePanes();
        RememberProjectPanes(next);
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
            if (state.ActiveProject is null || state.ProjectPanes is null ||
                state.ProjectPanes.Any(pair => pair.Value is null || !ValidProjectPanes(pair.Value)) ||
                state.DefaultProjectPanes is { } defaults && !ValidProjectPanes(defaults)) return false;
            if (state.Schema != 1 || state.ToolRestorePositions is null || groups.Count != state.Groups.Count || leaves.Length != 1 ||
                leaves.Distinct().Count() != leaves.Length || leaves.Any(id => !groups.Contains(id)) ||
                state.Groups.Any(group => group.Region == "center" != leaves.Contains(group.Id)) ||
                state.Groups.Any(group => string.IsNullOrWhiteSpace(group.Id) ||
                    group.Region is not ("center" or "left" or "right") ||
                    !double.IsFinite(group.Weight) || group.Weight <= 0 || group.Region == "center" && group.Folded) ||
                state.Groups.GroupBy(group => group.Region).Any(region => region.Count() > 32) ||
                tools.Any(tab => !tab.IsTool || !DockState.IsToolId(tab.Id)) ||
                tools.Select(tab => tab.Id).Distinct().Count() != tools.Length ||
                state.Groups.Any(group => group.Region == "center" && group.Tools.Count > 0) ||
                state.SideLimit is < 1 or > 32 ||
                !double.IsFinite(state.LeftWidth + state.RightWidth) ||
                state.LeftWidth is <= 0 or >= 1 || state.RightWidth is <= 0 or >= 1 || state.HomeProjectsWidth is <= 0 or >= 1) return false;
            return state.Workspaces.Values.All(view =>
            {
                var documents = view.Documents.Values.SelectMany(tabs => tabs).ToArray();
                // Tools and terminals never share a pane.
                if (state.Groups.Any(group => group.Region != "center" && group.Tools.Count > 0 && (view.Documents.GetValueOrDefault(group.Id)?.Count ?? 0) > 0))
                    return false;
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