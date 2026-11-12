using System.Globalization;
using System.Text.RegularExpressions;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    [GeneratedRegex(@"(\d+) insertion")]
    private static partial Regex Insertions();

    [GeneratedRegex(@"(\d+) deletion")]
    private static partial Regex Deletions();

    /// <summary>
    /// Line totals for a workspace row's badge, over the range its Changes panel opens on: the merge base of the
    /// workspace's review target and its HEAD to the working tree, or HEAD itself without a target.
    /// </summary>
    public async ValueTask<DiffStats?> GetDiffStatsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(workspacePath);
        var worktrees = GitRepository.ParseWorktrees(await GitRepository.RunAsync(root, cancellationToken, "worktree", "list", "--porcelain", "-z"));
        if (!worktrees.Any(tree => tree.Path == target))
            throw new UnauthorizedAccessException("Only this project's workspaces have change totals.");
        var comparison = state?.Current.DiffBase(target) ?? "";
        var from = comparison.Length > 0 ? await GitRepository.ComparisonBaseAsync(target, comparison, cancellationToken) : "HEAD";
        var summary = await GitRepository.RunAsync(target, cancellationToken, "diff", "--shortstat", "--end-of-options", from, "--");
        return new(Count(Insertions().Match(summary)), Count(Deletions().Match(summary)));

        static int Count(Match match) => match.Success ? int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 0;
    }
}