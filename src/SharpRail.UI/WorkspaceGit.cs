namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? gitRefresh;
    private bool gitLoading;
    private string? gitError;
    private SharpRail.Host.Abstractions.BranchCatalog gitBranches = new([], [], "");

    private void RememberGitSelection()
    {
        if (WorkspaceMounted) profile.Data.GitSelections[workspaceRoot] = new(comparison, changeScope, selectedCommit);
    }

    private void RestoreGitSelection()
    {
        var selection = profile.Data.GitSelections.GetValueOrDefault(workspaceRoot);
        comparison = selection?.Target ?? ""; changeScope = selection?.Scope ?? "All changes";
        selectedCommit = selection?.Commit; scopeCommits = null;
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
            IReadOnlyList<SharpRail.Host.Abstractions.GitCommit> catalog = commit is not null
                ? await Task.Run(async () => await host.ListCommitsAsync(selectedComparison, token), token) : [];
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
            // A commit scope already read the catalogue to validate its commit; the other scopes leave it to the menu.
            if (commit is not null) scopeCommits = catalog;
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
        // A refresh that changes nothing a panel shows keeps its controls, so an open menu or a pointer target survives.
        if (ChangesSignature() != changesSignature) toolContent.Remove("changes");
        if (ReviewSignature() != reviewSignature) toolContent.Remove("review");
        if (RailSignature() == railSignature) { surface.RefreshContents("changes", "review"); return; }
        KeepingFocus(() =>
        {
            toolContent.Remove("projects");
            surface.RefreshContents("projects", "changes", "review");
        });
    }

    private string reviewSignature = "";
    private string changesSignature = "";

    private string ChangesSignature() => string.Join("\0", [changeScope, comparison, selectedCommit?.Sha, changeTree, gitLoading, gitError, git.IsRepository, git.Branch,
        string.Join("\t", git.Branches), string.Join("\t", git.Changes), string.Join("\t", gitBranches.Local), string.Join("\t", gitBranches.Remote),
        scopeCommits is null ? null : string.Join("\t", scopeCommits.Select(commit => commit.Sha))]);

    private string ReviewSignature() => $"{git.IsRepository}\0{git.Branch}\0{git.Changes.Count}";

    private string RailSignature() => git.IsRepository + "\0" + string.Join("\0", git.Worktrees.Select(tree => $"{tree.Path}\t{tree.Branch}\t{tree.IsLocked}"));
}