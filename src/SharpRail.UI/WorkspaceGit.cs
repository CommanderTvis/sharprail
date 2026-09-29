namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? gitRefresh;
    private bool gitLoading;
    private string? gitError;

    private void RememberGitSelection()
    {
        if (WorkspaceMounted) profile.Data.GitSelections[workspaceRoot] = new(comparison, changeScope, selectedCommit);
    }

    private void RestoreGitSelection()
    {
        var selection = profile.Data.GitSelections.GetValueOrDefault(workspaceRoot);
        comparison = selection?.Target ?? ""; changeScope = selection?.Scope ?? "All changes";
        selectedCommit = selection?.Commit; gitCommits = [];
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
            git = snapshot;
            gitCommits = scope == "commit" ? catalog : snapshot.Commits;
            branchLabel.Text = snapshot.IsRepository ? snapshot.Branch : "";
            branchIcon.IsVisible = snapshot.IsRepository;
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
        toolContent.Remove("projects");
        toolContent.Remove("changes");
        toolContent.Remove("review");
        surface.RefreshContents("projects", "changes", "review");
    }
}
