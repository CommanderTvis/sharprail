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
    // The connection generation this window's content was read under; the first needs no second read.
    private int hydratedGeneration = 1;
    // The project this window is closing itself; the broadcast of that close must not navigate again.
    private string? closingProject;
    // Workspaces removed while this window lives: a read still in flight must not bring their local state back.
    private readonly HashSet<string> removedWorkspaces = [];
    private string syncedProject = "";

    /// <summary>Has the host ensure and re-sync a project's registry; the rail follows the broadcast, never this call.</summary>
    private async Task SyncWorkspacesAsync(string project)
    {
        try { await Task.Run(async () => await host.ListWorkspacesAsync(project, lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Console.Error.WriteLine("Workspaces could not be listed: " + error.Message); }
    }

    /// <summary>Forgets everything this window kept for a removed workspace; the view of one still shown goes once it is left.</summary>
    private void DropWorkspaceState(string path)
    {
        removedWorkspaces.Add(path);
        selectionHistory.Remove(path);
        if (profile.Data.GitSelections.Remove(path)) SaveProfile();
        Layout.DropWorkspace(path);
    }

    private void WireHostSync()
    {
        hydratedGeneration = Math.Max(1, state.Generation);
        state.Changed += SharedStateChanged;
        state.ConnectionChanged += ConnectionChanged;
        state.HandshakeChanged += HandshakeChanged;
        Closed += (_, _) => { state.Changed -= SharedStateChanged; state.ConnectionChanged -= ConnectionChanged; state.HandshakeChanged -= HandshakeChanged; };
    }

    /// <summary>Applies the capability gates of the connected host and detects one built for another protocol version.</summary>
    private void HandshakeChanged(HostHandshake? handshake)
    {
        var canRevert = state.Supports(HostProtocol.ChangeWritePath);
        foreach (var view in documentContent.Values.OfType<Rendering.DiffView>()) view.CanRevert = canRevert;
        if (handshake is null || handshake.ProtocolVersion == HostProtocol.Current) return;
        Console.Error.WriteLine($"Host protocol {handshake.ProtocolVersion} ({handshake.HostVersion}) differs from this app's {HostProtocol.Current}.");
        ShowNotification(handshake.ProtocolVersion > HostProtocol.Current
            ? "The host is newer than this app. Update the app to use every feature."
            : "The host is older than this app. Some features may be unavailable until it is updated.");
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
        var registry = !next.Workspaces.SequenceEqual(previous.Workspaces);
        var rail = RailSignature() != railSignature || !next.Projects.SequenceEqual(previous.Projects) || !next.RecentProjects.SequenceEqual(previous.RecentProjects) ||
            next.WorkspaceLabels.Count != previous.WorkspaceLabels.Count ||
            next.WorkspaceLabels.Any(entry => previous.WorkspaceLabels.GetValueOrDefault(entry.Key) != entry.Value);
        if (rail)
            KeepingFocus(() =>
            {
                UpdateScopeLabels();
                if (toolContent.Remove("projects")) surface.RefreshContents("projects");
                if (atHome || cleanWelcome) surface.RefreshContents();
            });
        else if (registry) UpdateRailSelection();
        if (WorkspaceMounted && (!ReferenceEquals(next.WorkspaceDiffBases, previous.WorkspaceDiffBases) || !ReferenceEquals(next.WorkspaceBases, previous.WorkspaceBases)))
            _ = RefreshWorkspaceStatsAsync(projectRequest);
        // Another client re-pointed this workspace's review target: follow it, as a local choice would.
        if (WorkspaceMounted && !atHome && next.DiffBase(workspaceRoot) is { Length: > 0 } target &&
            target != previous.DiffBase(workspaceRoot) && target != comparison)
        {
            comparison = target;
            RetargetDiffTabs();
            SaveGitSelection();
            _ = RefreshGitAsync(projectRequest);
        }
        // Another client closed this window's project: move on as a local close would.
        if (projectRoot.Length > 0 && projectRoot != closingProject && previous.Projects.Contains(projectRoot) && !next.Projects.Contains(projectRoot))
        {
            if (next.Projects.FirstOrDefault() is { } other) _ = OpenProjectHomeAsync(other);
            else ShowWelcome();
            return;
        }
        if (!registry) return;
        var paths = next.Workspaces.Select(workspace => workspace.Path).ToHashSet();
        removedWorkspaces.ExceptWith(paths);
        var gone = previous.Workspaces.Where(workspace => !paths.Contains(workspace.Path)).ToArray();
        var shown = WorkspaceMounted && !atHome && gone.Any(workspace => workspace.Path == workspaceRoot);
        var name = shown ? previous.WorkspaceLabels.GetValueOrDefault(workspaceRoot) ?? DirectoryName(workspaceRoot) : "";
        foreach (var workspace in gone) DropWorkspaceState(workspace.Path);
        if (shown)
        {
            _ = OpenProjectHomeAsync(projectRoot);
            ShowNotification($"{name} was removed.");
        }
        else if (WorkspaceMounted && !previous.WorkspacesOf(projectRoot).Select(workspace => workspace.Path)
            .SequenceEqual(next.WorkspacesOf(projectRoot).Select(workspace => workspace.Path))) _ = RefreshGitAsync(projectRequest);
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

    /// <summary>
    /// Each new connection generation re-reads what this window shows, once: a failed startup restore is
    /// retried, or the mounted workspace is watched and read again. A deferred rename is then finished.
    /// </summary>
    private void ConnectionChanged(bool connected)
    {
        if (!connected)
        {
            if (remote && status.Text is "Remote" or "Connected") status.Text = "Reconnecting";
            return;
        }
        var generation = state.Generation;
        if (restorePending) _ = StartAsync();
        else if (generation != hydratedGeneration && WorkspaceMounted)
        {
            StartWatching(projectRequest);
            _ = RefreshAsync();
        }
        hydratedGeneration = generation;
        if (renameCommitPending) CommitRename();
    }
}