using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.SpecDialect;

/// <summary>One spec in a workspace's graph. <c>Path</c> is worktree-relative; absent lists are empty, and an absent status or parent is left out.</summary>
public sealed record SpecGraphNode(
    string Id,
    string Type,
    string Title,
    string Path,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Implements,
    IReadOnlyList<string> Tags)
{
    public string? Status { get; init; }
    public string? Parent { get; init; }
}

public sealed record SpecGraphSnapshot(IReadOnlyList<SpecGraphNode> Nodes);

public sealed record SpecGraphParams(string WorkspaceId);

public static class SpecDialectContract
{
    public const string Id = "spec-dialect";
    public const int WireVersion = 1;

    /// <summary><c>plugin.spec-dialect.graph</c>: the workspace's spec graph, read from its worktree; an unknown workspace fails.</summary>
    public static readonly PluginMethod<SpecGraphParams, SpecGraphSnapshot> Graph = new("graph");

    public static readonly PluginContract Contract = PluginContract.Create(Id, WireVersion, [Graph], []);
}

public static class SpecDialectManifest
{
    /// <summary>The Specs side tool's local name, and its layout id <c>plugin:spec-dialect:specs</c>.</summary>
    public const string Tool = "specs";
    public static readonly string ToolId = PluginIdentity.ToolId(SpecDialectContract.Id, Tool);

    public static readonly PluginManifest Manifest = new(SpecDialectContract.Id, "Specs", "book-open", "0.1.0", PluginApi.Generation, SpecDialectContract.WireVersion)
    {
        Description = "Reads the project's spec graph and shows it in a Specs panel.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.SpecDialect.Host.dll",
        Ui = "SharpRail.Plugins.SpecDialect.UI.dll",
        Contributes = new() { SideTools = [new(Tool, "Specs", "book-open", PluginToolSide.Right)] }
    };
}