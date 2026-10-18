using SharpRail.Plugins.Api;

namespace SharpRail.UI.Docking;

/// <summary>A side tool the layout can name, show and restore: one of core's, or one the workbench composes in.</summary>
public sealed record DockToolInfo(string Id, string Title, string Icon, string Region, bool RequiresGit = false);

public sealed record DockTab(string Id, string Title, string Kind, string Path = "", bool Preview = false, string Scope = "", string Comparison = "")
{
    public bool IsTool => Kind == "tool";
}

public sealed class CenterNode
{
    public string GroupId { get; set; } = "";
    public string Axis { get; set; } = "horizontal";
    public double Ratio { get; set; } = .5;
    public CenterNode? First { get; set; }
    public CenterNode? Second { get; set; }
    public bool IsLeaf => First is null;
    public CenterNode Copy() => new() { GroupId = GroupId, Axis = Axis, Ratio = Ratio, First = First?.Copy(), Second = Second?.Copy() };
    public IEnumerable<string> Leaves() => IsLeaf ? [GroupId] : First!.Leaves().Concat(Second!.Leaves());
}

public sealed class DockGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Region { get; set; } = "center";
    public List<DockTab> Tools { get; set; } = [];
    public bool Folded { get; set; }
    public double Weight { get; set; } = 1;
    public DockGroup Copy() => new() { Id = Id, Region = Region, Tools = [.. Tools], Folded = Folded, Weight = Weight };
}

public sealed class WorkspaceView
{
    public Dictionary<string, List<DockTab>> Documents { get; set; } = [];
    /// <summary>Per centre group, its tabs shown together; workspace state because members are this workspace's tabs.</summary>
    public Dictionary<string, List<DockPane>> Panes { get; set; } = [];
    public Dictionary<string, string> Selected { get; set; } = [];
    public Dictionary<string, string> BeforeToolByTabId { get; set; } = [];
    public int NextTerminalNumber { get; set; } = 1;
    /// <summary>Set once this view's terminals were reconciled with the host's catalog, which decides them from then on.</summary>
    public bool TerminalsShared { get; set; }
    public string FocusedCenter { get; set; } = "";
    public string FocusedGroup { get; set; } = "";
    public Dictionary<string, string> FocusedAuxiliary { get; set; } = [];
    public WorkspaceView Copy() => new()
    {
        Documents = Documents.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
        Panes = Panes.ToDictionary(pair => pair.Key, pair => pair.Value.Select(pane => pane.Copy()).ToList()),
        Selected = new(Selected),
        BeforeToolByTabId = new(BeforeToolByTabId),
        NextTerminalNumber = NextTerminalNumber,
        TerminalsShared = TerminalsShared,
        FocusedCenter = FocusedCenter,
        FocusedGroup = FocusedGroup,
        FocusedAuxiliary = new(FocusedAuxiliary)
    };
}

/// <summary>The tools and visibility one project remembers within the window's shared frame.</summary>
public sealed class ProjectPaneState
{
    public List<string> OpenTools { get; set; } = [];
    public HashSet<string> FoldedGroups { get; set; } = [];
    public Dictionary<string, string> SelectedTools { get; set; } = [];
    public bool LeftVisible { get; set; } = true;
    public bool RightVisible { get; set; } = true;
    public bool HomeProjectsVisible { get; set; } = true;
    public ProjectPaneState Copy() => new()
    {
        OpenTools = [.. OpenTools], FoldedGroups = [.. FoldedGroups], SelectedTools = new(SelectedTools),
        LeftVisible = LeftVisible, RightVisible = RightVisible, HomeProjectsVisible = HomeProjectsVisible
    };
}

public sealed class DockState
{
    public int Schema { get; set; } = 1;
    public string ActiveProject { get; set; } = "";
    public ProjectPaneState? DefaultProjectPanes { get; set; }
    public Dictionary<string, ProjectPaneState> ProjectPanes { get; set; } = [];
    public CenterNode Center { get; set; } = new();
    public List<DockGroup> Groups { get; set; } = [];
    public Dictionary<string, WorkspaceView> Workspaces { get; set; } = [];
    public Dictionary<string, string> ToolRestore { get; set; } = [];
    public Dictionary<string, int> ToolRestorePositions { get; set; } = [];
    public string ActiveWorkspace { get; set; } = "";
    public bool LeftVisible { get; set; } = true;
    public bool RightVisible { get; set; } = true;
    public double LeftWidth { get; set; } = .18;
    public double RightWidth { get; set; } = .28;
    public int SideLimit { get; set; } = 6;
    /// <summary>Project Home's own Projects pane: shown or hidden, and its width, independent of the workspace frame's left side.</summary>
    public bool HomeProjectsVisible { get; set; } = true;
    public double HomeProjectsWidth { get; set; } = .22;
    public DockState Copy() => new()
    {
        ActiveProject = ActiveProject,
        DefaultProjectPanes = DefaultProjectPanes?.Copy(),
        ProjectPanes = ProjectPanes.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()),
        Center = Center.Copy(),
        Groups = Groups.Select(group => group.Copy()).ToList(),
        Workspaces = Workspaces.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()),
        ToolRestore = new(ToolRestore),
        ToolRestorePositions = new(ToolRestorePositions),
        ActiveWorkspace = ActiveWorkspace,
        LeftVisible = LeftVisible,
        RightVisible = RightVisible,
        LeftWidth = LeftWidth,
        RightWidth = RightWidth,
        SideLimit = SideLimit,
        HomeProjectsVisible = HomeProjectsVisible,
        HomeProjectsWidth = HomeProjectsWidth
    };

    public static readonly string[] ToolNames = ["projects", "files", "changes", "review", "lsp"];
    /// <summary>The spec dialect plugin's Specs tool, which the presets seat first in the right rail.</summary>
    public const string SpecsTool = "plugin:spec-dialect:specs";
    /// <summary>Core tool ids that moved into a plugin, and the plugin tool id a persisted layout reads them as.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyToolIds = new Dictionary<string, string> { ["specs"] = SpecsTool };
    public static DockTab Tool(string id)
    {
        var name = PluginIdentity.ParseToolId(id)?.Tool ?? id;
        return new(id, id == "lsp" ? "Code Intelligence" : char.ToUpperInvariant(name[0]) + name[1..], "tool");
    }
    public static string ToolRegion(string id) => id == "projects" ? "left" : "right";
    /// <summary>Core's tools and any <c>plugin:&lt;id&gt;:&lt;tool&gt;</c>, so a tab from a plugin since removed keeps its slot.</summary>
    public static bool IsToolId(string id) => ToolNames.Contains(id) || PluginIdentity.ParseToolId(id) is not null;

    /// <summary>
    /// Renames tool ids that moved out of core wherever a persisted layout names them, and separates the mixed
    /// tool and terminal panes older layouts allowed (<see cref="SeparatePanes"/>); a malformed layout is left to validation.
    /// </summary>
    public void MigrateLegacyTools()
    {
        try { SeparatePanes(); }
        catch (Exception error) when (error is NullReferenceException or InvalidOperationException or ArgumentException) { }
        static string Rename(string id) => LegacyToolIds.GetValueOrDefault(id) ?? id;
        try
        {
            foreach (var group in Groups)
                for (var index = 0; index < group.Tools.Count; index++)
                    if (group.Tools[index] is { IsTool: true } tab && LegacyToolIds.TryGetValue(tab.Id, out var renamed))
                        group.Tools[index] = Tool(renamed);
            foreach (var (legacy, renamed) in LegacyToolIds)
            {
                if (ToolRestore.Remove(legacy, out var group)) ToolRestore[renamed] = group;
                if (ToolRestorePositions.Remove(legacy, out var position)) ToolRestorePositions[renamed] = position;
            }
            foreach (var panes in ProjectPanes.Values.Concat(DefaultProjectPanes is { } defaults ? [defaults] : []))
            {
                panes.OpenTools = panes.OpenTools.Select(Rename).Distinct().ToList();
                foreach (var key in panes.SelectedTools.Keys.ToArray()) panes.SelectedTools[key] = Rename(panes.SelectedTools[key]);
            }
            foreach (var view in Workspaces.Values)
                foreach (var map in new[] { view.Selected, view.BeforeToolByTabId })
                    foreach (var (key, value) in map.ToArray())
                        map[key] = Rename(value);
        }
        catch (NullReferenceException) { }
    }

    /// <summary>Moves document tabs into the centre before discarding the retired bottom zone and its terminal placements.</summary>
    public void RemoveBottomZone()
    {
        if (Groups is null || Workspaces is null) return;
        var removed = Groups.Where(group => group is not null && group.Region == "bottom").Select(group => group.Id).ToList();
        Groups.RemoveAll(group => group is not null && removed.Contains(group.Id));
        var centers = Center?.Leaves().Where(id => Groups.Any(group => group?.Id == id && group.Region == "center")).ToList() ?? [];
        foreach (var view in Workspaces.Values)
        {
            if (view?.Documents is null || view.Selected is null || view.Panes is null || view.FocusedAuxiliary is null) continue;
            var target = centers.Contains(view.FocusedCenter) ? view.FocusedCenter : centers.FirstOrDefault();
            var present = view.Documents.Where(pair => !removed.Contains(pair.Key)).SelectMany(pair => pair.Value).Select(tab => tab.Id).ToHashSet();
            foreach (var id in removed)
            {
                if (view.Documents.Remove(id, out var tabs))
                {
                    var migrated = new HashSet<string>();
                    foreach (var tab in tabs)
                    {
                        view.BeforeToolByTabId?.Remove(tab.Id);
                        if (target is null || tab.IsTool || tab.Kind == "terminal" || !present.Add(tab.Id)) continue;
                        if (!view.Documents.TryGetValue(target, out var documents)) view.Documents[target] = documents = [];
                        documents.Add(tab);
                        migrated.Add(tab.Id);
                    }
                    if (target is not null && migrated.Count > 0)
                    {
                        var documents = view.Documents[target];
                        if (!documents.Any(tab => tab.Id == view.Selected.GetValueOrDefault(target)))
                            view.Selected[target] = migrated.Contains(view.Selected.GetValueOrDefault(id) ?? "") ? view.Selected[id] : documents[0].Id;
                        foreach (var pane in view.Panes.GetValueOrDefault(id) ?? [])
                        {
                            var members = pane.TabIds.Where(migrated.Contains).Distinct().ToList();
                            if (members.Count < DockPane.MinMembers) continue;
                            if (!view.Panes.TryGetValue(target, out var panes)) view.Panes[target] = panes = [];
                            var copy = pane.Copy();
                            copy.TabIds = members;
                            if (members.Count != pane.TabIds.Count) copy.Weights = DockPane.Even(members.Count);
                            panes.Add(copy);
                        }
                    }
                }
                view.Selected.Remove(id);
                view.Panes.Remove(id);
                if (view.FocusedGroup == id) view.FocusedGroup = target ?? "";
            }
            view.FocusedAuxiliary.Remove("bottom");
        }
        foreach (var tool in ToolRestore.Where(pair => removed.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
        {
            ToolRestore.Remove(tool);
            ToolRestorePositions.Remove(tool);
        }
    }

    /// <summary>Whether an auxiliary group may hold <paramref name="tab"/>: tools and terminals never share a pane.</summary>
    public bool CanJoin(WorkspaceView view, DockGroup group, DockTab tab) => group.Region == "center" || (tab.IsTool
        ? (view.Documents.GetValueOrDefault(group.Id)?.Count ?? 0) == 0
        : group.Tools.Count == 0);

    /// <summary>
    /// Where a terminal goes that may not stay near <paramref name="near"/>: the same region's nearest tool-free
    /// pane, then the last-focused centre group.
    /// </summary>
    public string TerminalTarget(WorkspaceView view, DockGroup? near)
    {
        if (near is not null && near.Region != "center")
        {
            var regional = Groups.Where(group => group.Region == near.Region).ToList();
            var origin = regional.IndexOf(near);
            var sibling = regional.Where(group => group.Id != near.Id && group.Tools.Count == 0)
                .OrderBy(group => Math.Abs(regional.IndexOf(group) - origin)).ThenBy(group => regional.IndexOf(group)).FirstOrDefault();
            if (sibling is not null) return sibling.Id;
        }
        var leaves = Center.Leaves().ToArray();
        return leaves.Contains(view.FocusedCenter) ? view.FocusedCenter : leaves[0];
    }

    /// <summary>
    /// Moves every workspace view's terminals out of tool panes to <see cref="TerminalTarget"/>, keeping their order,
    /// selecting nothing and unfolding nothing, and discards the mixed-order anchors that only a mixed pane needed.
    /// </summary>
    public void SeparatePanes()
    {
        foreach (var view in Workspaces.Values)
        {
            view.BeforeToolByTabId.Clear();
            foreach (var group in Groups.Where(group => group.Region != "center" && group.Tools.Count > 0))
            {
                if (!view.Documents.TryGetValue(group.Id, out var tabs)) continue;
                view.Documents.Remove(group.Id);
                if (tabs.Count == 0) continue;
                var target = TerminalTarget(view, group);
                if (!view.Documents.TryGetValue(target, out var destination)) view.Documents[target] = destination = [];
                destination.AddRange(tabs.Select(tab => tab with { Preview = false }));
                if (!group.Tools.Any(tool => tool.Id == view.Selected.GetValueOrDefault(group.Id))) view.Selected.Remove(group.Id);
            }
        }
    }

    public static DockState Preset(string name)
    {
        var center = new DockGroup();
        var left = new DockGroup { Region = "left", Tools = [Tool("projects")] };
        var top = new DockGroup { Region = "right", Tools = [Tool(SpecsTool), Tool("files")], Weight = 1.25 };
        var lower = new DockGroup { Region = "right", Tools = [Tool("changes"), Tool("review")] };
        var state = new DockState { Center = new() { GroupId = center.Id }, Groups = [center, left, top, lower] };
        if (name == "focus") state.LeftVisible = state.RightVisible = false;
        if (name == "review")
        {
            var secondary = new DockGroup();
            state.Groups.Add(secondary);
            state.Center = new() { Axis = "vertical", First = new() { GroupId = center.Id }, Second = new() { GroupId = secondary.Id } };
            top.Tools = [Tool("changes"), Tool("review")]; top.Weight = 1.4;
            lower.Tools = [Tool(SpecsTool), Tool("files")];
            state.LeftWidth = .16; state.RightWidth = .32;
        }
        return state;
    }
}