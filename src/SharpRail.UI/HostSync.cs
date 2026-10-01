using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

/// <summary>Applies the host's shared-state snapshots and connection changes to this window.</summary>
public sealed partial class WorkbenchWindow
{
    // A startup restore failed and retries when the host's subscription is (re)established.
    private bool restorePending;
    private bool hostLost;
    // The project this window is closing itself; the broadcast of that close must not navigate again.
    private string? closingProject;

    private void WireHostSync()
    {
        state.Changed += SharedStateChanged;
        state.ConnectionChanged += ConnectionChanged;
        Closed += (_, _) => { state.Changed -= SharedStateChanged; state.ConnectionChanged -= ConnectionChanged; };
    }

    /// <summary>Sends shared changes to the host, reporting a failure in this window.</summary>
    private async Task<bool> ShareAsync(params HostStateChange[] changes)
    {
        try { await state.ChangeAsync(changes); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { Report(new IOException("The host could not save this change: " + error.Message)); return false; }
    }

    private void SharedStateChanged(HostState previous, HostState next)
    {
        if (next.Settings != previous.Settings) RefreshAppearance();
        var rail = !next.Projects.SequenceEqual(previous.Projects) || !next.RecentProjects.SequenceEqual(previous.RecentProjects) ||
            next.WorkspaceLabels.Count != previous.WorkspaceLabels.Count ||
            next.WorkspaceLabels.Any(entry => previous.WorkspaceLabels.GetValueOrDefault(entry.Key) != entry.Value);
        if (rail)
            KeepingFocus(() =>
            {
                UpdateScopeLabels();
                if (toolContent.Remove("projects")) surface.RefreshContents("projects");
                if (atHome || cleanWelcome) surface.RefreshContents();
            });
        // Another client closed this window's project: move on as a local close would.
        if (projectRoot.Length > 0 && projectRoot != closingProject && previous.Projects.Contains(projectRoot) && !next.Projects.Contains(projectRoot))
        {
            if (next.Projects.FirstOrDefault() is { } other) _ = OpenProjectHomeAsync(other);
            else ShowWelcome();
            return;
        }
        if (projectRoot.Length == 0 || next.Workspaces.GetValueOrDefault(projectRoot) is not { } workspaces ||
            previous.Workspaces.GetValueOrDefault(projectRoot) is { } known && known.SequenceEqual(workspaces)) return;
        if (WorkspaceMounted && !atHome && workspaceRoot != projectRoot && !workspaces.Contains(workspaceRoot))
        {
            var name = previous.WorkspaceLabels.GetValueOrDefault(workspaceRoot) ?? DirectoryName(workspaceRoot);
            selectionHistory.Remove(workspaceRoot);
            _ = OpenProjectHomeAsync(projectRoot);
            ShowNotification($"{name} was removed.");
        }
        else if (WorkspaceMounted) _ = RefreshGitAsync(projectRequest);
    }

    /// <summary>
    /// Rebuilding panels replaces their controls; keep keyboard focus on the equivalent control of this
    /// window, identified by name and tag. Rename inputs manage their own focus.
    /// </summary>
    private void KeepingFocus(Action rebuild)
    {
        var focused = FocusManager?.GetFocusedElement() is Control current && TopLevel.GetTopLevel(current) == this ? current : null;
        rebuild();
        if (focused is not { Name: { } name } || focused is TextBox || focused.IsAttachedToVisualTree()) return;
        Dispatcher.UIThread.Post(() => surface.GetLogicalDescendants().OfType<Control>()
            .FirstOrDefault(control => control.Name == name && Equals(control.Tag, focused.Tag))?.Focus());
    }

    /// <summary>After a reconnect, retry a failed startup restore or refresh the mounted workspace, and finish a deferred rename.</summary>
    private void ConnectionChanged(bool connected)
    {
        if (!connected)
        {
            hostLost = true;
            if (remote && status.Text is "Remote" or "Connected") status.Text = "Reconnecting";
            return;
        }
        if (restorePending) _ = StartAsync();
        else if (hostLost && WorkspaceMounted) _ = RefreshAsync();
        hostLost = false;
        if (renameCommitPending) CommitRename();
    }
}