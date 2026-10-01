using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Plugins;

/// <summary>One registration, tagged with the plugin that made it.</summary>
public sealed record PluginRow<T>(string PluginId, T Value);

/// <summary>A registered file viewer and the read strategy its plugin's manifest declares.</summary>
public sealed record PluginFileViewer(string PluginId, FileViewerRegistration Registration, PluginFileRead Read);

/// <summary>A side tool a roster row or builtin manifest declares, under its layout id.</summary>
public sealed record PluginToolEntry(string Id, string PluginId, string Label, string Icon, PluginToolSide DefaultSide, bool RequiresGit, bool Dormant);

/// <summary>The tables one registry write touched, so a reader refreshes only its own region.</summary>
[Flags]
public enum PluginTables
{
    None = 0,
    Manifests = 1 << 0,
    Roster = 1 << 1,
    Active = 1 << 2,
    SideTools = 1 << 3,
    SettingsSections = 1 << 4,
    Companions = 1 << 5,
    FileViewers = 1 << 6,
    TabDecorators = 1 << 7,
    Launchers = 1 << 8,
    WorkspaceActions = 1 << 9,
    ProjectActions = 1 << 10,
    TerminalAccessories = 1 << 11,
    FileIconSlots = 1 << 12,
    DocumentLinkSlots = 1 << 13,
    Contributions = SideTools | SettingsSections | Companions | FileViewers | TabDecorators | Launchers | WorkspaceActions |
        ProjectActions | TerminalAccessories | FileIconSlots | DocumentLinkSlots,
    // Registrations whose result a predicate decides; Invalidate re-evaluates them.
    Predicates = Companions | TabDecorators | Launchers | FileIconSlots | DocumentLinkSlots
}

/// <summary>
/// The state a plugin's UI half fills in and the workbench reads back: manifests and roster, the active set, and one
/// append-only, plugin-tagged list per contribution kind. A pure leaf: it knows no window, host or loader, so it is
/// checkable with a constructed roster. Every write raises one <see cref="Changed"/> naming the tables it touched,
/// and derived lists are computed in the write, so reading them is a property read.
/// </summary>
public sealed class PluginRegistry
{
    private Dictionary<string, PluginManifest> manifests = [];
    private HashSet<string> active = [];

    public IReadOnlyDictionary<string, PluginManifest> Manifests => manifests;
    public IReadOnlyList<PluginRosterEntry> Roster { get; private set; } = [];
    public IReadOnlySet<string> Active => active;
    public IReadOnlyList<PluginRow<SideToolRegistration>> SideTools { get; private set; } = [];
    public IReadOnlyList<PluginRow<SettingsSectionRegistration>> SettingsSections { get; private set; } = [];
    public IReadOnlyList<PluginRow<CompanionRegistration>> Companions { get; private set; } = [];
    public IReadOnlyList<PluginFileViewer> FileViewers { get; private set; } = [];
    public IReadOnlyList<PluginRow<Func<TabRef, TabDecoration?>>> TabDecorators { get; private set; } = [];
    public IReadOnlyList<PluginRow<AgentLauncher>> Launchers { get; private set; } = [];
    public IReadOnlyList<PluginRow<WorkspaceScopedActionRegistration>> WorkspaceActions { get; private set; } = [];
    public IReadOnlyList<PluginRow<ProjectScopedActionRegistration>> ProjectActions { get; private set; } = [];
    public IReadOnlyList<PluginRow<TerminalAccessoryRegistration>> TerminalAccessories { get; private set; } = [];
    public IReadOnlyList<PluginRow<Func<string, FileIconKind, string?>>> FileIconSlots { get; private set; } = [];
    public IReadOnlyList<PluginRow<Func<string, string, string?>>> DocumentLinkSlots { get; private set; } = [];

    /// <summary>Plugin-declared side tools in roster order, then builtin manifests the roster has not listed yet.</summary>
    public IReadOnlyList<PluginToolEntry> ToolCatalog { get; private set; } = [];
    public IReadOnlyList<AgentLauncher> LauncherList { get; private set; } = [];

    public event Action<PluginTables>? Changed;

    public void RegisterManifest(PluginManifest manifest)
    {
        manifests = new(manifests) { [manifest.Id] = manifest };
        ToolCatalog = BuildToolCatalog();
        Raise(PluginTables.Manifests);
    }

    public void SetRoster(IReadOnlyList<PluginRosterEntry> roster)
    {
        Roster = roster;
        ToolCatalog = BuildToolCatalog();
        Raise(PluginTables.Roster);
    }

    public void SetActive(string pluginId, bool isActive)
    {
        var next = new HashSet<string>(active);
        if (!(isActive ? next.Add(pluginId) : next.Remove(pluginId))) return;
        active = next;
        ToolCatalog = BuildToolCatalog();
        Raise(PluginTables.Active);
    }

    public void AddSideTool(string pluginId, SideToolRegistration value) { SideTools = [.. SideTools, new(pluginId, value)]; Raise(PluginTables.SideTools); }
    public void AddSettingsSection(string pluginId, SettingsSectionRegistration value) { SettingsSections = [.. SettingsSections, new(pluginId, value)]; Raise(PluginTables.SettingsSections); }
    public void AddCompanion(string pluginId, CompanionRegistration value) { Companions = [.. Companions, new(pluginId, value)]; Raise(PluginTables.Companions); }
    public void AddFileViewer(string pluginId, FileViewerRegistration value, PluginFileRead read) { FileViewers = [.. FileViewers, new(pluginId, value, read)]; Raise(PluginTables.FileViewers); }
    public void AddTabDecorator(string pluginId, Func<TabRef, TabDecoration?> value) { TabDecorators = [.. TabDecorators, new(pluginId, value)]; Raise(PluginTables.TabDecorators); }
    public void AddWorkspaceAction(string pluginId, WorkspaceScopedActionRegistration value) { WorkspaceActions = [.. WorkspaceActions, new(pluginId, value)]; Raise(PluginTables.WorkspaceActions); }
    public void AddProjectAction(string pluginId, ProjectScopedActionRegistration value) { ProjectActions = [.. ProjectActions, new(pluginId, value)]; Raise(PluginTables.ProjectActions); }
    public void AddTerminalAccessory(string pluginId, TerminalAccessoryRegistration value) { TerminalAccessories = [.. TerminalAccessories, new(pluginId, value)]; Raise(PluginTables.TerminalAccessories); }
    public void AddFileIconSlot(string pluginId, Func<string, FileIconKind, string?> value) { FileIconSlots = [.. FileIconSlots, new(pluginId, value)]; Raise(PluginTables.FileIconSlots); }
    public void AddDocumentLinkSlot(string pluginId, Func<string, string, string?> value) { DocumentLinkSlots = [.. DocumentLinkSlots, new(pluginId, value)]; Raise(PluginTables.DocumentLinkSlots); }

    public void AddLauncher(string pluginId, AgentLauncher value)
    {
        Launchers = [.. Launchers, new(pluginId, value)];
        LauncherList = [.. Launchers.Select(row => row.Value)];
        Raise(PluginTables.Launchers);
    }

    /// <summary>Drops every row the plugin registered, across every table, and its activation, in one write.</summary>
    public void RemovePlugin(string pluginId)
    {
        var touched = PluginTables.None;
        IReadOnlyList<T> Drop<T>(IReadOnlyList<T> rows, Func<T, string> owner, PluginTables table)
        {
            if (!rows.Any(row => owner(row) == pluginId)) return rows;
            touched |= table;
            return [.. rows.Where(row => owner(row) != pluginId)];
        }
        if (active.Contains(pluginId)) { active = [.. active.Where(id => id != pluginId)]; touched |= PluginTables.Active; }
        SideTools = Drop(SideTools, row => row.PluginId, PluginTables.SideTools);
        SettingsSections = Drop(SettingsSections, row => row.PluginId, PluginTables.SettingsSections);
        Companions = Drop(Companions, row => row.PluginId, PluginTables.Companions);
        FileViewers = Drop(FileViewers, row => row.PluginId, PluginTables.FileViewers);
        TabDecorators = Drop(TabDecorators, row => row.PluginId, PluginTables.TabDecorators);
        Launchers = Drop(Launchers, row => row.PluginId, PluginTables.Launchers);
        WorkspaceActions = Drop(WorkspaceActions, row => row.PluginId, PluginTables.WorkspaceActions);
        ProjectActions = Drop(ProjectActions, row => row.PluginId, PluginTables.ProjectActions);
        TerminalAccessories = Drop(TerminalAccessories, row => row.PluginId, PluginTables.TerminalAccessories);
        FileIconSlots = Drop(FileIconSlots, row => row.PluginId, PluginTables.FileIconSlots);
        DocumentLinkSlots = Drop(DocumentLinkSlots, row => row.PluginId, PluginTables.DocumentLinkSlots);
        if (touched == PluginTables.None) return;
        if (touched.HasFlag(PluginTables.Launchers)) LauncherList = [.. Launchers.Select(row => row.Value)];
        ToolCatalog = BuildToolCatalog();
        Raise(touched);
    }

    /// <summary>Re-evaluates a plugin's predicates: readers of the predicate tables refresh what they render.</summary>
    public void Invalidate(string pluginId) => Raise(PluginTables.Predicates);

    public PluginRosterEntry? Entry(string pluginId) => Roster.FirstOrDefault(entry => entry.Id == pluginId);

    public string Label(string pluginId) => Entry(pluginId)?.Label ?? manifests.GetValueOrDefault(pluginId)?.Label ?? pluginId;

    public PluginToolEntry? Tool(string toolId) => ToolCatalog.FirstOrDefault(entry => entry.Id == toolId);

    /// <summary>The control factory a live side tool registered, for a <c>plugin:&lt;id&gt;:&lt;tool&gt;</c> layout id.</summary>
    public SideToolRegistration? SideTool(string toolId) =>
        PluginIdentity.ParseToolId(toolId) is { } parsed
            ? SideTools.FirstOrDefault(row => row.PluginId == parsed.PluginId && row.Value.Tool == parsed.Tool)?.Value
            : null;

    /// <summary>The first eligible viewer in registration order: its own predicate when it has one, else its manifest's declaration.</summary>
    public PluginFileViewer? FileViewer(string path) =>
        FileViewers.FirstOrDefault(viewer => viewer.Registration.Matches?.Invoke(path) ?? Declares(viewer.PluginId, path));

    /// <summary>Whether any known plugin, active or not, declares a viewer for the path: such a tab needs a viewer, not text.</summary>
    public PluginFileViewerContribution? DeclaredViewer(string path)
    {
        foreach (var contributions in Roster.Select(entry => entry.Contributes).Concat(manifests.Values.Select(manifest => manifest.Contributes)))
            if (contributions.FileViewers.FirstOrDefault(viewer => Claims(viewer, path)) is { } declared) return declared;
        return null;
    }

    public (string PluginId, TabDecoration Decoration)? Decorate(TabRef tab)
    {
        foreach (var row in TabDecorators)
            if (Safely(() => row.Value(tab)) is { } decoration) return (row.PluginId, decoration);
        return null;
    }

    public (string PluginId, string Icon)? FileIcon(string path, FileIconKind kind)
    {
        foreach (var row in FileIconSlots)
            if (Safely(() => row.Value(path, kind)) is { } icon) return (row.PluginId, icon);
        return null;
    }

    public string? DocumentLink(string workspaceId, string target)
    {
        foreach (var row in DocumentLinkSlots)
            if (Safely(() => row.Value(workspaceId, target)) is { } path) return path;
        return null;
    }

    /// <summary>Enabled plugins reachable from <paramref name="id"/> through <c>DependsOn</c>, transitively: a disable cascades to them.</summary>
    public static IReadOnlyList<string> ActiveDependents(string id, IReadOnlyList<PluginRosterEntry> roster)
    {
        var dependents = new List<string>();
        var frontier = new List<string> { id };
        while (frontier.Count > 0)
        {
            var next = new List<string>();
            foreach (var target in frontier)
                foreach (var entry in roster)
                    if (entry.Id != id && !dependents.Contains(entry.Id) && entry.DependsOn.Contains(target)) { dependents.Add(entry.Id); next.Add(entry.Id); }
            frontier = next;
        }
        return [.. dependents.Where(dependent => roster.FirstOrDefault(entry => entry.Id == dependent)?.Status != PluginStatus.Disabled)];
    }

    /// <summary>Disabled dependencies an enable of <paramref name="id"/> must turn on alongside it, transitively.</summary>
    public static IReadOnlyList<string> ToEnable(string id, IReadOnlyList<PluginRosterEntry> roster)
    {
        var byId = roster.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First());
        var seen = new List<string>();
        var stack = new Stack<string>([id]);
        while (stack.TryPop(out var current))
            foreach (var dependency in byId.GetValueOrDefault(current)?.DependsOn ?? [])
                if (!seen.Contains(dependency)) { seen.Add(dependency); stack.Push(dependency); }
        return [.. seen.Where(dependency => byId.GetValueOrDefault(dependency)?.Status == PluginStatus.Disabled)];
    }

    private bool Declares(string pluginId, string path)
    {
        var contributions = Entry(pluginId)?.Contributes ?? manifests.GetValueOrDefault(pluginId)?.Contributes;
        return contributions?.FileViewers.Any(viewer => Claims(viewer, path)) == true;
    }

    private static bool Claims(PluginFileViewerContribution viewer, string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name) is { Length: > 1 } dotted && name.LastIndexOf('.') > 0 ? dotted[1..] : "";
        return viewer.Extensions.Contains(extension) || viewer.Names.Contains(name);
    }

    private IReadOnlyList<PluginToolEntry> BuildToolCatalog()
    {
        var listed = Roster.Select(entry => entry.Id).ToHashSet();
        var fromRoster = Roster.SelectMany(entry => entry.Contributes.SideTools.Select(tool =>
            Tool(entry.Id, tool, entry.Status != PluginStatus.Active || !active.Contains(entry.Id))));
        var fromManifests = manifests.Values.Where(manifest => !listed.Contains(manifest.Id))
            .SelectMany(manifest => manifest.Contributes.SideTools.Select(tool => Tool(manifest.Id, tool, true)));
        return [.. fromRoster.Concat(fromManifests)];

        static PluginToolEntry Tool(string pluginId, PluginSideToolContribution tool, bool dormant) =>
            new(PluginIdentity.ToolId(pluginId, tool.Tool), pluginId, tool.Label, tool.Icon, tool.DefaultSide, tool.RequiresGit, dormant);
    }

    // A predicate that throws is a broken plugin, not a broken workbench: it answers nothing.
    private static T? Safely<T>(Func<T?> predicate) where T : class
    {
        try { return predicate(); }
        catch (Exception error) { Console.Error.WriteLine("Plugin predicate failed: " + error.Message); return null; }
    }

    private void Raise(PluginTables tables) => Changed?.Invoke(tables);
}