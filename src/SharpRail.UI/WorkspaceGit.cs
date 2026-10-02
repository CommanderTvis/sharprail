using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? gitRefresh;
    private bool gitLoading;
    private string? gitError;
    private ContextMenu? gitPanelMenu;

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
            git = snapshot;
            gitCommits = scope == "commit" ? catalog : snapshot.Commits;
            branchLabel.Text = snapshot.IsRepository ? snapshot.Branch : "";
            branchIcon.IsVisible = snapshot.IsRepository;
            UpdateReadyBranch();
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
        toolContent.Remove("changes");
        toolContent.Remove("review");
        if (RailSignature() == railSignature) { surface.RefreshContents("changes", "review"); return; }
        KeepingFocus(() =>
        {
            toolContent.Remove("projects");
            surface.RefreshContents("projects", "changes", "review");
        });
    }

    private string RailSignature() => git.IsRepository + "\0" + string.Join("\0", git.Worktrees.Select(tree => $"{tree.Path}\t{tree.Branch}\t{tree.IsLocked}"));
}