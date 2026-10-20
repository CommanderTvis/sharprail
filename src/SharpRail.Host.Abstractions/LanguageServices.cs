namespace SharpRail.Host.Abstractions;

/// <summary>A workspace-explicit language request. Positions are zero-based UTF-16; null Text reads disk.</summary>
public sealed record LanguageRequest(string Workspace, string Path = "", string Operation = "inspect", int Line = 0,
    int Column = 0, string? Text = null, string DocumentId = "", long Version = 0, string Query = "", bool Dirty = false, string Server = "", string Scope = "", IReadOnlyList<string>? Paths = null, int Offset = 0, int Limit = 200, string? Configuration = null);

/// <summary>A location, diagnostic or semantic token. Positions are zero-based UTF-16.</summary>
public sealed record LanguageItem(string Path, int Line, int Column, int EndLine, int EndColumn,
    string Text = "", string Kind = "", string Source = "", string Preview = "", string Body = "", bool Truncated = false)
{
    /// <summary>Space-separated semantic-token modifier names from the server legend.</summary>
    public string Modifiers { get; init; } = "";
}

/// <summary>A supported language server in one workspace; listing never starts a process.</summary>
public sealed record LanguageServerState(string Id, string Path, string State, string Detail, string Version, string Configuration = "", string ActiveConfiguration = "", bool Configured = false)
{
    /// <summary>How the most recent run of this server ended, when the host knows; it outlives the process and host restarts.</summary>
    public LanguageExit? Exit { get; init; }
}

/// <summary>
/// The host's record of one language-server process ending. <paramref name="Reason"/> is one line of facts and labelled suspicions;
/// <paramref name="Unexpected"/> is false for stops the user or SharpRail's lifecycle asked for. <paramref name="Details"/> is a copyable report with the
/// stderr tail, recent JSON-RPC messages and requests in flight.
/// </summary>
public sealed record LanguageExit(string Time, string Reason, bool Unexpected, string Initiator, int? ExitCode, string Signal, int UptimeSeconds, long MemoryBytes, string Details);

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
public sealed record LanguageServerOptions(bool Disabled = false, bool AllowPath = false, string Command = "", string[]? Arguments = null);

/// <summary>User-interface-only terms review/acceptance; acceptance is never a language-agent operation.</summary>
public sealed record IntelliJTermsRequest(string Workspace, bool Accept = false, string ArtifactId = "", string EulaSha256 = "");

/// <summary>The exact host installation's terms and recorded acknowledgement; acceptance does not establish entitlement.</summary>
public sealed record IntelliJTermsResult(string State, string Detail = "", string Version = "", string ArtifactId = "", string EulaSha256 = "", string Text = "", bool Accepted = false, string AcceptedAtUtc = "");

public partial interface IProjectServices
{
    /// <summary>Prepares the installation for review or explicitly acknowledges IntelliJ terms through the authenticated user interface, without execution.</summary>
    ValueTask<IntelliJTermsResult> IntelliJTermsAsync(IntelliJTermsRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new IntelliJTermsResult("unavailable", "This host does not support IntelliJ terms review."));
}