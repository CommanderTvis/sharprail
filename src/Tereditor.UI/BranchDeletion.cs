using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>What deleting a local branch needs to say before it runs: a refusal, or the confirmation and the tip to restore.</summary>
internal sealed record BranchDeletePlan(string? Refusal, string Title, string Text, string ConfirmLabel, string Sha)
{
    internal string ShortSha => Sha[..Math.Min(7, Sha.Length)];
}

public sealed partial class WorkbenchWindow
{
    internal IReadOnlyList<WorktreeInfo> Worktrees => git.Worktrees;
    internal const string BranchDeleteConfirmName = "BranchDeleteConfirm";

    /// <summary>
    /// Refuses the checked-out branch, a branch checked out in any worktree and the default branch; a branch contained in the default
    /// base asks a plain confirmation, an unmerged one names how many commits stop being reachable and says so on its button.
    /// </summary>
    internal static BranchDeletePlan PlanBranchDeletion(string name, BranchCatalog catalog, IReadOnlyList<WorktreeInfo> worktrees)
    {
        static BranchDeletePlan Refuse(string reason) => new(reason, "", "", "", "");
        if (!catalog.Local.Contains(name)) return Refuse($"There is no local branch {name}.");
        if (worktrees.FirstOrDefault(tree => tree.Branch == name) is { } holder)
            return Refuse(holder.IsMain ? $"{name} is the branch currently checked out." : $"{name} is checked out by the workspace at {holder.Path}.");
        if (catalog.DefaultBranch == name) return Refuse($"{name} is the project's default branch.");
        var tip = catalog.Tips.GetValueOrDefault(name);
        var sha = tip?.Sha ?? "";
        var plan = new BranchDeletePlan(null, $"Delete branch {name}?", "", "Delete branch", sha);
        var at = sha.Length > 0 ? " at " + plan.ShortSha : "";
        var baseName = catalog.DefaultBase;
        if (tip is { Unmerged: 0 })
            return plan with { Text = $"{name}{at} is already contained in {baseName}; no commit becomes unreachable." };
        var count = tip is null ? "Its unmerged commits" : tip.Unmerged == 1 ? "1 commit" : tip.Unmerged + " commits";
        return plan with
        {
            Text = $"{name}{at} is not merged into {baseName}. {count} will no longer be reachable from any branch.",
            ConfirmLabel = "Delete unmerged branch"
        };
    }

    /// <summary>
    /// The one way a local branch is deleted, for the branch list and for plugins: refusals and confirmation first, then the host's deletion,
    /// a refreshed branch list and a toast whose Undo recreates the branch at its old tip.
    /// </summary>
    internal async Task<PluginBranchDeleteOutcome> DeleteBranchWithConfirmationAsync(string name)
    {
        if (!WorkspaceMounted) return PluginBranchDeleteOutcome.Refused;
        BranchCatalog catalog;
        try { catalog = await host.ListBranchesAsync(false, lifetime.Token); }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); return PluginBranchDeleteOutcome.Failed; }
        var plan = PlanBranchDeletion(name, catalog, git.Worktrees);
        if (plan.Refusal is { } refusal)
        {
            Toasts.Push(ToastVariant.Info, refusal, "Branch not deleted");
            return PluginBranchDeleteOutcome.Refused;
        }
        if (!await Dialogs.Confirm(this, plan.Title, plan.Text, plan.ConfirmLabel, BranchDeleteConfirmName)) return PluginBranchDeleteOutcome.Cancelled;
        if (!await GitActionAsync(new("delete-branch", "", name))) { await LoadBranchesAsync(); return PluginBranchDeleteOutcome.Failed; }
        await LoadBranchesAsync();
        if (plan.Sha.Length > 0)
            Toasts.Push(ToastVariant.Info, $"Deleted branch {name} (was {plan.ShortSha})", duration: TimeSpan.FromSeconds(15), countdown: true,
                action: ToastAction.Async("Undo", async () =>
                {
                    await GitActionAsync(new("create-branch", "", name, plan.Sha));
                    await LoadBranchesAsync();
                }));
        return PluginBranchDeleteOutcome.Deleted;
    }
}