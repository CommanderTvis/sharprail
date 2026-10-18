using System.ComponentModel;

namespace SharpRail.Plugins.AirContext.Host;

/// <summary>Arguments of <c>context_search</c>.</summary>
public sealed record ContextSearchParams(
    [property: Description("Detailed natural-language description of the behavior or implementation to locate.")] string Query)
{
    [Description("Absolute directory of this SharpRail workspace; cannot target another workspace.")]
    public string? Workspace { get; init; }

    [Description("Optional workspace-relative path filter. No external directories or repository URLs.")]
    public string? Path { get; init; }

    [Description("Requested result limit when the native tool supports it; output is always bounded.")]
    public int? Limit { get; init; }
}

/// <summary>Arguments of <c>context_status</c>.</summary>
public sealed record ContextStatusParams
{
    [Description("Absolute directory of this SharpRail workspace; cannot target another workspace.")]
    public string? Workspace { get; init; }

    [Description("Ask the vendor CLI for current index status; does not start indexing.")]
    public bool? Refresh { get; init; }
}

/// <summary>Arguments of <c>context_index</c>.</summary>
public sealed record ContextIndexParams
{
    [Description("Absolute directory of this SharpRail workspace; cannot target another workspace.")]
    public string? Workspace { get; init; }
}