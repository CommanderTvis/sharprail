using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SharpRail.Plugins.SpecDialect;

public sealed record SpecGrepParams([property: Description("Regex or substring to search for within spec files.")] string Pattern)
{
    [Description("Treat pattern as a regular expression (default: substring).")]
    public bool? Regex { get; init; }
    [Description("Case-insensitive match (default: true).")]
    public bool? IgnoreCase { get; init; }
    [Description("Only search specs with this frontmatter type.")]
    public string? Type { get; init; }
    [Description("Only search specs carrying this tag.")]
    public string? Tag { get; init; }
    [Description("Only search specs whose parent is this id.")]
    public string? Parent { get; init; }
    [Description("Only search specs that depend-on this id.")]
    public string? DependsOn { get; init; }
    [Description("Max matches to return (default: 200).")]
    public double? Limit { get; init; }
}

public sealed record SpecGetParams([property: Description("The spec id to look up.")] string Id);
public sealed record SpecDeleteParams([property: Description("Id of the spec to delete.")] string Id);
public sealed record SpecValidateParams;

public enum SpecSliceDirection { Subtree, Ancestors, Neighbors }
public enum SpecEdgeKind { Parent, [JsonStringEnumMemberName("depends-on")] DependsOn, References, Implements }
public enum SpecType
{
    [JsonStringEnumMemberName("goal-and-requirements")] GoalAndRequirements,
    [JsonStringEnumMemberName("architecture-design")] ArchitectureDesign,
    [JsonStringEnumMemberName("module-design")] ModuleDesign,
    [JsonStringEnumMemberName("submodule-design")] SubmoduleDesign,
    [JsonStringEnumMemberName("task-spec")] TaskSpec
}
public enum SpecStatus { Draft, Active, Stale, Done, Deprecated }

public sealed record SpecGraphToolParams(
    [property: Description("Id of the node to start from.")] string Root,
    [property: Description("subtree = down the parent tree; ancestors = up the parent chain; neighbors = across an edge and its reverse.")] SpecSliceDirection Direction)
{
    [Description("How many hops to expand (default: 1).")]
    public double? Depth { get; init; }
    [Description("For direction=neighbors: which edge kind to traverse (default: depends-on).")]
    public SpecEdgeKind? Edge { get; init; }
}

public sealed record SpecCreateParams(
    [property: Description("Root-relative path for the new spec file, ending in .md. Must stay inside the project root.")] string Path,
    [property: Description("Unique spec id.")] string Id,
    [property: Description("Spec type: goal-and-requirements | architecture-design | module-design | submodule-design | task-spec.")] SpecType Type,
    [property: Description("Human-readable title.")] string Title)
{
    [Description("Lifecycle status: draft | active | stale | done | deprecated.")]
    public SpecStatus? Status { get; init; }
    [Description("Parent id (the tree edge).")]
    public string? Parent { get; init; }
    [Description("depends-on link ids.")]
    public IReadOnlyList<string>? DependsOn { get; init; }
    [Description("references link ids.")]
    public IReadOnlyList<string>? References { get; init; }
    [Description("implements link ids.")]
    public IReadOnlyList<string>? Implements { get; init; }
    [Description("covers ids.")]
    public IReadOnlyList<string>? Covers { get; init; }
    [Description("tags.")]
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record SpecUpdateParams([property: Description("Id of the spec to update.")] string Id)
{
    [Description("Scalar frontmatter fields to set/overwrite. For list fields use addList/removeList.")]
    public Dictionary<string, string>? Set { get; init; }
    [Description("Frontmatter field names to remove entirely.")]
    public IReadOnlyList<string>? Remove { get; init; }
    public Dictionary<string, IReadOnlyList<string>>? AddList { get; init; }
    public Dictionary<string, IReadOnlyList<string>>? RemoveList { get; init; }
}