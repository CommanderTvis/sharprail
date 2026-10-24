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

/// <summary>
/// A host tool a server needs besides its own executable, such as Node for an npm server or Go to build gopls (H21). The host probes it
/// before a start and in Settings, honours the user's host-wide path for it, and never downloads or installs it.
/// </summary>
/// <param name="Executable">The tool's file name, as <see cref="ILanguageServerInstallation.RequireTool"/> names it.</param>
/// <param name="Name">What people call it, such as <c>Go</c> or <c>a JDK 21 or newer</c>.</param>
/// <param name="InstallHint">One line on how to install it on the host.</param>
/// <param name="InstallOnly">Whether only the managed installation needs it, so an installed server runs without it.</param>
/// <param name="HomeVariable">An environment variable naming the tool's home (such as <c>JAVA_HOME</c>), searched as <c>home/bin</c> and <c>home</c> before PATH.</param>
public sealed record LanguageServerDependency(string Executable, string Name, string InstallHint, bool InstallOnly = false, string HomeVariable = "");

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

    /// <summary>The user-selected installation directory, or empty for the managed <see cref="Root"/>.</summary>
    string InstallationPath { get; }

    /// <summary>A directory for one launch, inside <see cref="WorkspaceState"/>, removed when the session ends.</summary>
    string SessionDirectory { get; }

    /// <summary>The terms the user accepted for this launch, for a server with <see cref="LanguageServerSpec.TermsAsync"/>.</summary>
    LanguageServerTerms? AcceptedTerms { get; }

    /// <summary>An HTTP client for a provider's own downloads; it follows redirects, so the provider checks the final host.</summary>
    HttpClient Http { get; }

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

    /// <summary>The host tools the managed launch or installation needs; an executable override or PATH launch needs none of them.</summary>
    public IReadOnlyList<LanguageServerDependency> Dependencies { get; init; } = [];

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

    /// <summary>
    /// Seeds a newly created workspace (<see cref="ILanguageServerPrime.Target"/>) with the server's per-checkout state, such
    /// as an index, from the checkout it was created from (<see cref="ILanguageServerPrime.Source"/>). The host runs it in the
    /// background before the workspace's servers start, which wait for it (bounded); it must never replace what the
    /// workspace already has, and failures are ignored.
    /// </summary>
    public Func<ILanguageServerPrime, CancellationToken, Task>? PrimeWorkspaceAsync { get; init; }

    /// <summary>A PATH executable to try when none is found, such as one located by a toolchain driver.</summary>
    public Func<ILanguageServerInstallation, CancellationToken, Task<string?>>? FindFallbackAsync { get; init; }

    /// <summary>Whether a PATH executable is usable, such as a TypeScript server that can reach a compiler.</summary>
    public Func<string, string, bool> AcceptsPathExecutable { get; init; } = (_, _) => true;

    /// <summary>The name people see, such as <c>C# (Roslyn)</c>; empty shows <see cref="Id"/>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The server's mark in the user interface, or <see langword="null"/> for a generic one.</summary>
    public LanguageServerIcon? Icon { get; init; }

    /// <summary>The typed workspace options the server accepts.</summary>
    public LanguageServerOptionGroups OptionGroups { get; init; }

    /// <summary>
    /// Servers this one replaces when it is chosen for their languages, host-wide or in a workspace; it then serves
    /// their files and they stay stopped, and while it is not chosen it does not start.
    /// </summary>
    public IReadOnlyList<string> Replaces { get; init; } = [];

    /// <summary>The languages of <see cref="Replaces"/> as people name them, such as <c>Java / Kotlin</c>; empty joins the replaced servers' names.</summary>
    public string ReplacesLabel { get; init; } = "";

    /// <summary>Arguments the provider manages, which configured arguments may not repeat (also as <c>name=value</c>).</summary>
    public IReadOnlyList<string> ReservedArguments { get; init; } = [];

    /// <summary>Whether a host executable override may replace the managed launch.</summary>
    public bool AllowsOverride { get; init; } = true;

    /// <summary>
    /// Reads the installation's terms for review; <c>prepare</c> is whether the provider may download it first. A server
    /// with terms is installed and launched by its provider: the host never searches PATH for it and starts it only once
    /// the exact terms were accepted, passing them in <see cref="ILanguageServerInstallation.AcceptedTerms"/>.
    /// </summary>
    public Func<ILanguageServerInstallation, bool, CancellationToken, Task<LanguageServerTerms>>? TermsAsync { get; init; }

    /// <summary>What the user should know before reviewing the terms, such as an evaluation-only licence and how the vendor ends it.</summary>
    public string TermsNote { get; init; } = "";

    /// <summary>The process exit code with which the vendor refuses this build, or <see langword="null"/>.</summary>
    public int? ExpiredExitCode { get; init; }

    /// <summary>What the host shows once the build expired.</summary>
    public string ExpiredDetail { get; init; } = "The vendor refused this build.";

    /// <summary>Records an expiry so later launches fail fast, such as by removing a managed installation.</summary>
    public Action<ILanguageServerInstallation> OnExpired { get; init; } = _ => { };

    /// <summary>Fills initialization options from the typed options, the manifests and the server's directories, after <see cref="DefaultInitialization"/>.</summary>
    public Action<ILanguageServerStart, JsonObject> ConfigureInitialization { get; init; } = (_, _) => { };

    /// <summary>Additional workspace-relative manifests, such as a compilation database in an excluded directory.</summary>
    public Func<string, LanguageServerWorkspaceOptions, IEnumerable<string>> AdditionalManifests { get; init; } = (_, _) => [];

    /// <summary>Whether a workspace directory is itself a manifest, such as an Xcode project; it is then not searched.</summary>
    public Func<string, bool> IsProjectDirectory { get; init; } = _ => false;

    /// <summary>The absolute workspace folders to announce, given the workspace and its manifests.</summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyList<string>> WorkspaceFolders { get; init; } = (workspace, _) => [workspace];

    /// <summary>A longer timeout for <c>initialize</c>, such as for a server that imports the project first.</summary>
    public TimeSpan? InitializeTimeout { get; init; }

    /// <summary>A work-done token sent with <c>initialize</c>, or <see langword="null"/>.</summary>
    public string? InitializeProgressToken { get; init; }

    /// <summary>Whether, given the initialization options, startup waits for <see cref="ProjectLoadedNotification"/> before serving requests.</summary>
    public Func<JsonObject, bool> LoadsProjects { get; init; } = _ => false;

    /// <summary>The notification that ends <see cref="LoadsProjects"/> loading.</summary>
    public string ProjectLoadedNotification { get; init; } = "";

    /// <summary>
    /// Text in a <c>window/logMessage</c> that says the server finished reloading projects after watched project inputs changed;
    /// empty when the server reports no such end, and project-input changes then never hold requests back.
    /// </summary>
    public string ProjectReloadedMessage { get; init; } = "";

    /// <summary>Whether a changed file, by absolute path, makes the server reload its project graph, such as a project file or restore output.</summary>
    public Func<string, bool> IsProjectInput { get; init; } = _ => false;

    /// <summary>
    /// Extensions, with the dot, of files the server's project files include by glob but that it registers no watcher for, such as
    /// Avalonia <c>.axaml</c>: creating or deleting one is reported as a change of the nearest enclosing project manifest.
    /// </summary>
    public IReadOnlyList<string> ReloadingExtensions { get; init; } = [];

    /// <summary>Handles a server notification or request the host does not, such as a progress or restore message; requests are answered with <c>null</c>.</summary>
    public Action<ILanguageServerSession, string, JsonNode?> OnMessage { get; init; } = (_, _, _) => { };

    /// <summary>URI schemes, with the colon, whose dependency sources <see cref="ReadSourceAsync"/> reads.</summary>
    public IReadOnlyList<string> SourceSchemes { get; init; } = [];

    /// <summary>Reads a dependency source a navigation result named by a URI in <see cref="SourceSchemes"/>.</summary>
    public Func<ILanguageServerSession, string, CancellationToken, Task<LanguageServerSource>>? ReadSourceAsync { get; init; }

    /// <summary>Absolute source roots the server reports, shown on directories; the host keeps only those inside the workspace.</summary>
    public Func<ILanguageServerSession, CancellationToken, Task<IReadOnlyList<string>>>? SourceRootsAsync { get; init; }
}

/// <summary>The typed workspace options a server accepts besides arguments, initialization options and settings (H21).</summary>
[Flags]
public enum LanguageServerOptionGroups
{
    /// <summary>Only the common options.</summary>
    None = 0,

    /// <summary>Include paths, compiler flags and a compilation database directory.</summary>
    CompilerFlags = 1,

    /// <summary>A user-selected installation directory instead of the managed one.</summary>
    Installation = 2,

    /// <summary>A project JDK and a build importer.</summary>
    JvmProject = 4
}

/// <summary>A server's mark in the user interface: SVG (tinted with the text colour) or PNG bytes.</summary>
/// <param name="Data">The image bytes.</param>
/// <param name="Format"><c>svg</c> or <c>png</c>.</param>
public sealed record LanguageServerIcon(byte[] Data, string Format)
{
    /// <summary>Reads an embedded resource of <paramref name="assembly"/>.</summary>
    /// <param name="assembly">The assembly holding the resource.</param>
    /// <param name="name">The resource's logical name; its extension gives the format.</param>
    /// <returns>The icon.</returns>
    public static LanguageServerIcon Embedded(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{assembly.GetName().Name} is built without {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new(buffer.ToArray(), Path.GetExtension(name).TrimStart('.'));
    }
}

/// <summary>The typed workspace options of one server, as saved (paths are not yet expanded).</summary>
/// <param name="IncludePaths">Include paths (<see cref="LanguageServerOptionGroups.CompilerFlags"/>).</param>
/// <param name="CompilerFlags">Compiler flags (<see cref="LanguageServerOptionGroups.CompilerFlags"/>).</param>
/// <param name="CompilationDatabasePath">The compilation database directory (<see cref="LanguageServerOptionGroups.CompilerFlags"/>).</param>
/// <param name="InstallationPath">The user-selected installation (<see cref="LanguageServerOptionGroups.Installation"/>).</param>
/// <param name="ProjectJdk">The project JDK home (<see cref="LanguageServerOptionGroups.JvmProject"/>).</param>
/// <param name="BuildTool">The build importer: <c>maven</c>, <c>gradle</c>, <c>bazel</c>, <c>jps</c> or empty (<see cref="LanguageServerOptionGroups.JvmProject"/>).</param>
/// <param name="InitializationSupplied">The initialization option names the user set.</param>
public sealed record LanguageServerWorkspaceOptions(IReadOnlyList<string> IncludePaths, IReadOnlyList<string> CompilerFlags, string CompilationDatabasePath,
    string InstallationPath, string ProjectJdk, string BuildTool, IReadOnlyCollection<string> InitializationSupplied);

/// <summary>The checkouts and this server's per-workspace state directories that <see cref="LanguageServerSpec.PrimeWorkspaceAsync"/> works with.</summary>
public interface ILanguageServerPrime
{
    /// <summary>The absolute checkout the workspace was created from.</summary>
    string Source { get; }

    /// <summary>The absolute new workspace.</summary>
    string Target { get; }

    /// <summary>The server's per-workspace state directory for <see cref="Source"/> (<see cref="ILanguageServerStart.WorkspaceState"/>); it may not exist.</summary>
    string SourceState { get; }

    /// <summary>The server's per-workspace state directory for <see cref="Target"/>; it may not exist.</summary>
    string TargetState { get; }
}

/// <summary>What the host lends <see cref="LanguageServerSpec.ConfigureInitialization"/> while it builds a session's initialization options.</summary>
public interface ILanguageServerStart
{
    /// <summary>The absolute workspace.</summary>
    string Workspace { get; }

    /// <summary>The server's typed workspace options.</summary>
    LanguageServerWorkspaceOptions Options { get; }

    /// <summary>The project manifests discovery found, workspace-relative with <c>/</c> separators; empty outside a session start.</summary>
    IReadOnlyList<string> Manifests { get; }

    /// <summary>A per-workspace directory for this server's state, outside the workspace; empty outside a session start.</summary>
    string WorkspaceState { get; }

    /// <summary>This server version's own cache directory, shared across workspaces; empty outside a session start.</summary>
    string ServerCache { get; }

    /// <summary>The launch's own directory (<see cref="ILanguageServerInstallation.SessionDirectory"/>), removed when the session ends; empty outside a session start.</summary>
    string SessionDirectory { get; }

    /// <summary>Expands <c>${workspaceFolder}</c> and makes a path absolute against the workspace.</summary>
    /// <param name="value">The saved path.</param>
    /// <returns>The absolute path.</returns>
    string FullPath(string value);

    /// <summary>Expands <c>${workspaceFolder}</c>.</summary>
    /// <param name="value">The saved value.</param>
    /// <returns>The expanded value.</returns>
    string Expand(string value);

    /// <summary>The project JDK: <paramref name="configured"/>, else the workspace <c>.sdkmanrc</c>, else SDKMAN current; throws an <see cref="ArgumentException"/> when none is usable.</summary>
    /// <param name="configured">An explicit JDK home, or empty.</param>
    /// <returns>The JDK home (empty when none was found) and where it came from.</returns>
    (string Path, string Source) ResolveProjectJdk(string configured);

    /// <summary>Adds a line to the session's log.</summary>
    /// <param name="category">A short category.</param>
    /// <param name="text">The line.</param>
    void Log(string category, string text);
}

/// <summary>A running session, lent to the session hooks of <see cref="LanguageServerSpec"/>.</summary>
public interface ILanguageServerSession
{
    /// <summary>The absolute workspace.</summary>
    string Workspace { get; }

    /// <summary>Whether the server advertises <paramref name="command"/> through <c>workspace/executeCommand</c>.</summary>
    /// <param name="command">The command name.</param>
    /// <returns>Whether it is advertised.</returns>
    bool SupportsCommand(string command);

    /// <summary>Sends a request to the server.</summary>
    /// <param name="method">The LSP method.</param>
    /// <param name="parameters">Its parameters, serialized as JSON.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="timeout">A longer timeout than the host default, or <see langword="null"/>.</param>
    /// <returns>The result.</returns>
    Task<JsonNode?> RequestAsync(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null);

    /// <summary>Shows a status while the server works, holding navigation results as partial; <see langword="null"/> clears it.</summary>
    /// <param name="key">The status's key.</param>
    /// <param name="message">The message, or <see langword="null"/>.</param>
    void SetProgress(string key, string? message);

    /// <summary>Runs project work, such as a restore, at most once per key; navigation waits for it and a failure is shown as the server's status.</summary>
    /// <param name="key">The work's key.</param>
    /// <param name="describe">What the work is, for the log and a failure message.</param>
    /// <param name="work">The work, cancelled when the session ends.</param>
    void Track(string key, string describe, Func<CancellationToken, Task> work);

    /// <summary>Whether an absolute path is inside the workspace.</summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether it is.</returns>
    bool Contains(string path);
}

/// <summary>A read-only dependency source a server returned for a non-file URI.</summary>
/// <param name="Text">The source text.</param>
/// <param name="Language">Its LSP language id.</param>
public sealed record LanguageServerSource(string Text, string Language);

/// <summary>A server installation's terms, read for review (H21). The host records acceptance per server and artifact.</summary>
/// <param name="Installation">The installation the terms belong to.</param>
/// <param name="Version">The installation's build.</param>
/// <param name="ArtifactId">A fingerprint of the whole installation.</param>
/// <param name="Text">The terms' text.</param>
/// <param name="TextSha256">The lowercase hex SHA-256 of the terms' bytes.</param>
public sealed record LanguageServerTerms(string Installation, string Version, string ArtifactId, string Text, string TextSha256);

/// <summary>Thrown by a provider whose server build the vendor refused; the host shows <see cref="LanguageServerSpec.ExpiredDetail"/>.</summary>
/// <param name="message">What happened.</param>
public sealed class LanguageServerExpiredException(string message) : IOException(message);

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