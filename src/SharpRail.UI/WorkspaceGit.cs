namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? gitRefresh;
    private bool gitLoading;
    private string? gitError;
    private SharpRail.Host.Abstractions.BranchCatalog gitBranches = new([], [], "");

    private void RememberGitSelection()
    {
        if (WorkspaceMounted && !removedWorkspaces.Contains(workspaceRoot)) profile.Data.GitSelections[workspaceRoot] = new(comparison, changeScope, selectedCommit);
    }

    private void RestoreGitSelection()
    {
        var selection = profile.Data.GitSelections.GetValueOrDefault(workspaceRoot);
        comparison = selection?.Target ?? ""; changeScope = selection?.Scope ?? "All changes";
        selectedCommit = selection?.Commit; gitCommits = [];
        gitBranches = new([], [], "");
    }

    private void SaveGitSelection() { RememberGitSelection(); SaveProfile(); }

    private async Task RefreshGitAsync(long request)
    {
        if (request != projectRequest || !WorkspaceMounted || lifetime.IsCancellationRequested) return;
        gitRefresh?.Cancel();
        gitRefresh?.Dispose();
        gitRefresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = gitRefresh.Token;
        var selectedComparison = comparison;
        var selectedScope = changeScope;
        var commit = selectedCommit;
        var scope = commit is not null ? "commit" : selectedScope == "Staged" ? "staged" : selectedScope == "Uncommitted" ? "uncommitted" : "all";
        try
        {
            var catalog = commit is not null
                ? await Task.Run(async () => await host.ListCommitsAsync(selectedComparison, token), token) : gitCommits;
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison || selectedScope != changeScope || commit?.Sha != selectedCommit?.Sha) return;
            if (commit is not null && !catalog.Any(item => item.Sha == commit.Sha))
            {
                selectedCommit = null; changeScope = "All changes";
                SaveGitSelection();
                gitLoading = true; gitError = null; RefreshGitPanels();
                ShowNotification("That commit is no longer in this branch — showing all changes.");
                await RefreshGitAsync(request);
                return;
            }
            var snapshot = await Task.Run(async () => await host.GetGitAsync(commit?.Sha ?? selectedComparison, token, scope), token);
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison || selectedScope != changeScope || commit?.Sha != selectedCommit?.Sha) return;
            var branches = snapshot.IsRepository
                ? await Task.Run(async () => await host.ListBranchesAsync(false, token), token)
                : new SharpRail.Host.Abstractions.BranchCatalog([], [], "");
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison || selectedScope != changeScope || commit?.Sha != selectedCommit?.Sha) return;
            git = snapshot;
            gitBranches = branches;
            gitCommits = scope == "commit" ? catalog : snapshot.Commits;
            branchLabel.Text = snapshot.IsRepository ? snapshot.Branch : "";
            branchIcon.IsVisible = snapshot.IsRepository;
            if (readyBranch is not null) readyBranch.Text = ReadyBranchText();
            gitLoading = false; gitError = null;
            RefreshGitPanels();
            _ = RefreshDiffTabsAsync(request);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison || selectedScope != changeScope || commit?.Sha != selectedCommit?.Sha) return;
            gitLoading = false; gitError = "Git could not be loaded: " + error.Message;
            RefreshGitPanels();
            Console.Error.WriteLine(error);
        }
    }

    private void RefreshGitPanels()
    {
        toolContent.Remove("changes");
        toolContent.Remove("review");
        if (RailSignature() == railSignature) { UpdateRailSelection(); surface.RefreshContents("changes", "review"); return; }
        KeepingFocus(() =>
        {
            toolContent.Remove("projects");
            surface.RefreshContents("projects", "changes", "review");
        });
    }

    // The rows the rail is built from. Anything else it shows (selection, branches, what Git allows) is restyled in
    // place, so a refresh that adds or removes no row leaves its controls, focus and pointer targets alone.
    private string RailSignature() => string.Join("\0", state.Current.Projects.SelectMany(RailWorkspaces)
        .Select(workspace => $"{workspace.Path}\t{workspace.Kind}\t{WorktreeLocked(workspace.Path)}"));

    private bool WorktreeLocked(string path) => git.Worktrees.Any(tree => tree.Path == path && tree.IsLocked);
}