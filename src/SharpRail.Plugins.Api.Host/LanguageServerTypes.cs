using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Api.Host;

/// <summary>How completely a language server's managed installation is pinned (H21).</summary>
public enum LanguageServerPinning
{
    /// <summary>Exact version and a SHA-256 per platform archive in <see cref="LanguageServerSpec.Artifacts"/>, verified before extraction.</summary>
    Archive,

    /// <summary>The whole npm graph pinned by an embedded <c>package-lock.json</c>, installed with <c>npm ci</c> and install scripts disabled.</summary>
    Lockfile,

    /// <summary>Exact version and a SHA-512 per NuGet package in <see cref="LanguageServerSpec.NuGetPackages"/>, verified before a local-feed install.</summary>
    NuGet,

    /// <summary>Exact version and embedded <c>go.mod</c>/<c>go.sum</c> covering the whole module graph, built read-only.</summary>
    GoModule,

    /// <summary>Exact version; integrity is left to the package manager.</summary>
    Partial,

    /// <summary>Nothing is installed: the server comes from the host's toolchain.</summary>
    None
}

/// <summary>One downloadable platform archive of a managed server.</summary>
/// <param name="Platform"><c>macos-aarch64</c>, <c>macos-x86_64</c>, <c>linux-aarch64</c>, <c>linux-x86_64</c>, <c>windows-aarch64</c>, <c>windows-x86_64</c>, or <c>any</c>.</param>
/// <param name="Url">The HTTPS download URL.</param>
/// <param name="Sha256">The pinned lowercase hex SHA-256 of the archive.</param>
/// <param name="Format"><c>zip</c>, <c>tar.gz</c> or <c>gz</c> (a single gzipped executable).</param>
/// <param name="Source">Where the hash was obtained.</param>
/// <param name="Hosts">The HTTPS hosts the download and its redirects may reach.</param>
public sealed record LanguageServerArtifact(string Platform, string Url, string Sha256, string Format, string Source, IReadOnlyList<string> Hosts);

/// <summary>One NuGet package of a managed .NET tool with its pinned SHA-512 (lowercase hex of the catalog <c>packageHash</c>).</summary>
/// <param name="Id">The package id.</param>
/// <param name="Version">The exact package version.</param>
/// <param name="Platform">The platform it serves, or <c>any</c>.</param>
/// <param name="Sha512">The pinned SHA-512.</param>
public sealed record LanguageServerNuGetPackage(string Id, string Version, string Platform, string Sha512);

/// <summary>One named configuration control shown for the server in the language configuration view.</summary>
/// <param name="Label">The control's label.</param>
/// <param name="Section"><c>settings</c> (workspace/configuration) or <c>initializationOptions</c>.</param>
/// <param name="Path">The dotted path of the value inside the section.</param>
/// <param name="Type"><c>string</c>, <c>integer</c>, <c>boolean</c>, <c>lines</c> or <c>features</c>.</param>
/// <param name="Choices">Fixed choices, or <see langword="null"/>.</param>
/// <param name="Help">One line of help.</param>
/// <param name="PathKind"><c>file</c>, <c>folder</c>, <c>fileOrFolder</c>, or empty when the value is not a path.</param>
/// <param name="AllowCustomChoices">Whether a value outside <paramref name="Choices"/> may be typed.</param>
/// <param name="Minimum">The smallest allowed integer, or <see langword="null"/>.</param>
public sealed record LanguageSettingField(string Label, string Section, string Path, string Type = "string", IReadOnlyList<string>? Choices = null,
    string Help = "", string PathKind = "", bool AllowCustomChoices = false, decimal? Minimum = null)
{
    /// <summary>A control in the <c>settings</c> section.</summary>
    /// <param name="label">The control's label.</param>
    /// <param name="path">The value's dotted path.</param>
    /// <param name="type">The value's type.</param>
    /// <param name="choices">Fixed choices.</param>
    /// <param name="help">One line of help.</param>
    /// <param name="pathKind">What kind of path the value is.</param>
    /// <param name="allowCustomChoices">Whether other values may be typed.</param>
    /// <param name="minimum">The smallest allowed integer.</param>
    /// <returns>The control.</returns>
    public static LanguageSettingField Setting(string label, string path, string type = "string", IReadOnlyList<string>? choices = null, string help = "",
        string pathKind = "", bool allowCustomChoices = false, decimal? minimum = null) =>
        new(label, "settings", path, type, choices, help, pathKind, allowCustomChoices, minimum);

    /// <summary>A control in the <c>initializationOptions</c> section.</summary>
    /// <param name="label">The control's label.</param>
    /// <param name="path">The value's dotted path.</param>
    /// <param name="type">The value's type.</param>
    /// <param name="choices">Fixed choices.</param>
    /// <param name="help">One line of help.</param>
    /// <param name="pathKind">What kind of path the value is.</param>
    /// <param name="allowCustomChoices">Whether other values may be typed.</param>
    /// <param name="minimum">The smallest allowed integer.</param>
    /// <returns>The control.</returns>
    public static LanguageSettingField Init(string label, string path, string type = "string", IReadOnlyList<string>? choices = null, string help = "",
        string pathKind = "", bool allowCustomChoices = false, decimal? minimum = null) =>
        new(label, "initializationOptions", path, type, choices, help, pathKind, allowCustomChoices, minimum);
}

/// <summary>The process a provider starts for one workspace.</summary>
/// <param name="Executable">The absolute executable path.</param>
/// <param name="Arguments">Its arguments.</param>
public sealed record LanguageServerLaunch(string Executable, IReadOnlyList<string> Arguments)
{
    /// <summary>Environment variables to set; a <see langword="null"/> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// What the host lends a provider while it installs or launches its server (H21). Every tool lookup uses the host's
/// hardened PATH search, which never returns an executable inside the workspace, and every process runs without a
/// shell, with output captured and the call's cancellation honoured.
/// </summary>
public interface ILanguageServerInstallation
{
    /// <summary>The server's managed installation directory for its pinned version on this platform.</summary>
    string Root { get; }

    /// <summary>The absolute workspace the server is started for.</summary>
    string Workspace { get; }

    /// <summary>A per-workspace directory for this server's state, outside the workspace.</summary>
    string WorkspaceState { get; }

    /// <summary>A cache directory shared by every installation of every server.</summary>
    string SharedCache { get; }

    /// <summary>This server version's own cache directory, shared across workspaces.</summary>
    string ServerCache { get; }

    /// <summary>The host platform, in the form <see cref="LanguageServerArtifact.Platform"/> uses.</summary>
    string Platform { get; }

    /// <summary>Finds a tool on the hardened PATH.</summary>
    /// <param name="executable">The tool's file name.</param>
    /// <returns>Its absolute path, or <see langword="null"/>.</returns>
    string? FindTool(string executable);

    /// <summary>Finds a tool on the hardened PATH, or throws an <see cref="IOException"/> asking the user to install it.</summary>
    /// <param name="executable">The tool's file name.</param>
    /// <returns>Its absolute path.</returns>
    string RequireTool(string executable);

    /// <summary>Finds an executable inside <see cref="Root"/>, marking it executable, or throws an <see cref="IOException"/> asking for a clean reinstall.</summary>
    /// <param name="name">Its file name, without <c>.exe</c>.</param>
    /// <returns>Its absolute path.</returns>
    string FindInstalled(string name);

    /// <summary>Finds the .NET host: <c>DOTNET_ROOT</c> first, then the hardened PATH.</summary>
    /// <returns>Its absolute path, or <see langword="null"/>.</returns>
    string? FindDotnet();

    /// <summary>Runs a process to completion and throws an <see cref="IOException"/> carrying its error output when it fails.</summary>
    /// <param name="executable">The absolute executable path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="workingDirectory">Its working directory.</param>
    /// <param name="environment">Variables to set; a <see langword="null"/> value removes one.</param>
    /// <param name="cancellationToken">Kills the process tree when cancelled.</param>
    /// <returns>Its standard output.</returns>
    Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default);

    /// <summary>Downloads one NuGet package into <paramref name="feed"/>, keeping it only when its SHA-512 matches.</summary>
    /// <param name="package">The pinned package.</param>
    /// <param name="feed">The local feed directory.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>A task that completes when the package is verified.</returns>
    Task FetchNuGetAsync(LanguageServerNuGetPackage package, string feed, CancellationToken cancellationToken = default);
}

/// <summary>
/// A language server a plugin provides (H21). The host owns everything generic: the session and LSP protocol, the
/// installation lock and readiness marker, archive download and verification, npm lockfile installs, executable
/// overrides, the PATH policy and per-server settings. The provider declares its identity, version, files and pinning
/// and supplies only what differs: how to install, and how to launch.
/// </summary>
public sealed class LanguageServerSpec
{
    /// <summary>The catalog id, such as <c>typescript</c>; settings and <c>.sharprail/language.toml</c> are keyed by it.</summary>
    public required string Id { get; init; }

    /// <summary>The exact pinned version, or <c>toolchain</c> for an SDK-bundled server.</summary>
    public required string Version { get; init; }

    /// <summary>The executable's name: on PATH, inside the installation, or in an npm package's <c>bin</c>.</summary>
    public required string Executable { get; init; }

    /// <summary>The default arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>File extensions the server owns, with the leading dot and exact case.</summary>
    public required IReadOnlyList<string> Extensions { get; init; }

    /// <summary>Exact file names the server owns besides <see cref="Extensions"/>, such as <c>Gemfile</c>.</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];

    /// <summary>Other names a caller may use for the server, such as <c>js</c> for <c>typescript</c>.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The implementation and its authors, as <c>Name · Author</c>.</summary>
    public required string Implementation { get; init; }

    /// <summary>Where the installation comes from, such as <c>RubyGems · ruby-lsp</c>; npm servers default to their packages.</summary>
    public string Source { get; init; } = "";

    /// <summary>The implementation's website.</summary>
    public string Website { get; init; } = "";

    /// <summary>The implementation's repository.</summary>
    public string Repository { get; init; } = "";

    /// <summary>What <see cref="Repository"/> is.</summary>
    public string RepositoryLabel { get; init; } = "Source repository";

    /// <summary>How the managed installation is pinned.</summary>
    public required LanguageServerPinning Pinning { get; init; }

    /// <summary>One honest line on that pinning, shown in Settings; empty uses the host's wording for <see cref="Pinning"/>.</summary>
    public string PinningDescription { get; init; } = "";

    /// <summary>Whether the executable is the server itself, so allowing PATH for it is meaningful.</summary>
    public bool PathCapable { get; init; } = true;

    /// <summary>Whether the process is a general runtime whose first arguments choose the program, so configured arguments never replace them.</summary>
    public bool HostsRuntime { get; init; }

    /// <summary>The npm packages with exact versions, space separated, for <see cref="LanguageServerPinning.Lockfile"/>; the first names the server.</summary>
    public string NpmPackages { get; init; } = "";

    /// <summary>Embedded install inputs by file name: <c>package.json</c> and <c>package-lock.json</c> for npm, <c>go.mod</c> and <c>go.sum</c> for Go.</summary>
    public IReadOnlyDictionary<string, string> LockFiles { get; init; } = new Dictionary<string, string>();

    /// <summary>The pinned archives for <see cref="LanguageServerPinning.Archive"/>.</summary>
    public IReadOnlyList<LanguageServerArtifact> Artifacts { get; init; } = [];

    /// <summary>The pinned packages for <see cref="LanguageServerPinning.NuGet"/>: the tool for <c>any</c>, then each platform's runtime pack.</summary>
    public IReadOnlyList<LanguageServerNuGetPackage> NuGetPackages { get; init; } = [];

    /// <summary>The configuration controls the server's pinned version understands.</summary>
    public IReadOnlyList<LanguageSettingField> Settings { get; init; } = [];

    /// <summary>Whether a workspace-relative path is a project manifest for this server.</summary>
    public Func<string, bool> IsProjectManifest { get; init; } = _ => false;

    /// <summary>How the server scopes project-wide results, given the manifests found.</summary>
    public Func<IReadOnlyList<string>, string> ProjectScope { get; init; } = _ => "";

    /// <summary>What a manifest <see cref="IsProjectManifest"/> accepted is, such as <c>Maven project</c>, or <see langword="null"/> to leave it out of project listings.</summary>
    public Func<string, string?> ProjectKind { get; init; } = _ => null;

    /// <summary>Fills the server's defaults into initialization options, leaving every value the user set.</summary>
    public Action<JsonObject> DefaultInitialization { get; init; } = _ => { };

    /// <summary>Fills the server's defaults into <c>workspace/configuration</c> settings, leaving every value the user set.</summary>
    public Action<JsonObject> DefaultSettings { get; init; } = _ => { };

    /// <summary>The LSP language id of a path, or <see langword="null"/> for the server's id.</summary>
    public Func<string, string?> LanguageId { get; init; } = _ => null;

    /// <summary>
    /// Installs into <see cref="ILanguageServerInstallation.Root"/> for a <see cref="LanguageServerPinning.NuGet"/>,
    /// <see cref="LanguageServerPinning.GoModule"/> or <see cref="LanguageServerPinning.Partial"/> server. The host holds
    /// the installation lock and writes the readiness marker after it returns. Archive and npm installs are the host's.
    /// </summary>
    public Func<ILanguageServerInstallation, CancellationToken, Task>? InstallAsync { get; init; }

    /// <summary>Whether the installation is complete beyond the host's readiness marker.</summary>
    public Func<string, bool> Installed { get; init; } = _ => true;

    /// <summary>
    /// The managed launch, after installation. Absent, the host runs the npm package's <c>bin</c> with Node, or the
    /// first file named <see cref="InstalledExecutable"/> inside the installation.
    /// </summary>
    public Func<ILanguageServerInstallation, CancellationToken, Task<LanguageServerLaunch>>? LaunchAsync { get; init; }

    /// <summary>The executable's file name inside a managed installation, without <c>.exe</c>; empty uses <see cref="Executable"/>.</summary>
    public string InstalledExecutable { get; init; } = "";

    /// <summary>Adjusts every launch, managed, PATH or override, such as cache variables or a state directory argument.</summary>
    public Action<ILanguageServerInstallation, LanguageServerLaunchEdit> ConfigureLaunch { get; init; } = (_, _) => { };

    /// <summary>A PATH executable to try when none is found, such as one located by a toolchain driver.</summary>
    public Func<ILanguageServerInstallation, CancellationToken, Task<string?>>? FindFallbackAsync { get; init; }

    /// <summary>Whether a PATH executable is usable, such as a TypeScript server that can reach a compiler.</summary>
    public Func<string, string, bool> AcceptsPathExecutable { get; init; } = (_, _) => true;
}

/// <summary>A launch about to start, which <see cref="LanguageServerSpec.ConfigureLaunch"/> may adjust.</summary>
public sealed class LanguageServerLaunchEdit
{
    /// <summary>The arguments, in order.</summary>
    public required IList<string> Arguments { get; init; }

    /// <summary>The environment, after the host's own variables.</summary>
    public required IDictionary<string, string?> Environment { get; init; }
}

/// <summary>
/// The host half of a plugin whose only contribution is one language server (H21): its contract has no methods,
/// channels or settings, and activation provides <see cref="Spec"/>.
/// </summary>
/// <param name="pluginId">The plugin's id, which its manifest must share.</param>
public abstract class LanguageServerPlugin(string pluginId) : PluginHostModule
{
    /// <summary>The server this plugin provides.</summary>
    public abstract LanguageServerSpec Spec { get; }

    /// <inheritdoc/>
    public override PluginContract Contract { get; } = PluginContract.Create(pluginId, 1, [], []);

    /// <inheritdoc/>
    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.LanguageServer(Spec);
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}
