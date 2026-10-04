using Avalonia.Controls;
using Avalonia.LogicalTree;

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
            var catalog = commit is not null
                ? await Task.Run(async () => await host.ListCommitsAsync(selectedComparison, token), token) : scopeCommits ?? [];
            var resolved = commit is not null ? await Task.Run(async () => await host.GetCommitAsync(commit.Sha, token), token) : null;
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison || selectedScope != changeScope || commit?.Sha != selectedCommit?.Sha) return;
            if (commit is not null && resolved is null)
            {
                selectedCommit = null; changeScope = "All changes";
                SaveGitSelection();
                gitLoading = true; gitError = null; RefreshGitPanels();
                ShowNotification("That commit is no longer in this branch — showing all changes.");
                await RefreshGitAsync(request);
                return;
            }
            if (resolved is not null) selectedCommit = resolved;
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
        if (toolContent.GetValueOrDefault("review")?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(label => label.Name == "ReviewSummary") is { } summary)
            summary.Text = ReviewSummary();
        UpdateProjectHomeActions();
        SyncPluginTools();
        var openMenu = toolContent.GetValueOrDefault("changes")?.GetLogicalDescendants().OfType<Button>()
            .Select(button => button.ContextMenu).FirstOrDefault(menu => menu?.IsOpen == true);
        if (openMenu is not null)
        {
            if (gitPanelMenu is null)
            {
                gitPanelMenu = openMenu;
                openMenu.Closed += Closed;
                void Closed(object? sender, EventArgs args)
                {
                    openMenu.Closed -= Closed;
                    gitPanelMenu = null;
                    if (!lifetime.IsCancellationRequested) RefreshGitPanels();
                }
            }
            return;
        }
        ReconcileChangesPanel();
        if (toolContent.ContainsKey("review"))
        {
            var previousKey = reviewKey;
            EnsureOpenReview();
            if (previousKey != reviewKey) RefreshReviewPanel();
        }
        if (RailSignature() == railSignature) { UpdateRailSelection(); surface.RefreshContents("changes", "review"); return; }
        KeepingFocus(() =>
        {
            toolContent.Remove("projects");
            surface.RefreshContents("projects", "changes");
        });
    }

    private ContextMenu? gitPanelMenu;
    private string changesSignature = "";

    private string ChangesSignature() => string.Join("\0", [changeScope, comparison, selectedCommit?.Sha, changeTree, gitLoading, gitError, git.IsRepository, git.Branch,
        string.Join("\t", git.Branches), string.Join("\t", git.Changes), string.Join("\t", gitBranches.Local), string.Join("\t", gitBranches.Remote),
        scopeCommits is null ? null : string.Join("\t", scopeCommits.Select(commit => commit.Sha))]);

    // The rows the rail is built from. Anything else it shows (selection, branches, what Git allows) is restyled in
    // place, so a refresh that adds or removes no row leaves its controls, focus and pointer targets alone.
    private string RailSignature() => string.Join("\0", state.Current.Projects.SelectMany(RailWorkspaces)
        .Select(workspace => $"{workspace.Path}\t{workspace.Kind}\t{WorktreeLocked(workspace.Path)}"));

    private bool WorktreeLocked(string path) => git.Worktrees.Any(tree => tree.Path == path && tree.IsLocked);
}