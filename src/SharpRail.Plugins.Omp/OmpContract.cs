using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Omp;

/// <summary>Where a context file applies: the omp user (or profile) directory, a directory above the launch directory, or the launch directory.</summary>
public enum OmpContextScope
{
    User,
    Ancestor,
    Project
}

/// <summary>The context files SharpRail offers to create when missing.</summary>
public enum OmpContextTarget
{
    /// <summary><c>&lt;workspace&gt;/AGENTS.md</c>.</summary>
    [JsonStringEnumMemberName("agents")] Agents,
    /// <summary><c>&lt;workspace&gt;/.omp/RULES.md</c>.</summary>
    [JsonStringEnumMemberName("rules")] Rules,
    /// <summary><c>&lt;workspace&gt;/.omp/APPEND_SYSTEM.md</c>.</summary>
    [JsonStringEnumMemberName("append-system")] AppendSystem
}

/// <summary>Whether <c>omp</c> was found and which version it reports.</summary>
/// <param name="Installed">Whether <c>omp --version</c> ran and exited cleanly.</param>
public sealed record OmpStatus(bool Installed, string Command)
{
    public string? Version { get; init; }
    public string? Detail { get; init; }
    /// <summary>The SharpRail profile's MCP file, when launches use the profile.</summary>
    public string? ProfileMcpPath { get; init; }
}

/// <summary>
/// One context file row. <paramref name="Depth"/> is zero for a discovered file and the import hop for an
/// <c>@</c>-import, which follows its importer in the list.
/// </summary>
/// <param name="Provider">The omp provider that discovers it: native, claude, agents, codex, gemini, opencode, github or agents-md; <c>import</c> for an import.</param>
public sealed record OmpContextFile(string Label, string Path, OmpContextScope Scope, string Provider, long Bytes, int Depth)
{
    /// <summary>A rough token estimate, about four bytes per token.</summary>
    public long Tokens => (Bytes + 3) / 4;
    /// <summary>Another provider at the same scope and depth wins, so omp does not read this file.</summary>
    public bool Shadowed { get; init; }
    /// <summary>For an import: whether it was skipped because it was already on the import chain.</summary>
    public bool Cycle { get; init; }
}

public sealed record OmpContextOffer(OmpContextTarget Target, string Label, string RelativePath);

/// <summary>The context files omp would read for a launch in <paramref name="Root"/>, most general first, with the offers for missing ones.</summary>
/// <param name="UserDirectory">The agent directory user-level files come from: the SharpRail profile's when launches use it.</param>
public sealed record OmpContextSnapshot(string Root, string UserDirectory, IReadOnlyList<OmpContextFile> Files, IReadOnlyList<OmpContextOffer> Offers);

public sealed record OmpNoParams;

public sealed record OmpWorkspaceParams(string WorkspaceId);

public sealed record OmpCreateParams(string WorkspaceId, OmpContextTarget Target);

/// <summary>The outcome of copying the default omp profile's sign-in into SharpRail's profile; the message carries no credentials.</summary>
public sealed record OmpSignInResult(bool Success, string Message);

public sealed record OmpSettings
{
    /// <summary>The executable; the system installation on PATH by default.</summary>
    public string Command { get; init; } = "omp";
    /// <summary><c>--model</c>: a model id, <c>provider/model</c> or <c>@role</c>; blank uses omp's default.</summary>
    public string Model { get; init; } = "";
    /// <summary><c>--approval-mode</c>: <c>always-ask</c>, <c>always-ask-cached</c>, <c>write</c> or <c>yolo</c>; blank uses omp's configuration.</summary>
    public string ApprovalMode { get; init; } = "";
    /// <summary><c>--advisor</c>.</summary>
    public bool Advisor { get; init; }
    /// <summary>Launch with SharpRail's profile, whose MCP file names SharpRail's server.</summary>
    public bool Mcp { get; init; } = true;
    /// <summary>Keep omp's own language servers; off passes <c>--no-lsp</c> so sessions use SharpRail's.</summary>
    public bool OwnCodeIntelligence { get; init; }
}

public static class OmpContract
{
    public const int WireVersion = 1;

    public static readonly PluginMethod<OmpNoParams, OmpStatus> Status = new("status");
    public static readonly PluginMethod<OmpWorkspaceParams, OmpContextSnapshot> Context = new("context");
    public static readonly PluginMethod<OmpCreateParams, OmpContextSnapshot> CreateContextFile = new("createContextFile");
    /// <summary>Pipes <c>omp auth export</c> (default profile) into <c>omp --profile sharprail auth import -</c> on the host.</summary>
    public static readonly PluginMethod<OmpNoParams, OmpSignInResult> ImportSignIn = new("importSignIn");

    public static readonly PluginContract Contract = PluginContract.Create<OmpSettings>(OmpManifest.Id, WireVersion,
        [Status, Context, CreateContextFile, ImportSignIn], []);
}