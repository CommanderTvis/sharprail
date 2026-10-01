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
        // The review target is the host's, shared by every client; the profile only remembers one the host has none for.
        comparison = state.Current.DiffBase(workspaceRoot) is { Length: > 0 } target ? target : selection?.Target ?? "";
        changeScope = selection?.Scope ?? "All changes";
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
            if (commit is not null && !catalog.Any(item => item.Sha == commit.Sha)) { await ShowAllChangesAsync(request); return; }
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
            SetBranch(snapshot.IsRepository ? snapshot.Branch : "");
            if (readyBranch is not null) readyBranch.Text = ReadyBranchText();
            gitLoading = false; gitError = null;
            RefreshGitPanels();
            _ = RefreshDiffTabsAsync(request);
            _ = RefreshWorkspaceStatsAsync(request);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SharpRail.Host.Abstractions.HostException error) when (error.Code == SharpRail.Host.Abstractions.HostErrorCode.UnknownCommit && commit is not null)
        {
            // The commit left the branch between the catalog read and the snapshot.
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison || selectedScope != changeScope || commit.Sha != selectedCommit?.Sha) return;
            await ShowAllChangesAsync(request);
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison || selectedScope != changeScope || commit?.Sha != selectedCommit?.Sha) return;
            gitLoading = false; gitError = "Git could not be loaded: " + error.Message;
            RefreshGitPanels();
            Console.Error.WriteLine(error);
        }
    }

    private Task ShowAllChangesAsync(long request)
    {
        selectedCommit = null; changeScope = "All changes";
        SaveGitSelection();
        gitLoading = true; gitError = null; RefreshGitPanels();
        ShowNotification("That commit is no longer in this branch — showing all changes.");
        return RefreshGitAsync(request);
    }

    private void RefreshGitPanels()
    {
        UpdateProjectHomeActions();
        SyncPluginTools();
        // A refresh that changes nothing a panel shows keeps its controls, so an open menu or a pointer target survives.
        if (ChangesSignature() != changesSignature) toolContent.Remove("changes");
        if (ReviewSignature() != reviewSignature) toolContent.Remove("review");
        if (RailSignature() == railSignature) { UpdateRailSelection(); surface.RefreshContents("changes", "review"); return; }
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

    // The rows the rail is built from. Anything else it shows (selection, branches, what Git allows) is restyled in
    // place, so a refresh that adds or removes no row leaves its controls, focus and pointer targets alone.
    private string RailSignature() => string.Join("\0", state.Current.Projects.SelectMany(RailWorkspaces)
        .Select(workspace => $"{workspace.Path}\t{workspace.Kind}\t{WorktreeLocked(workspace.Path)}"));

    private bool WorktreeLocked(string path) => git.Worktrees.Any(tree => tree.Path == path && tree.IsLocked);
}