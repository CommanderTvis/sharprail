using Avalonia.Controls;
using Avalonia.Media;

namespace SharpRail.Plugins.Api.UI;

/// <summary>
/// The arguments a registered file viewer's control is created with (W7). The viewer follows later changes with
/// <see cref="IPluginUIContext.ObserveFileRevision"/>.
/// </summary>
/// <param name="WorkspaceId">The file's workspace.</param>
/// <param name="Path">The workspace-relative path.</param>
/// <param name="Revision">The file's filesystem revision when the viewer mounted.</param>
/// <param name="Text">The file's text when the manifest's read strategy is text; <see langword="null"/> for none.</param>
public sealed record FileViewerProps(string WorkspaceId, string Path, int Revision, string? Text);

/// <summary>
/// Registers the viewer for files the manifest's <see cref="PluginContributions.FileViewers"/> claim (W7).
/// Viewers are consulted in registration order and the first eligible one wins.
/// </summary>
/// <param name="Create">Creates the viewer's control; a new control per tab.</param>
public sealed record FileViewerRegistration(Func<FileViewerProps, Control> Create)
{
    /// <summary>Narrows eligibility beyond the manifest's extensions and names, for example to one of two viewers sharing an extension.</summary>
    public Func<string, bool>? Matches { get; init; }

    /// <summary>Handles the open outright instead of the open path's normal tab; return <see langword="true"/> to take it over.</summary>
    public Func<string, string, bool>? Open { get; init; }
}

/// <summary>Registers a section in Settings (W4).</summary>
/// <param name="Label">Shown in the section list.</param>
/// <param name="Icon">A Remix Icon name or <c>asset:&lt;path&gt;</c>.</param>
/// <param name="Create">Creates the section's content each time it is shown.</param>
public sealed record SettingsSectionRegistration(string Label, string Icon, Func<Control> Create)
{
    /// <summary>The section's id within the plugin; defaults to the plugin's id.</summary>
    public string? Id { get; init; }
}

/// <summary>Registers the control for a side tool the manifest declares (W5).</summary>
/// <param name="Tool">The tool name, matching an entry in <see cref="PluginContributions.SideTools"/>.</param>
/// <param name="Create">Creates the tool's content for a workspace id.</param>
public sealed record SideToolRegistration(string Tool, Func<string, Control> Create)
{
    /// <summary>Decides per workspace whether the tool is the rail's default selection; consulted off the UI thread.</summary>
    public Func<string, CancellationToken, ValueTask<bool>>? RailDefault { get; init; }

    /// <summary>
    /// Reuses an existing control for another workspace, so one instance serves every workspace of the tool. On true the control shows the new
    /// workspace's state and none of the old one's: work still in flight for the old workspace is cancelled or its results rejected by a
    /// generation or workspace check, unsaved drafts of the old workspace are dropped and never written into the new one, and
    /// subscriptions stay registered once (a retarget re-registers only what depends on the workspace). Return false without changing the
    /// control to require a separate instance, for example when the new workspace has another data scope the control cannot swap to.
    /// Called on the UI thread: read in-memory state and start background loads, never wait.
    /// </summary>
    public Func<Control, string, bool>? Retarget { get; init; }
}

/// <summary>Registers an embedded companion pane attachable to a terminal (W6).</summary>
/// <param name="Kind">The companion's kind within the plugin, used with <see cref="IPluginUIContext.FocusCompanion"/>.</param>
/// <param name="Title">The pane's default title.</param>
/// <param name="Icon">A Remix Icon name or <c>asset:&lt;path&gt;</c>.</param>
/// <param name="IsAvailable">Whether the companion is offered for a terminal; re-evaluated on <see cref="IPluginUIContext.Invalidate"/>.</param>
/// <param name="Create">Creates the pane's content for a terminal.</param>
public sealed record CompanionRegistration(string Kind, string Title, string Icon, Func<CompanionHost, bool> IsAvailable, Func<CompanionHost, Control> Create)
{
    /// <summary>A per-instance title, such as a document's heading; <see langword="null"/> falls back to <see cref="Title"/>. Re-evaluated on <see cref="IPluginUIContext.Invalidate"/>.</summary>
    public Func<CompanionHost, string?>? InstanceTitle { get; init; }
}

/// <summary>The icon or adornment a tab decorator returns for one tab (W8).</summary>
public sealed record TabDecoration
{
    /// <summary>Replaces the tab's icon: a Remix Icon name or <c>asset:&lt;path&gt;</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>Creates extra content shown after the tab's title, such as a badge.</summary>
    public Func<Control>? Adornment { get; init; }

    /// <summary>For an agent's terminal: whether the agent is in the middle of a turn, read from in-memory state when the app is asked to quit so it can confirm first. <see langword="null"/> never counts.</summary>
    public Func<bool>? IsWorking { get; init; }
}

/// <summary>One open tab, passed to a tab decorator (W8).</summary>
/// <param name="WorkspaceId">The tab's workspace.</param>
public abstract record TabRef(string WorkspaceId);

/// <summary>A terminal tab.</summary>
/// <param name="WorkspaceId">The tab's workspace.</param>
/// <param name="TabKey">The terminal's tab key.</param>
public sealed record TerminalTabRef(string WorkspaceId, string TabKey) : TabRef(WorkspaceId);

/// <summary>A side tool tab.</summary>
/// <param name="WorkspaceId">The tab's workspace.</param>
/// <param name="Tool">The layout tool id.</param>
public sealed record ToolTabRef(string WorkspaceId, string Tool) : TabRef(WorkspaceId);

/// <summary>A file, external file or diff tab.</summary>
/// <param name="WorkspaceId">The tab's workspace.</param>
/// <param name="Kind">What the tab shows.</param>
/// <param name="Path">The workspace-relative path, or an absolute path for an external file.</param>
public sealed record FileTabRef(string WorkspaceId, EditorKind Kind, string Path) : TabRef(WorkspaceId);

/// <summary>A contribution to a workspace's or a project's start-actions row (W10).</summary>
/// <param name="Id">The action's id within the plugin.</param>
public abstract record WorkspaceActionRegistration(string Id);

/// <summary>
/// A workspace-scoped action, rendered in the workspace's start-actions row beside core's new-terminal button,
/// wherever the window places that row. The control must not assume which.
/// </summary>
/// <param name="Id">The action's id within the plugin.</param>
/// <param name="Create">Creates the control for a workspace id and the centre group id an open from it should target.</param>
public sealed record WorkspaceScopedActionRegistration(string Id, Func<string, string, Control> Create) : WorkspaceActionRegistration(Id);

/// <summary>A project-scoped action, rendered on Project Home and after a project is opened.</summary>
/// <param name="Id">The action's id within the plugin.</param>
/// <param name="Create">Creates the control for a project id.</param>
public sealed record ProjectScopedActionRegistration(string Id, Func<string, Control> Create) : WorkspaceActionRegistration(Id);

/// <summary>What a file action acts on: a file or folder of a workspace and, when lines were chosen in it, their range.</summary>
/// <param name="WorkspaceId">The workspace.</param>
/// <param name="Path">The workspace-relative path.</param>
/// <param name="IsDirectory">Whether the path is a folder.</param>
public sealed record FileActionTarget(string WorkspaceId, string Path, bool IsDirectory)
{
    /// <summary>The one-based first chosen line, or <see langword="null"/> when the action is for the whole file.</summary>
    public int? StartLine { get; init; }

    /// <summary>The one-based last chosen line.</summary>
    public int? EndLine { get; init; }
}

/// <summary>
/// An action offered in the context menu of a file wherever core shows one (W21): a row of the Files panel, the code
/// editor and the Markdown preview, the last two with the lines selected there.
/// </summary>
/// <param name="Id">The action's id within the plugin.</param>
/// <param name="Label">Returns the menu label for a target, or <see langword="null"/> when the action is not offered for it; read each time a menu opens.</param>
/// <param name="Run">Runs the action on the UI thread.</param>
public sealed record FileActionRegistration(string Id, Func<FileActionTarget, string?> Label, Action<FileActionTarget> Run)
{
    /// <summary>A Remix Icon name or <c>asset:&lt;path&gt;</c>.</summary>
    public string? Icon { get; init; }
}

/// <summary>
/// An item a plugin adds to a terminal tab's context menu wherever core shows one (W26): the tab strip, the rail and
/// the Projects list, but not a phone's chips. Core reads <paramref name="Label"/> each time the menu opens, from
/// in-memory facts only.
/// </summary>
/// <param name="Id">The item's id within the plugin.</param>
/// <param name="Label">Returns the item's text for a terminal, or <see langword="null"/> to hide it for that one.</param>
/// <param name="Run">Runs the item on the UI thread.</param>
public sealed record TerminalTabActionRegistration(string Id, Func<TerminalTabRef, string?> Label, Action<TerminalTabRef> Run)
{
    /// <summary>A Remix Icon name or <c>asset:&lt;path&gt;</c>.</summary>
    public string? Icon { get; init; }
}

/// <summary>Registers a row shown alongside every terminal (W11).</summary>
/// <param name="Create">Creates the row for one terminal's accessory handle.</param>
public sealed record TerminalAccessoryRegistration(Func<ITerminalAccessoryApi, Control> Create);

/// <summary>
/// An agent an operator can start in a terminal (W9): offered when a workspace is created and wherever core
/// starts an agent terminal. Availability and the model list are re-evaluated on
/// <see cref="IPluginUIContext.Invalidate"/>.
/// </summary>
/// <param name="Id">The launcher's id, stable across sessions.</param>
/// <param name="Label">Shown wherever the launcher is offered.</param>
/// <param name="Icon">A Remix Icon name or <c>asset:&lt;path&gt;</c>.</param>
/// <param name="TerminalCommand">Builds the shell command that starts the agent.</param>
/// <param name="Availability">Reports whether the launcher can be used now.</param>
public sealed record AgentLauncher(string Id, string Label, string Icon, Func<LauncherCommandOptions, string> TerminalCommand, Func<LauncherAvailability> Availability)
{
    /// <summary>Creates the launcher's icon at the supplied size and colour, using the owning plugin's assets; each call returns a new control.</summary>
    public Func<double, IBrush?, Control>? CreateIcon { get; init; }

    /// <summary>The models the launcher offers, read when its picker opens; <see langword="null"/> when it offers no choice.</summary>
    public Func<IReadOnlyList<LauncherModel>>? Models { get; init; }
}

/// <summary>The named places in core's own pages where plugins may contribute a section (W23).</summary>
public static class UiSlots
{
    /// <summary>The bottom of Settings › Code Intelligence; the section is created for the window's current workspace.</summary>
    public const string CodeIntelligenceSettings = "core:code-intelligence-settings";

    /// <summary>The bottom of the workspace's Language servers pane; the section is created for that pane's workspace.</summary>
    public const string WorkspaceLanguages = "core:workspace-languages";
}

/// <summary>The core Settings pages a plugin may open (W23).</summary>
public static class UiPages
{
    /// <summary>Settings › Code Intelligence.</summary>
    public const string CodeIntelligence = "Code Intelligence";

    /// <summary>A plugin's own Settings section (W4), for <see cref="IPluginUIContext.OpenSettings"/>.</summary>
    /// <param name="pluginId">The plugin that registered the section.</param>
    /// <param name="sectionId">The section's <see cref="SettingsSectionRegistration.Id"/>; defaults to the plugin's id.</param>
    /// <returns>The page id.</returns>
    public static string PluginSection(string pluginId, string? sectionId = null) => "plugin:" + pluginId + ":" + (sectionId ?? pluginId);
}

/// <summary>Contributes a section to a named place in one of core's pages (W23).</summary>
/// <param name="Slot">The place, one of the <see cref="UiSlots"/> ids.</param>
/// <param name="Create">Creates the section's control for a workspace id, or <see langword="null"/> when the page has none; a new control each time the section is shown.</param>
public sealed record SlotSectionRegistration(string Slot, Func<string?, Control> Create);

/// <summary>
/// Contributes a small item to the trailing end of every workbench window's header, before the connection status and
/// Settings (W24). The control decides what it shows: it hides itself (<c>IsVisible</c> false) while it has nothing
/// to say, and reads its data from the plugin's channels, never from I/O on the UI thread.
/// </summary>
/// <param name="Create">Creates the item's control; called once per window, a new control each time.</param>
public sealed record HeaderItemRegistration(Func<Control> Create);