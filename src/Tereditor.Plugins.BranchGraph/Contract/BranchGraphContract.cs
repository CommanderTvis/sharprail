using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.BranchGraph;

/// <summary>One commit as the graph draws it: its parents are the edges, its refs the labels.</summary>
/// <param name="Refs">Ref names pointing here, as <c>%D</c> reports them, already split.</param>
/// <param name="Branches">The local branches among <paramref name="Refs"/>; remote-tracking branches and tags are not in it.</param>
public sealed record GitGraphCommit(string Sha, string ShortSha, IReadOnlyList<string> Parents, IReadOnlyList<string> Refs,
    string Subject, string Author, string CommittedAt, IReadOnlyList<string>? Branches = null);

/// <summary>A worktree sitting on a commit; the workspace is absent for one SharpRail does not list.</summary>
public sealed record GitGraphWorktree(string Sha, string Name, string? WorkspaceId = null);

/// <param name="HasMore">True when older commits follow this page.</param>
public sealed record GitGraph(IReadOnlyList<GitGraphCommit> Commits, IReadOnlyList<GitGraphWorktree> Worktrees, bool HasMore);

public sealed record GitGraphParams(string ProjectId, int Skip = 0);

public sealed record GitPatchParams(string ProjectId, string Sha);

public sealed record GitPatch(string Patch);

public static class BranchGraphContract
{
    public static readonly PluginMethod<GitGraphParams, GitGraph> Graph = new("graph");
    public static readonly PluginMethod<GitPatchParams, GitPatch> Patch = new("patch");
    public static readonly PluginContract Contract = PluginContract.Create(BranchGraphPlugin.Id, 1, [Graph, Patch], [], BranchGraphJson.Default);
}

public static class BranchGraphPlugin
{
    public const string Id = "branch-graph";
    public const string Tool = "graph";

    public static readonly PluginManifest Manifest = new(Id, "Git Graph", "git-branch", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.VersionControl,
        Description = "Shows the project's branch graph as a side tool.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Host = "SharpRail.Plugins.BranchGraph.Host.dll",
        Ui = "SharpRail.Plugins.BranchGraph.UI.dll",
        Contributes = new()
        {
            SideTools = [new(Tool, "Git Graph", "git-branch", PluginToolSide.Right) { RequiresGit = true }]
        }
    };
}

[JsonSerializable(typeof(GitGraphParams))]
[JsonSerializable(typeof(GitGraph))]
[JsonSerializable(typeof(GitPatchParams))]
[JsonSerializable(typeof(GitPatch))]
internal sealed partial class BranchGraphJson : JsonSerializerContext;