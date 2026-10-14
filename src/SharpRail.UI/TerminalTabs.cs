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
    // Commands a plugin asked to run in a terminal it opened, typed once its shell has started.
    private readonly Dictionary<string, string> terminalCommands = [];
    private Dictionary<string, Dictionary<string, string>> knownTerminals = [];
    private bool applyingTerminalCatalog;
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
            OpenFileLink = async link =>
            {
                if (workspaceRoot != workspace || !WorkspaceMounted) return;
                var path = HostPaths.Relative(workspace, link.Path);
                await OpenDocumentAsync(path, true, line: link.Line, raw: true, column: link.Column);
            }
        };

    private static async Task TypeWhenStartedAsync(TerminalView terminal, string command)
    {
        if (terminal.Backend is not { } backend) return;
        try { await backend.Started; } catch (Exception) { return; }
        terminal.Write(command + "\r");
    }

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
        PlaceWorkspaceTabs();
        RevealAwaitedTerminal();
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
            foreach (var (id, title) in tabs.Where(tab => !before.ContainsKey(tab.Key))) _ = ReserveTerminalAsync(workspace, new(id, title));
            foreach (var id in before.Keys.Where(id => !tabs.ContainsKey(id))) _ = CloseTerminalAsync(workspace, id);
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
                ownTerminals.Remove(key); terminalCommands.Remove(key);
                Report(new IOException("The host could not open this terminal: " + error.Message));
                ApplyTerminalCatalog();
            }
        }
        RefreshTerminals(workspace, tab.Key);
    }

    private async Task CloseTerminalAsync(string workspace, string tabId)
    {
        ownTerminals.Remove(workspace + ":" + tabId); terminalCommands.Remove(workspace + ":" + tabId);
        try
        {
            var catalog = await Task.Run(async () => await workbench.TerminalTabs.CloseTabAsync(workspace, tabId, lifetime.Token), lifetime.Token);
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