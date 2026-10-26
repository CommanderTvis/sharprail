using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.UI.Panels;
using SharpRail.UI.Terminal;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    // Live terminals whose removal was confirmed for the transition being retried.
    private readonly HashSet<string> approvedTerminalCloses = [];
    private bool askingToCloseTerminal;
    internal bool ConfirmEveryTerminalClose { get; set; } = OperatingSystem.IsAndroid();
    // Workspaces this window has told the host it shows; their terminal tabs follow the host's catalog.
    private readonly HashSet<string> sharedWorkspaces = [];
    private readonly HashSet<string> sharingWorkspaces = [];
    // Workspaces whose terminal tabs may attach: reconciled with the catalog, or left this window's own without one.
    private readonly HashSet<string> terminalWorkspaces = [];
    // Terminal tabs created here that the host has not recorded yet; the catalog cannot remove them meanwhile.
    private readonly HashSet<string> reservingTerminals = [];
    // Terminal tabs created here: their first attach takes the session, where every other one yields to its holder.
    private readonly HashSet<string> ownTerminals = [];
    // Commands a plugin asked to run in a terminal it opened: the new shell runs each as its program.
    private readonly Dictionary<string, string> terminalCommands = [];
    private Dictionary<string, Dictionary<string, string>> knownTerminals = [];
    private bool applyingTerminalCatalog;
    // Terminal tabs created here, by the terminal they were opened from: its shell's directory is where theirs starts.
    private readonly Dictionary<string, string> terminalSources = [];
    // A terminal chosen before this window's layout had it.
    private (string Workspace, string Tab)? awaitedTerminal;

    private Dictionary<string, Dictionary<string, string>> TerminalsByWorkspace() => Layout.State.Workspaces
        .Where(workspace => !workspace.Key.StartsWith("home:", StringComparison.Ordinal))
        .ToDictionary(workspace => workspace.Key, workspace => workspace.Value.Documents.Values.SelectMany(tabs => tabs)
            .Where(tab => tab.Kind == "terminal").ToDictionary(tab => tab.Id, tab => tab.Title));

    private void WireTerminalCatalog()
    {
        surface.RenameTerminal = RenameTerminal;
        knownTerminals = TerminalsByWorkspace();
        workbench.TerminalCatalogChanged += ApplyTerminalCatalog;
        Closed += (_, _) => workbench.TerminalCatalogChanged -= ApplyTerminalCatalog;
    }

    private async void RenameTerminal(string workspace, Docking.DockTab tab)
    {
        var result = await Dialogs.Prompt(this, "Rename terminal", ("Name", tab.Title));
        if (result is not [var title] || title.Length == 0 || title == tab.Title) return;
        if (title.Length > TerminalTab.MaxTitleLength)
        {
            Report(new ArgumentException("A terminal name can have at most 1000 characters."));
            return;
        }
        if (Layout.State.Workspaces.GetValueOrDefault(workspace)?.Documents.Values.SelectMany(tabs => tabs)
            .Any(known => known.Kind == "terminal" && known.Id == tab.Id) != true &&
            workbench.TerminalCatalog?.Workspaces.GetValueOrDefault(workspace)?.Any(known => known.Key == tab.Id) != true) return;
        await ShareAsync(HostStateChange.TitleTerminal(new TerminalRef(workspace, tab.Id), title));
    }

    private bool TerminalMayAttach(string workspace, string tabId) =>
        atHome || terminalWorkspaces.Contains(workspace) && !reservingTerminals.Contains(workspace + ":" + tabId);

    private TerminalLaunch LaunchTerminal(string workspace, string tabId) =>
        new(workspace, TerminalLaunch.SessionFor(workspace, tabId), Path.Combine(profile.DirectoryPath, "clipboard"), terminalClient)
        {
            TabKey = tabId,
            Yield = !ownTerminals.Remove(workspace + ":" + tabId),
            Program = terminalCommands.Remove(workspace + ":" + tabId, out var program) ? program : null,
            DirectoryFrom = terminalSources.Remove(workspace + ":" + tabId, out var source) ? source : null,
            ResolveCommitLinks = (text, token) => ResolveTerminalCommits(workspace, text, token),
            OpenFileLink = link => OpenTerminalLinkAsync(workspace, link)
        };

    internal async Task OpenTerminalLinkAsync(string workspace, Ghostty.Avalonia.TerminalLink link)
    {
        if (workspaceRoot != workspace || !WorkspaceMounted || atHome) return;
        if (TerminalCommitLinks.Commit(link) is { } sha) { OpenCommitChanges(workspace, sha); return; }
        if (link.IsDirectory)
        {
            // The host already said it is a folder. One inside the workspace is focused in Files; any other opens the
            // host folder browser (it lists only files to pick), and the picked file opens like any other link.
            var folder = link.Path.TrimEnd('/', '\\');
            var inside = folder == workspace.TrimEnd('/', '\\') ? "" : HostPaths.Relative(workspace, folder).Replace('\\', '/');
            if (inside.Length == 0 || inside != folder.Replace('\\', '/'))
            {
                await RevealFolderAsync(workspace, inside);
                return;
            }
            if (await ChooseHostPathAsync(link.Path, "Open file from folder") is not { } picked || workspaceRoot != workspace) return;
            await OpenDocumentAsync(HostPaths.Relative(workspace, picked), true, raw: true);
            return;
        }
        var path = HostPaths.Relative(workspace, link.Path);
        await OpenDocumentAsync(path, true, line: link.Line, raw: true, column: link.Column);
    }

    /// <summary>Shows the Files pane and selects a workspace-relative folder in it ("" is the workspace root), listing its ancestors in the background first.</summary>
    private async Task RevealFolderAsync(string workspace, string folder)
    {
        var request = projectRequest;
        RevealFiles();
        List<string> parents = [];
        for (var parent = HostPaths.Parent(folder); folder.Length > 0 && parent.Length > 0; parent = HostPaths.Parent(parent)) parents.Add(parent);
        foreach (var parent in parents) expandedFolders.Add(parent);
        if (parents.Count > 0) await RefreshFilesAsync(request, parents);
        if (workspace != workspaceRoot || request != projectRequest || !WorkspaceMounted) return;
        if (toolContent.GetValueOrDefault("files")?.GetLogicalDescendants().OfType<DrawnTree>().FirstOrDefault() is not { } tree) return;
        foreach (var parent in parents.AsEnumerable().Reverse())
            if (tree.Rows.FirstOrDefault(entry => entry.Tag is ProjectFile { IsDirectory: true } item && item.Path == parent) is { } row) tree.Expand(row, true);
        if (folder.Length > 0 && tree.Rows.FirstOrDefault(entry => entry.Tag is ProjectFile { IsDirectory: true } item && item.Path == folder) is { } target) tree.Reveal(target);
    }

    private Task<IReadOnlyList<Ghostty.Avalonia.TerminalLink>> ResolveTerminalCommits(string workspace, string text, CancellationToken token)
        => Task.Run(async () =>
        {
            if (workspaceRoot != workspace || !WorkspaceMounted || atHome) return (IReadOnlyList<Ghostty.Avalonia.TerminalLink>)[];
            if (!TerminalCommitLinks.HasCandidates(text)) return (IReadOnlyList<Ghostty.Avalonia.TerminalLink>)[];
            var request = projectRequest;
            var project = workbench.NewSession();
            try
            {
                if (project is not null) await project.OpenProjectAsync(workspace, token);
                var links = await TerminalCommitLinks.Resolve(project ?? host, text, token);
                token.ThrowIfCancellationRequested();
                return workspaceRoot == workspace && WorkspaceMounted && !atHome && request == projectRequest ? links : [];
            }
            finally { (project as IDisposable)?.Dispose(); }
        }, token);

    /// <summary>
    /// Tells the host this window shows a workspace, once per connection. A view that never met the catalog, or
    /// a workspace the host does not know, brings this window's terminal tabs for the host to adopt; otherwise
    /// the catalog decides and the view follows, so a tab closed elsewhere meanwhile is not brought back.
    /// </summary>
    private async void ShareTerminals(string workspace)
    {
        if (sharedWorkspaces.Contains(workspace) || !sharingWorkspaces.Add(workspace)) return;
        try
        {
            if (!await workbench.TerminalCatalogStarted) throw new NotSupportedException();
            var brings = Layout.State.Workspaces.GetValueOrDefault(workspace) is { TerminalsShared: false } ||
                workbench.TerminalCatalog?.Workspaces.ContainsKey(workspace) != true;
            TerminalTab[] own = brings && knownTerminals.GetValueOrDefault(workspace) is { } tabs ? [.. tabs.Select(tab => new TerminalTab(tab.Key, tab.Value))] : [];
            var catalog = await Task.Run(async () => await workbench.TerminalTabs.OpenWorkspaceAsync(workspace, own, lifetime.Token), lifetime.Token);
            sharedWorkspaces.Add(workspace);
            workbench.OfferTerminalCatalog(catalog);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            // Without a catalog (an older host, or one that refused these tabs) the terminals stay this window's own.
            if (error is not NotSupportedException) Console.Error.WriteLine("Terminals could not be shared: " + error.Message);
            if (terminalWorkspaces.Add(workspace)) RefreshTerminals(workspace);
            RevealAwaitedTerminal();
            return;
        }
        finally { sharingWorkspaces.Remove(workspace); }
        ApplyTerminalCatalog();
    }

    /// <summary>A new connection may be a restarted host: every workspace is announced again when next shown.</summary>
    private void ReshareTerminals()
    {
        sharedWorkspaces.Clear();
        if (WorkspaceMounted && !atHome) ShareTerminals(workspaceRoot);
    }

    // Tabs closed elsewhere go, tabs opened elsewhere arrive without taking selection or focus.
    private void ApplyTerminalCatalog()
    {
        if (workbench.TerminalCatalog is not { } catalog || lifetime.IsCancellationRequested) return;
        string? ready = null;
        // A focused terminal the catalog removes (its shell exited, or another client closed it) hands focus on within its group.
        var focusedGroup = FocusedTerminalGroup();
        applyingTerminalCatalog = true;
        try
        {
            foreach (var workspace in sharedWorkspaces)
            {
                if (!catalog.Workspaces.TryGetValue(workspace, out var tabs)) continue;
                var prefix = workspace + ":";
                if (terminalWorkspaces.Add(workspace)) ready = workspace;
                Layout.ReconcileTerminals(workspace, tabs, reservingTerminals.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(key => key[prefix.Length..]).ToHashSet(), state.Current.TerminalTitles
                        .Where(title => title.Terminal.WorkspaceId == workspace).ToDictionary(title => title.Terminal.TabKey, title => title.Title));
            }
        }
        finally { applyingTerminalCatalog = false; }
        if (ready is not null) RefreshTerminals(workspaceRoot);
        if (focusedGroup is var (group, tab) && Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.Values.SelectMany(tabs => tabs).Any(item => item.Id == tab) != true)
            surface.FocusGroupContent(Layout.State.Groups.Any(item => item.Id == group) ? group : Layout.View.FocusedCenter);
        PlaceWorkspaceTabs();
        RevealAwaitedTerminal();
    }

    private (string Group, string Tab)? FocusedTerminalGroup()
    {
        if (!WorkspaceMounted || atHome || Layout.State.Workspaces.GetValueOrDefault(workspaceRoot) is not { } view) return null;
        foreach (var (group, tabs) in view.Documents)
            foreach (var tab in tabs)
                if (tab.Kind == "terminal" && documentContent.GetValueOrDefault(workspaceRoot + ":" + tab.Id) is TerminalView { IsKeyboardFocusWithin: true })
                    return (group, tab.Id);
        return null;
    }

    private void RevealAwaitedTerminal()
    {
        if (awaitedTerminal is { } awaited && WorkspaceMounted && !atHome && awaited.Workspace == workspaceRoot && terminalWorkspaces.Contains(workspaceRoot) &&
            knownTerminals.GetValueOrDefault(workspaceRoot)?.ContainsKey(awaited.Tab) == true)
            _ = RevealTerminalAsync(awaited.Workspace, awaited.Tab);
    }

    // Mounts the bodies of terminal tabs that waited for the host, leaving every other content as it is.
    private void RefreshTerminals(string workspace, string? tabId = null)
    {
        if (workspace != workspaceRoot || !WorkspaceMounted || atHome) return;
        string[] tabs = tabId is not null ? [tabId] : knownTerminals.GetValueOrDefault(workspace) is { } known ? [.. known.Keys] : [];
        if (tabs.Length > 0) surface.RefreshContents(tabs);
    }

    // Every layout change: a terminal tab created here is reserved with the host before its shell is shown, and
    // one closed here is closed for every client. Changes the catalog made itself are only recorded.
    private void TrackTerminalTabs()
    {
        var previous = knownTerminals;
        knownTerminals = TerminalsByWorkspace();
        if (applyingTerminalCatalog) return;
        foreach (var (workspace, tabs) in knownTerminals)
        {
            // A workspace seen for the first time brings its tabs when it is shared.
            if (!previous.TryGetValue(workspace, out var before)) continue;
            foreach (var (id, title) in tabs.Where(tab => !before.ContainsKey(tab.Key)))
            {
                // A new tab starts where the terminal it was opened from is, as Ghostty's inherit-working-directory does.
                if (Layout.TakeTerminalSource(workspace, id) is { } source && before.ContainsKey(source))
                    terminalSources[workspace + ":" + id] = TerminalLaunch.SessionFor(workspace, source);
                _ = ReserveTerminalAsync(workspace, new(id, title));
            }
            foreach (var id in before.Keys.Where(id => !tabs.ContainsKey(id))) _ = CloseTerminalAsync(workspace, id, undoableTerminalCloses.Contains(workspace + ":" + id));
        }
    }

    private async Task ReserveTerminalAsync(string workspace, TerminalTab tab)
    {
        var key = workspace + ":" + tab.Key;
        reservingTerminals.Add(key); ownTerminals.Add(key);
        try
        {
            var catalog = await Task.Run(async () => await workbench.TerminalTabs.ReserveAsync(workspace, tab, lifetime.Token), lifetime.Token);
            reservingTerminals.Remove(key);
            workbench.OfferTerminalCatalog(catalog);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            reservingTerminals.Remove(key);
            // A host without a catalog keeps the tab this window's own; a refused tab goes with the next reconciliation.
            if (error is not NotSupportedException)
            {
                ownTerminals.Remove(key); terminalCommands.Remove(key); terminalSources.Remove(key);
                Report(new IOException("The host could not open this terminal: " + error.Message));
                ApplyTerminalCatalog();
            }
        }
        RefreshTerminals(workspace, tab.Key);
    }

    // An undoable close leaves the host session running, detached, for the undo timeout.
    private async Task CloseTerminalAsync(string workspace, string tabId, bool undoable = false)
    {
        ownTerminals.Remove(workspace + ":" + tabId); terminalCommands.Remove(workspace + ":" + tabId); terminalSources.Remove(workspace + ":" + tabId);
        try
        {
            var catalog = await Task.Run(async () => undoable
                ? await workbench.TerminalTabs.CloseTabForUndoAsync(workspace, tabId, lifetime.Token)
                : await workbench.TerminalTabs.CloseTabAsync(workspace, tabId, lifetime.Token), lifetime.Token);
            workbench.OfferTerminalCatalog(catalog);
        }
        catch (Exception error) when (error is OperationCanceledException or NotSupportedException) { }
        catch (Exception error) { Report(new IOException("The host could not close this terminal: " + error.Message)); }
    }

    private TerminalView? LiveTerminal(string workspace, string tabId) =>
        documentContent.GetValueOrDefault(workspace + ":" + tabId) is TerminalView { IsExited: false, IsFailed: false } view ? view : null;

    private bool TerminalMayClose(string workspace, string tabId) =>
        approvedTerminalCloses.Contains(workspace + ":" + tabId) ||
        !(ConfirmEveryTerminalClose && Layout.State.Workspaces.GetValueOrDefault(workspace)?.Documents.Values
            .SelectMany(tabs => tabs).Any(tab => tab.Id == tabId && tab.Kind == "terminal") == true ||
          LiveTerminal(workspace, tabId) is not null);

    // Returns whether every blocked terminal may now close.
    private async Task<bool> ResolveTerminalsAsync(IReadOnlyList<(string Workspace, string TabId)> blocked)
    {
        var live = blocked.Where(item => !TerminalMayClose(item.Workspace, item.TabId)).ToArray();
        if (live.Length == 0) return true;
        if (askingToCloseTerminal) return false;
        askingToCloseTerminal = true;
        try
        {
            var busy = 0;
            if (!ConfirmEveryTerminalClose)
                foreach (var (workspace, tabId) in live)
                    if (LiveTerminal(workspace, tabId) is { } view && await view.IsBusyAsync()) busy++;
            var count = ConfirmEveryTerminalClose ? live.Length : busy;
            if (count > 0 && !await Dialogs.Confirm(this, count == 1 ? "Close this terminal?" : $"Close {count} terminals?",
                    ConfirmEveryTerminalClose ? "Closing ends the terminal session for all clients." :
                        "A process is still running. Closing the tab terminates it.", "Close terminal", "TerminalCloseBusyConfirm"))
                return false;
            foreach (var (workspace, tabId) in live) approvedTerminalCloses.Add(workspace + ":" + tabId);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Report(error);
            return false;
        }
        finally { askingToCloseTerminal = false; }
    }
}