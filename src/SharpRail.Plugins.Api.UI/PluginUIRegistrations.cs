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