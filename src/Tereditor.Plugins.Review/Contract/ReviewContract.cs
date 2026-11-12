using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Review;

/// <summary>What the pane says about a workspace: whether it is a repository, its branch and how many files differ from HEAD.</summary>
public sealed record ReviewSummary(bool IsRepository, string Branch, int Changed);

public sealed record ReviewSummaryParams(string WorkspaceId);

public static class ReviewContract
{
    public static readonly PluginMethod<ReviewSummaryParams, ReviewSummary> Summary = new("summary");
    public static readonly PluginContract Contract = PluginContract.Create(ReviewPlugin.Id, 1, [Summary], [], ReviewJson.Default);
}

public static class ReviewPlugin
{
    public const string Id = "review";
    public const string Tool = "review";

    public static readonly PluginManifest Manifest = new(Id, "Review", "discuss-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.VersionControl,
        Description = "The Review tool window: a summary of the workspace's changes and the repo host's pull request review.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Host = "SharpRail.Plugins.Review.Host.dll",
        Ui = "SharpRail.Plugins.Review.UI.dll",
        Contributes = new()
        {
            SideTools = [new(Tool, "Review", "discuss-line", PluginToolSide.Right) { RequiresGit = true }]
        }
    };
}

[JsonSerializable(typeof(ReviewSummaryParams))]
[JsonSerializable(typeof(ReviewSummary))]
internal sealed partial class ReviewJson : JsonSerializerContext;