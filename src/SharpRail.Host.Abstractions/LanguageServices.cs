namespace SharpRail.Host.Abstractions;

/// <summary>A workspace-explicit language request. Positions are zero-based UTF-16; null Text reads disk. Origin is empty for a person using the editor or panes, "config" for a configured autostart and "agent-tool" for an agent's MCP tool call.</summary>
public sealed record LanguageRequest(string Workspace, string Path = "", string Operation = "inspect", int Line = 0,
    int Column = 0, string? Text = null, string DocumentId = "", long Version = 0, string Query = "", bool Dirty = false, string Server = "", string Scope = "", IReadOnlyList<string>? Paths = null, int Offset = 0, int Limit = 200, string? Configuration = null, string Origin = "");

/// <summary>A location, diagnostic or semantic token. Positions are zero-based UTF-16.</summary>
public sealed record LanguageItem(string Path, int Line, int Column, int EndLine, int EndColumn,
    string Text = "", string Kind = "", string Source = "", string Preview = "", string Body = "", bool Truncated = false)
{
    /// <summary>Space-separated semantic-token modifier names from the server legend.</summary>
    public string Modifiers { get; init; } = "";

    /// <summary>The server's container name for a workspace symbol, such as its file and project or its containing type.</summary>
    public string Container { get; init; } = "";
}

/// <summary>A supported language server in one workspace; listing never starts a process.</summary>
public sealed record LanguageServerState(string Id, string Path, string State, string Detail, string Version, string Configuration = "", string ActiveConfiguration = "", bool Configured = false)
{
    /// <summary>How the most recent run of this server ended, when the host knows; it outlives the process and host restarts.</summary>
    public LanguageExit? Exit { get; init; }

    /// <summary>Who started the running server: "human", "config", "workspace creation" or "agent"; informational, it never changes how long the server runs.</summary>
    public string StartedBy { get; init; } = "";

    /// <summary>How agents have used this server through the lsp tools in this workspace; null when none has.</summary>
    public LanguageUsageSummary? Usage { get; init; }

    /// <summary>What the providing plugin says to show for this server.</summary>
    public LanguageServerPresentation? Presentation { get; init; }

    /// <summary>The host tools the server declares, probed without starting it; null when the host did not probe them.</summary>
    public IReadOnlyList<LanguageDependencyState>? Dependencies { get; init; }
}

/// <summary>
/// One host tool a language server needs, as probed on the host: <paramref name="State"/> is <c>found</c>, <c>missing</c> or <c>rejected</c> (an
/// override that cannot run), <paramref name="Source"/> is <c>override</c>, <c>home</c> (an environment variable such as <c>JAVA_HOME</c>),
/// <c>path</c> or empty. <paramref name="Required"/> is false when the server's current launch does not need the tool, such as a build tool once
/// the managed installation exists.
/// </summary>
public sealed record LanguageDependencyState(string Executable, string Name, string State, string Path, string Source, string InstallHint,
    string Problem = "", bool Override = false, bool Required = true);

/// <summary>
/// How a provider asks the user interface to show its server: <paramref name="Name"/>, a mark (<paramref name="Icon"/> bytes in
/// <paramref name="IconFormat"/> <c>svg</c> or <c>png</c>, or none), the typed option groups it accepts (<c>compilerFlags</c>,
/// <c>installation</c>, <c>jvmProject</c>), the servers it <paramref name="Replaces"/> when chosen, and whether it has terms to accept,
/// with <paramref name="TermsNote"/> to read first; <paramref name="ReplacesLabel"/> names the replaced servers' languages.
/// </summary>
public sealed record LanguageServerPresentation(string Name, byte[]? Icon = null, string IconFormat = "", IReadOnlyList<string>? OptionGroups = null,
    IReadOnlyList<string>? Replaces = null, bool HasTerms = false, string TermsNote = "", string ReplacesLabel = "");

/// <summary>Counts of agent tool calls that used one language server in one workspace, with the time of the latest (UTC, round-trip format).</summary>
public sealed record LanguageUsageSummary(int Calls, int Successes, int Failures, string LastUsed);

/// <summary>
/// The host's record of one language-server process ending. <paramref name="Reason"/> is one line of facts and labelled suspicions;
/// <paramref name="Unexpected"/> is false for stops the user or SharpRail's lifecycle asked for. <paramref name="Details"/> is a copyable report with the
/// stderr tail, recent JSON-RPC messages and requests in flight.
/// </summary>
public sealed record LanguageExit(string Time, string Reason, bool Unexpected, string Initiator, int? ExitCode, string Signal, int UptimeSeconds, long MemoryBytes, string Details)
{
    /// <summary>Who started the process that ended: "human", "config", "workspace creation" or "agent".</summary>
    public string StartedBy { get; init; } = "";
}

/// <summary>A detected build root or provider-reported directory role; State identifies provenance without asserting complete import or package ownership.</summary>
public sealed record LanguageProject(string Root, string Manifest, string Server, string Kind, string State, string Detail);

/// <summary>Coverage of one explicitly requested file; skipped and pending files are never reported as checked.</summary>
public sealed record LanguageFileResult(string Path, string State, string Freshness, string Detail);

/// <summary>Server state and results for precisely the requested document version. Freshness is explicit, never inferred from an empty list.</summary>
public sealed record LanguageResult(string State, string Detail = "", string Server = "", long Version = 0)
{
    /// <summary>Whether the initialized provider supports full semantic coloring for this document.</summary>
    public bool SemanticColoring { get; init; }
    public string Configuration { get; init; } = "";
    public string ActiveConfiguration { get; init; } = "";
    public int Total { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<LanguageProject> Projects { get; init; } = [];
    public IReadOnlyList<LanguageFileResult> Files { get; init; } = [];
    public IReadOnlyList<LanguageServerState> Servers { get; init; } = [];
    public IReadOnlyList<LanguageItem> Locations { get; init; } = [];
    public IReadOnlyList<LanguageItem> Diagnostics { get; init; } = [];
    public IReadOnlyList<LanguageItem> Tokens { get; init; } = [];
    public string Freshness { get; init; } = "pending";
    public string Coverage { get; init; } = "open documents";

    /// <summary>One line naming the call that fixes a stopped, disabled or unsupported server the request needed; empty otherwise.</summary>
    public string Hint { get; init; } = "";
}

public partial interface IProjectServices
{
    ValueTask<LanguageResult> LanguageAsync(LanguageRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new LanguageResult("unavailable", "This host does not provide language services."));
}
/// <summary>
/// Host-wide launcher choices applied on the next language-server start. The managed installation is the default; <paramref name="AllowPath"/>
/// is the host user's per-server opt-in to run an unverified executable found on PATH instead, and <paramref name="Command"/> an explicit override.
/// Neither is ever read from a project file.
/// </summary>
public sealed record LanguageServerOptions(bool AllowPath = false, string Command = "", string[]? Arguments = null);

/// <summary>User-interface-only terms review/acceptance; acceptance is never a language-agent operation.</summary>
public sealed record LanguageTermsRequest(string Workspace, string Server, bool Accept = false, string ArtifactId = "", string EulaSha256 = "");

/// <summary>The exact host installation's terms and recorded acknowledgement; acceptance does not establish entitlement.</summary>
public sealed record LanguageTermsResult(string State, string Detail = "", string Version = "", string ArtifactId = "", string EulaSha256 = "", string Text = "", bool Accepted = false, string AcceptedAtUtc = "");

public partial interface IProjectServices
{
    /// <summary>Prepares a server's installation for review or explicitly acknowledges its terms through the authenticated user interface, without execution.</summary>
    ValueTask<LanguageTermsResult> LanguageTermsAsync(LanguageTermsRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new LanguageTermsResult("unavailable", "This host does not support terms review."));
}