namespace SharpRail.Plugins.Api;

/// <summary>
/// The static description of a plugin: identity, versioning, its two entry assemblies, and what it declares
/// before any of its code runs. A builtin plugin constructs this record in code; an external plugin ships the
/// same fields as <see cref="FileName"/> in its directory, read with <see cref="PluginJson.Options"/>.
/// <see cref="Contributes"/> exists because the file-open dispatcher and the layout need a contribution to be
/// known before the plugin's code runs; the control or finer predicate behind it is registered by the UI half
/// during activation.
/// </summary>
/// <param name="Id">The plugin's namespace for every method, channel, route, tool id and settings key. For an external plugin it must equal its directory name.</param>
/// <param name="Label">Shown in the roster, Settings › Plugins and a dormant tool placeholder.</param>
/// <param name="Icon">A Remix Icon name (for example <c>puzzle-2-line</c>) or <c>asset:&lt;path&gt;</c> naming an SVG under <see cref="Assets"/>; an unknown name falls back to a generic glyph.</param>
/// <param name="Version">The plugin's own version, informational only.</param>
/// <param name="ApiGeneration">The <see cref="PluginApi.Generation"/> this plugin was built against; a mismatch is refused before load.</param>
/// <param name="WireVersion">Incremented whenever the plugin's methods or channels change, so a separately shipped half can detect drift.</param>
public sealed record PluginManifest(string Id, string Label, string Icon, string Version, int ApiGeneration, int WireVersion)
{
    /// <summary>The manifest file an external plugin ships at the root of its directory.</summary>
    public const string FileName = "sharprail-plugin.json";

    /// <summary>One sentence on what the plugin does, shown under its label in Settings.</summary>
    public string? Description { get; init; }

    /// <summary>Honoured for a builtin plugin; an external plugin always arrives disabled regardless of this value.</summary>
    public bool EnabledByDefault { get; init; }

    /// <summary>Other plugins this one is built on, by id and exact wire version; see <see cref="PluginDependency"/>.</summary>
    public IReadOnlyList<PluginDependency> DependsOn { get; init; } = [];

    /// <summary>
    /// The host half's assembly, relative to the plugin directory, holding exactly one public concrete
    /// subclass of <c>PluginHostModule</c>. Absent when the plugin has no host half. A builtin plugin's host
    /// half is referenced by the host instead, and this field is informational.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    /// The UI half's assembly, relative to the plugin directory, holding exactly one public concrete subclass
    /// of <c>PluginUIModule</c>. Absent when the plugin has no UI half. A builtin plugin's UI half is
    /// referenced by the app instead, and this field is informational.
    /// </summary>
    public string? Ui { get; init; }

    /// <summary>
    /// A directory, relative to the plugin directory, whose files the host serves to the UI half through the
    /// plugin file read and hands to the host half as its assets directory.
    /// </summary>
    public string? Assets { get; init; }

    /// <summary>The statically known parts of what the plugin contributes: side tools and file viewers.</summary>
    public PluginContributions Contributes { get; init; } = new();

    /// <summary>
    /// Agent resources the ThinkRail manifest format declares for pi sessions. SharpRail has no in-process
    /// agent, so the block is parsed for compatibility and otherwise ignored.
    /// </summary>
    public PluginPiBlock? Pi { get; init; }
}

/// <summary>
/// Declares that a plugin is built on another plugin, by id and exact wire version. A plugin whose dependency
/// is absent, disabled, failed, refused or at a different wire version is refused, naming the dependency.
/// Activation runs dependency-first and disposal dependent-first; a cycle is refused at load. At runtime a
/// dependency grants calls to its declared methods and subscriptions to its channels, and nothing more.
/// </summary>
/// <param name="Id">The id of the plugin depended on.</param>
/// <param name="WireVersion">The exact wire version the dependency must be running.</param>
public sealed record PluginDependency(string Id, int WireVersion);

/// <summary>The ThinkRail manifest's pi block, read so a shared manifest parses; SharpRail does not act on it.</summary>
public sealed record PluginPiBlock
{
    /// <summary>Extension entries for pi's resource loader.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Skill directories for pi's resource loader.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>Whether the extensions and skills also reach delegated sub-agent sessions.</summary>
    public bool ReachesSubagents { get; init; }

    /// <summary>Whether an extension appends to the agent's system prompt.</summary>
    public bool ModifiesSystemPrompt { get; init; }
}

/// <summary>The statically known parts of a plugin, declared in its manifest before any of its code runs.</summary>
public sealed record PluginContributions
{
    /// <summary>Side tools rendered under <c>plugin:&lt;id&gt;:&lt;tool&gt;</c> layout ids.</summary>
    public IReadOnlyList<PluginSideToolContribution> SideTools { get; init; } = [];

    /// <summary>File viewers, consulted in registration order when a file opens.</summary>
    public IReadOnlyList<PluginFileViewerContribution> FileViewers { get; init; } = [];
}

/// <summary>The rail side a plugin side tool is placed on when it is first revealed.</summary>
public enum PluginToolSide
{
    /// <summary>The left rail.</summary>
    Left,

    /// <summary>The right rail.</summary>
    Right
}

/// <summary>One side tool a manifest declares, rendered under a <c>plugin:&lt;id&gt;:&lt;tool&gt;</c> layout id.</summary>
/// <param name="Tool">The tool name, unique within the plugin.</param>
/// <param name="Label">The tab title.</param>
/// <param name="Icon">A Remix Icon name or <c>asset:&lt;path&gt;</c>, resolved like <see cref="PluginManifest.Icon"/>.</param>
/// <param name="DefaultSide">The rail the tool is placed on when first revealed.</param>
public sealed record PluginSideToolContribution(string Tool, string Label, string Icon, PluginToolSide DefaultSide)
{
    /// <summary>Withheld, like Changes and Review, in a workspace whose folder has no Git history to read.</summary>
    public bool RequiresGit { get; init; }
}

/// <summary>How the file-open path reads a file before a viewer renders it.</summary>
public enum PluginFileRead
{
    /// <summary>The file is read as text, as an ordinary file tab is.</summary>
    Text,

    /// <summary>Nothing is read; the viewer fetches what it needs itself.</summary>
    None
}

/// <summary>One file viewer a manifest declares: the extensions and names it claims, and how the file is read.</summary>
public sealed record PluginFileViewerContribution
{
    /// <summary>Claimed extensions without the dot, compared case-insensitively.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Claimed exact file names, such as <c>BLUEPRINT.md</c>.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>The read strategy the open path uses before the viewer mounts.</summary>
    public PluginFileRead Read { get; init; } = PluginFileRead.Text;
}