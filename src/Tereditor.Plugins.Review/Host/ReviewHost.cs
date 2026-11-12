using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Review.Host;

/// <summary>The host half: the branch and the number of changed files of a workspace, through the host's Git runner.</summary>
public sealed class ReviewHost : PluginHostModule
{
    public override PluginContract Contract => ReviewContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.Method(ReviewContract.Summary, async (parameters, _, cancellationToken) =>
        {
            var workspace = (await context.WorkspacesAsync(null, cancellationToken)).FirstOrDefault(item => item.Id == parameters.WorkspaceId)
                ?? throw new InvalidOperationException($"Unknown workspace: {parameters.WorkspaceId}");
            var status = await context.GitAsync(workspace.Path, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken: cancellationToken);
            if (status.Failure is not null || !status.Ok) return new ReviewSummary(false, "", 0);
            return new ReviewSummary(true, workspace.Branch, Count(status.Out));
        });
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    /// <summary>Entries of <c>status --porcelain -z</c>; a rename or copy carries its source as a second field that is not another change.</summary>
    internal static int Count(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        for (var index = 0; index < fields.Length; index++)
        {
            count++;
            if (fields[index].Length > 1 && (fields[index][0] is 'R' or 'C' || fields[index][1] is 'R' or 'C')) index++;
        }
        return count;
    }
}