using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private sealed record ChangeFrameKey(GitChange Change, string Label, bool CanStage, (string PluginId, string Icon)? Icon);
    private sealed record GitPanelProjection(GitSnapshot Git, BranchCatalog Branches, IReadOnlyList<GitCommit> Commits,
        string Target, string Scope, GitCommit? Commit, bool Tree, bool Loading, string? Error, string Icons)
    {
        internal bool Matches(GitPanelProjection other) => Target == other.Target && Scope == other.Scope && Commit == other.Commit &&
            Tree == other.Tree && Loading == other.Loading && Error == other.Error && Icons == other.Icons &&
            Git.IsRepository == other.Git.IsRepository && Git.Branch == other.Git.Branch && Git.Changes.SequenceEqual(other.Git.Changes) &&
            Branches.Local.SequenceEqual(other.Branches.Local) && Branches.Remote.SequenceEqual(other.Branches.Remote) &&
            Commits.SequenceEqual(other.Commits);
    }

    private GitPanelProjection? changesProjection;

    private GitPanelProjection CurrentGitProjection() => new(git, gitBranches, scopeCommits ?? [], comparison, changeScope, selectedCommit,
        changeTree, gitLoading, gitError, string.Join("\n", git.Changes.Select(change =>
            Plugins.FileIcon(change.Path, SharpRail.Plugins.Api.UI.FileIconKind.File) is { } icon ? $"{icon.PluginId}\t{icon.Icon}" : "")));

    private void ReconcileChangesPanel()
    {
        if (toolContent.GetValueOrDefault("changes") is not Grid panel) return;
        var nextProjection = CurrentGitProjection();
        panel.IsHitTestVisible = WorkspaceMounted && !gitLoading;
        if (gitLoading && changesProjection is { Loading: false, Error: null }) return;
        if (changesProjection?.Matches(nextProjection) == true) return;
        var next = (Grid)ChangesPanel();
        var oldToolbar = panel.Children[0].GetLogicalDescendants().OfType<Grid>().First();
        var nextToolbar = next.Children[0].GetLogicalDescendants().OfType<Grid>().First();
        var oldSelectors = oldToolbar.Children.OfType<StackPanel>().Single();
        var nextSelectors = nextToolbar.Children.OfType<StackPanel>().Single();
        foreach (var fresh in nextSelectors.Children.OfType<Button>().ToArray())
        {
            var existing = oldSelectors.Children.OfType<Button>().FirstOrDefault(button => button.Name == fresh.Name);
            if (existing is null) { nextSelectors.Children.Remove(fresh); oldSelectors.Children.Add(fresh); }
            else
            {
                CopyTexts(existing, fresh);
                existing.ContextMenu = fresh.ContextMenu;
                ToolTip.SetTip(existing, ToolTip.GetTip(fresh));
            }
        }
        foreach (var existing in oldSelectors.Children.OfType<Button>().Where(button => !nextSelectors.Children.OfType<Button>()
            .Any(fresh => fresh.Name == button.Name) && button.Name == "ChangesBranch" && !git.IsRepository).ToArray()) oldSelectors.Children.Remove(existing);
        foreach (var toggle in oldToolbar.Children.OfType<ToggleButton>())
        {
            var fresh = nextToolbar.Children.OfType<ToggleButton>().Single(button => button.Name == toggle.Name);
            toggle.IsChecked = fresh.IsChecked; toggle.Background = fresh.Background;
            CopyTexts(toggle, fresh);
        }
        var oldBody = panel.Children[1];
        var nextBody = next.Children[1];
        if (oldBody is ScrollViewer { Content: StackPanel oldRows } && nextBody is ScrollViewer { Content: StackPanel nextRows })
            ReconcileChangeRows(oldRows, nextRows);
        else if (oldBody is TreeView oldTree && nextBody is TreeView nextTree) ReconcileChangeNodes(oldTree.Items, nextTree.Items);
        else
        {
            next.Children.Remove(nextBody);
            panel.Children.Remove(oldBody);
            panel.Children.Add(nextBody);
        }
        changesProjection = nextProjection;
        changeFrames.Clear();
        foreach (var frame in panel.GetLogicalDescendants().OfType<Border>().Where(frame => frame.Tag is ChangeFrameKey))
            changeFrames.Add((frame, ((ChangeFrameKey)frame.Tag!).Change));
        UpdateActiveChangeRows();
    }

    private static void CopyTexts(Control target, Control source)
    {
        var texts = target.GetLogicalDescendants().OfType<TextBlock>().ToArray();
        var incoming = source.GetLogicalDescendants().OfType<TextBlock>().ToArray();
        for (var index = 0; index < Math.Min(texts.Length, incoming.Length); index++)
        { texts[index].Text = incoming[index].Text; texts[index].Foreground = incoming[index].Foreground; }
    }

    private static void ReconcileChangeRows(StackPanel target, StackPanel source)
    {
        var retained = target.Children.Where(control => control.Tag is ChangeFrameKey).ToDictionary(control => (ChangeFrameKey)control.Tag!);
        var desired = source.Children.Select(fresh => fresh.Tag is ChangeFrameKey key ? retained.GetValueOrDefault(key) ?? fresh
            : fresh is TextBlock text ? target.Children.OfType<TextBlock>().FirstOrDefault(old => old.Text == text.Text) ?? (Control)fresh : fresh).ToArray();
        foreach (var old in target.Children.Where(old => !desired.Contains(old)).ToArray()) target.Children.Remove(old);
        for (var index = 0; index < desired.Length; index++)
        {
            var control = desired[index];
            var current = target.Children.IndexOf(control);
            if (current >= 0) { if (current != index) target.Children.Move(current, index); }
            else { source.Children.Remove(control); target.Children.Insert(index, control); }
        }
    }

    private static void ReconcileChangeNodes(ItemCollection target, ItemCollection source)
    {
        var retained = target.OfType<TreeViewItem>().ToDictionary(node => node.Tag!);
        var desired = source.OfType<TreeViewItem>().Select(fresh =>
        {
            var existing = retained.GetValueOrDefault(fresh.Tag!);
            if (existing is null) return fresh;
            if (fresh.Tag is string) { CopyTexts((Control)existing.Header!, (Control)fresh.Header!); ReconcileChangeNodes(existing.Items, fresh.Items); }
            else if (existing.Header is Border oldFrame && fresh.Header is Border nextFrame && !Equals(oldFrame.Tag, nextFrame.Tag))
            { fresh.Header = null; existing.Header = nextFrame; }
            return existing;
        }).ToArray();
        foreach (var old in target.OfType<TreeViewItem>().Where(old => !desired.Contains(old)).ToArray()) target.Remove(old);
        for (var index = 0; index < desired.Length; index++)
        {
            var node = desired[index];
            if (index < target.Count && ReferenceEquals(target[index], node)) continue;
            if (target.Contains(node)) target.Remove(node);
            else source.Remove(node);
            target.Insert(index, node);
        }
    }
}