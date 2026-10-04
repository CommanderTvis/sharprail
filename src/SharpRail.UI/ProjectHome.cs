using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private readonly List<string> selectionHistory = [];
    private bool atHome;
    private bool cleanWelcome;
    private bool creatingWorkspace;
    private TextBlock? readyBranch;
    private string? renaming;
    private string renameDraft = "";
    private string renameOriginal = "";
    private bool renameCommitting;
    private bool renameCommitPending;
    private TextBox? renameBox;
    private IReadOnlyList<EditorInfo>? editors;

    public bool AtProjectHome => atHome;
    public bool ShowsWelcome => atHome || cleanWelcome;
    public string ProjectRoot => projectRoot;

    private static string HomeKey(string project) => "home:" + project;

    private static string DirectoryName(string path) => new DirectoryInfo(path).Name;

    private string WorkspaceName(string path) => path == projectRoot || state.Current.Projects.Contains(path) ? "Default" :
        state.Current.WorkspaceLabels.GetValueOrDefault(path) ?? DirectoryName(path);

    private string ReadyBranchText() => workspaceRoot == projectRoot ? "on " + branchLabel.Text :
        branchLabel.Text + (comparison.Length > 0 ? " · from " + comparison : "");

    /// <summary>"from main" is a word and a branch with nothing joining them; hovering says both things it means.</summary>
    private void UpdateReadyBranch()
    {
        if (readyBranch is null) return;
        readyBranch.Text = ReadyBranchText();
        ToolTip.SetTip(readyBranch, workspaceRoot != projectRoot && comparison.Length > 0
            ? $"This workspace was cut from {comparison}, and its changes are measured against it."
            : null);
    }

    private async Task StartAsync()
    {
        var data = slot;
        // A failed restore keeps the remembered location and retries when the host reconnects.
        restorePending = false;
        if (initialRoot.Length > 0 && (remote || Directory.Exists(initialRoot)))
        {
            if (data.LastAtHome && initialRoot == data.LastProjectRoot) await OpenProjectHomeAsync(initialRoot);
            else await OpenProjectAsync(initialRoot);
        }
        else if (data.LastProjectRoot.Length > 0 && (remote || Directory.Exists(data.LastProjectRoot))) await OpenProjectHomeAsync(data.LastProjectRoot);
        else ShowWelcome();
        restorePending = !WorkspaceMounted && !cleanWelcome;
        if (StartLink is { } link && !restorePending) { StartLink = null; await NavigateAsync(link); }
    }

    public Task OpenProjectHomeAsync(string project) => OpenWorkspaceAsync(project, true, home: true);

    private void ShowWelcome()
    {
        RememberGitSelection();
        projectRequest++; gitRefresh?.Cancel(); StopWatching();
        switchingWorkspace = false;
        WorkspaceMounted = false; atHome = false; cleanWelcome = true;
        projectRoot = ""; workspaceRoot = "";
        git = new(false, "", [], [], []); gitLoading = false; gitError = null;
        folderCache.Clear(); expandedFolders.Clear(); toolContent.Clear(); selectionHistory.Clear();
        slot.LastProject = ""; slot.LastProjectRoot = ""; slot.LastAtHome = false; restorePending = false; syncedProject = "";
        SaveProfile();
        UpdateScopeLabels();
        SetBranch("");
        status.Text = remote ? "Remote" : "Connected";
        ShowReady();
        Layout.SwitchWorkspace("");
        surface.RefreshCenterActions();
        surface.RefreshContents();
        LocationChanged?.Invoke();
    }

    private void UpdateScopeLabels()
    {
        // A header rename belongs to the workspace it started on; leaving that workspace abandons it.
        if (renameInHeader && renaming is not null && (atHome || renaming != workspaceRoot))
        { renaming = null; renameBox = null; renameCommitPending = false; }
        projectLabel.Text = projectRoot.Length == 0 ? "SharpRail" : DirectoryName(projectRoot);
        workspaceLabel.Text = projectRoot.Length == 0 ? "" : atHome ? "Project home" : WorkspaceName(workspaceRoot);
        this.FindControl<Border>("WorkspaceSegment")!.IsVisible = projectRoot.Length > 0;
        var project = this.FindControl<Button>("ScopeProject")!;
        var workspace = this.FindControl<Button>("ScopeWorkspace")!;
        AutomationProperties.SetName(project, "Project " + projectLabel.Text);
        AutomationProperties.SetName(workspace, "Workspace " + workspaceLabel.Text);
        var input = this.FindControl<ContentControl>("ScopeRename")!;
        var editing = renameInHeader && renaming is not null;
        workspace.IsVisible = !editing; input.IsVisible = editing;
        if (!editing) input.Content = null;
        else input.Content ??= RenameBox(new Thickness(0), 22);
        UpdateBranchSegment();
    }

    private Control Welcome()
    {
        var panel = new StackPanel
        {
            Name = "Welcome",
            Spacing = 12,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var clean = !atHome;
        if (!clean)
        {
            var scope = Ui.Text("PROJECT HOME", size: 12);
            scope.Name = "WelcomeScope";
            panel.Children.Add(scope);
        }
        var title = Ui.Text(clean ? "SharpRail" : DirectoryName(projectRoot), Ui.TextBrush, 24);
        title.Name = "WelcomeTitle";
        panel.Children.Add(title);
        // The reference's screen is heading, then one to three cards; no pitch prose.
        var buttons = new WrapPanel { Name = "WelcomeCards", Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 12 };
        welcomeCards = buttons;
        FillWelcomeCards(buttons);
        panel.Children.Add(buttons);
        foreach (var child in panel.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
        return panel;
    }

    private WrapPanel? welcomeCards;
    private bool welcomeGitReady;

    private void FillWelcomeCards(WrapPanel buttons)
    {
        welcomeGitReady = !gitLoading && gitError is null && git.IsRepository;
        buttons.Children.Clear();
        if (!atHome)
        {
            var open = new Panels.WelcomeCard("folderFill", "Open project", "Choose a local folder to work in.", primary: true) { Name = "WelcomeCta" };
            open.ContextMenu = ProjectMenu();
            open.Click += (_, _) => open.ContextMenu.Open(open);
            buttons.Children.Add(open);
        }
        else if (!projectSpecs.TryGetValue(projectRoot, out var hasSpecs))
        {
            // Which card leads depends on whether the project has specs; the cards wait for that answer.
            if (WorkspaceMounted) _ = ProbeSpecsAsync();
        }
        else
        {
            if (!hasSpecs && welcomeGitReady)
            {
                var setUp = new Panels.WelcomeCard("bookFill", "Set up project", "Draft the project's specs, starting from its goal, in an isolated workspace.", primary: true)
                { Name = "WelcomeCta" };
                setUp.Click += (_, _) => _ = CreateWorkspaceDialogAsync();
                buttons.Children.Add(setUp);
            }
            var create = new Panels.WelcomeCard("add", "Create workspace", $"An isolated worktree on its own branch ({Shortcut("N")}).", primary: hasSpecs)
            { Name = hasSpecs ? "WelcomeCta" : "WelcomeAction", IsVisible = welcomeGitReady };
            create.Click += (_, _) => _ = CreateWorkspaceDialogAsync();
            var folder = new Panels.WelcomeCard("homeFill", "Work in project folder", "Changes and terminals run directly in your project folder — no isolation.",
                primary: false)
            { Name = "WelcomeAction" };
            folder.Click += (_, _) => _ = OpenWorkspaceAsync(projectRoot, false);
            buttons.Children.Add(create); buttons.Children.Add(folder);
            AddProjectActions(buttons);
        }
    }

    // Whether each project shown at its home has durable specs; absent while the first answer is on its way.
    private readonly Dictionary<string, bool> projectSpecs = [];
    private readonly HashSet<string> specProbes = [];

    /// <summary>Asks the host once per change whether the project at home has specs, then refills the Welcome cards if the answer moved.</summary>
    private async Task ProbeSpecsAsync()
    {
        var project = projectRoot;
        if (!atHome || !specProbes.Add(project)) return;
        bool has;
        try { has = await Task.Run(async () => await host.HasDurableSpecsAsync(lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        // Without an answer Welcome offers the ordinary fork rather than a suggestion it cannot justify.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Grpc.Core.RpcException) { has = true; }
        finally { specProbes.Remove(project); }
        // The session may have moved to a workspace or another project meanwhile; its answer would be about that root.
        if (!atHome || projectRoot != project || lifetime.IsCancellationRequested) return;
        if (projectSpecs.TryGetValue(project, out var known) && known == has) return;
        projectSpecs[project] = has;
        // The empty center is not a tab the surface re-renders, so the mounted cards are refilled in place.
        if (welcomeCards is { } cards && cards.IsAttachedToVisualTree()) FillWelcomeCards(cards);
    }

    private static string Shortcut(string key) => (OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+") + key;

    private void UpdateProjectHomeActions()
    {
        if (atHome && welcomeCards is { } cards && welcomeGitReady != (!gitLoading && gitError is null && git.IsRepository))
            FillWelcomeCards(cards);
    }

    private ContextMenu ProjectMenu()
    {
        var menu = new ContextMenu();
        var create = Ui.Menu("Create project…", () => _ = CreateProjectAsync());
        create.Name = "CreateProjectMenu";
        menu.Items.Add(create);
        menu.Items.Add(Ui.Menu("Open project", () => _ = PickProjectAsync()));
        menu.Items.Add(Ui.Menu(remote ? "Enter host path…" : "Enter path…", () => _ = EnterHostPathAsync(null)));
        var clone = Ui.Menu("Clone repository…", () => _ = CloneProjectAsync());
        clone.Name = "CloneProjectMenu";
        menu.Items.Add(clone);
        var recents = state.Current.RecentProjects.Where(path => !state.Current.Projects.Contains(path)).ToArray();
        if (recents.Length == 0) return menu;
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Recent", IsEnabled = false });
        foreach (var path in recents)
        {
            var item = Ui.Menu(path, () => _ = OpenPickedProjectAsync(path));
            item.Name = "RecentProject";
            menu.Items.Add(item);
        }
        return menu;
    }

    private ContextMenu ProjectActions(string project)
    {
        var menu = new ContextMenu { Name = "ProjectActions" };
        var create = Ui.Menu("Start work", () => _ = CreateWorkspaceForAsync(project));
        create.Name = "ProjectMenuCreateWorkspace"; create.Icon = Ui.Icon("add", null, 14);
        // Copying neither selects the project nor activates a workspace.
        var copy = Ui.Menu("Copy absolute path", () => _ = CopyTextAsync(project));
        copy.Name = "ProjectMenuCopyPath";
        var close = Ui.Menu("Close project", () => _ = CloseProjectAsync(project));
        close.Name = "ProjectMenuClose"; close.Icon = Ui.Icon("close", null, 14);
        var existing = Ui.Menu("Open existing worktree…", () => _ = OpenExistingWorktreeAsync(project));
        existing.Name = "ProjectMenuOpenExisting"; existing.Icon = Ui.Icon("folderOpen", null, 14);
        menu.Items.Add(create);
        menu.Items.Add(existing);
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(close);
        menu.Closed += (_, _) => { if (!creatingWorkspace) FocusProject(project); };
        return menu;
    }

    private void FocusProject(string? project) => Dispatcher.UIThread.Post(() =>
    {
        Control? target = project is null ? null : this.GetLogicalDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "ProjectName" && Equals(button.Tag, project));
        (target ?? this.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "AddProjectMenu"))?.Focus();
    });

    private async Task CreateWorkspaceForAsync(string project)
    {
        if (project != projectRoot || !WorkspaceMounted) await OpenProjectHomeAsync(project);
        await CreateWorkspaceDialogAsync();
        FocusProject(project);
    }

    /// <summary>Attaches a worktree Git already lists and enters it; the dialog stays open while the host answers.</summary>
    private async Task OpenExistingWorktreeAsync(string project)
    {
        creatingWorkspace = true;
        try
        {
            var dialog = new ExistingWorktreeDialog(
                async () => (await host.ListWorkspacesAsync(project, lifetime.Token)).Existing,
                async path => (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Attach(project, path), lifetime.Token))!);
            if (await dialog.ShowAsync(this) is not { } attached) { FocusProject(project); return; }
            if (profile.Data.CollapsedProjects.Remove(project)) SaveProfile();
            removedWorkspaces.Remove(attached.Path);
            await OpenWorkspaceAsync(attached.Path, project != projectRoot);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
        finally { creatingWorkspace = false; }
    }

    private async Task CloseProjectAsync(string project)
    {
        var name = DirectoryName(project);
        // A project whose folder is gone has nothing left to lose, so it closes without asking.
        var missing = !remote && !Directory.Exists(project);
        if (!missing && !await Dialogs.Confirm(this, $"Close {name}?",
            "Removes this project from the open projects list. Its repository and workspaces are kept. Reopen it from Add project → Recents.",
            "Close project"))
        {
            FocusProject(project);
            return;
        }
        closingProject = project;
        try
        {
            if (!await ShareAsync(HostStateChange.CloseProject(project))) { FocusProject(project); return; }
            var next = state.Current.Projects.Where(item => item != project).FirstOrDefault();
            if (project == projectRoot)
            {
                if (next is null) ShowWelcome();
                else await OpenProjectHomeAsync(next);
            }
            else { toolContent.Remove("projects"); surface.RefreshContents("projects"); }
            FocusProject(next);
        }
        finally { closingProject = null; }
    }

    private async Task CreateWorkspaceDialogAsync()
    {
        if (creatingWorkspace || projectRoot.Length == 0 || !WorkspaceMounted) return;
        creatingWorkspace = true;
        // Another project's branches are read through a session of its own, so picking never moves this window.
        IProjectServices? probe = null;
        try
        {
            var request = projectRequest;
            var project = projectRoot;
            BranchCatalog catalog;
            bool hasGit;
            try
            {
                hasGit = !gitLoading && gitError is null ? git.IsRepository
                    : (await Task.Run(async () => await host.GetGitAsync("", lifetime.Token), lifetime.Token)).IsRepository;
                if (request != projectRequest) return;
                catalog = hasGit
                    ? await Task.Run(async () => await host.ListBranchesAsync(false, lifetime.Token), lifetime.Token)
                    : new([], [], "");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                ReportDialogFailure("Couldn't create workspace", error);
                return;
            }
            if (request != projectRequest) return;
            var projects = workbench.CanOpenWindows ? state.Current.Projects : [project];
            var dialog = new NewWorkspaceDialog(projects.Contains(project) ? projects : [project], project, catalog, profile.Data.CollapsedRemotes, SaveProfile, Plugins.LauncherList, hasGit);
            dialog.LoadProject = async picked =>
            {
                try
                {
                    if (picked == project) return await host.ListBranchesAsync(false, lifetime.Token);
                    probe ??= workbench.NewSession();
                    if (probe is null) return null;
                    await probe.OpenProjectAsync(picked, lifetime.Token);
                    return await probe.ListBranchesAsync(false, lifetime.Token);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Report(new IOException("Couldn't create workspace. " + error.Message));
                    return null;
                }
            };
            if (hasGit) _ = PrefetchDefaultAsync(dialog, project);
            var choice = await dialog.ShowAsync(this);
            if (choice is null || request != projectRequest) return;
            if (choice.Project != project)
            {
                await OpenProjectHomeAsync(choice.Project);
                if (!WorkspaceMounted || projectRoot != choice.Project) return;
            }
            if (choice.InProjectFolder) await OpenWorkspaceAsync(choice.Project, false);
            else await CreateWorktreeAsync(choice);
            if (choice.Launcher is { } launcher && WorkspaceMounted && !atHome)
                await OpenPluginTerminalAsync(workspaceRoot, new() { Command = launcher.TerminalCommand(new()) });
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
        finally { (probe as IDisposable)?.Dispose(); creatingWorkspace = false; }
    }

    // A rejected dialog action has no dialog left to report in, so it is a toast rather than the window's error line.
    private void ReportDialogFailure(string title, Exception error)
    {
        Toasts.Push(State.ToastVariant.Error, error.Message, title);
        Console.Error.WriteLine(error);
    }

    private async Task PrefetchDefaultAsync(NewWorkspaceDialog dialog, string project)
    {
        try
        {
            var fresh = await host.ListBranchesAsync(true, lifetime.Token);
            if (dialog.Window.IsVisible) dialog.Update(fresh, project);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine(error.Message); }
    }

    private async Task CreateWorktreeAsync(NewWorkspaceChoice choice)
    {
        gitRefresh?.Cancel();
        try
        {
            git = await host.ApplyGitActionAsync(new("create-worktree", choice.Path, choice.Branch, choice.Base), lifetime.Token);
            removedWorkspaces.Remove(choice.Path);
            if (choice.Base.Length > 0 && choice.Base != "HEAD") profile.Data.GitSelections[choice.Path] = new(choice.Base, "All changes", null);
            if (choice.Name is { } name) await state.ChangeAsync(HostStateChange.Label(choice.Path, name));
            await OpenWorkspaceAsync(choice.Path, false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ReportDialogFailure("Couldn't create workspace", error);
            RefreshGitPanels();
        }
    }

    /// <summary>An attached worktree is only forgotten; one SharpRail created is removed from disk unless Git refuses.</summary>
    private async Task RemoveWorkspaceAsync(WorkspaceRecord worktree)
    {
        var external = worktree.Kind == WorkspaceKinds.External;
        // The dialog names the workspace it was opened for; leaving that workspace first dismisses it.
        using var dismiss = new CancellationTokenSource();
        void Left() { if (atHome || workspaceRoot != worktree.Path) dismiss.Cancel(); }
        if (!atHome && worktree.Path == workspaceRoot) LocationChanged += Left;
        try
        {
            if (!(external
                ? await Dialogs.Confirm(this, $"Remove {WorkspaceName(worktree.Path)} from SharpRail?",
                    $"SharpRail stops listing {worktree.Path}. The checkout and its branch stay untouched; reopen it from Open existing worktree…", "Remove from SharpRail", dismiss: dismiss.Token)
                : await Dialogs.Confirm(this, "Remove worktree?", $"Remove {worktree.Path}? Git will refuse if it has uncommitted changes. The branch will be retained.",
                    dismiss: dismiss.Token))) return;
        }
        finally { LocationChanged -= Left; }
        if (!atHome && worktree.Path == workspaceRoot)
        {
            var previous = selectionHistory.LastOrDefault(path => path != worktree.Path &&
                (path.Length == 0 || path == projectRoot || state.Current.WorkspacesOf(projectRoot).Any(workspace => workspace.Path == path)));
            if (string.IsNullOrEmpty(previous)) await OpenProjectHomeAsync(projectRoot);
            else await OpenWorkspaceAsync(previous, false);
            if (!atHome && workspaceRoot == worktree.Path) return;
        }
        selectionHistory.Remove(worktree.Path);
        // The row may predate a re-registration of its path, so the id is read when it is used.
        var id = state.Current.Workspaces.FirstOrDefault(workspace => workspace.Path == worktree.Path)?.Id ?? worktree.Id;
        try { await host.ApplyWorkspaceActionAsync(external ? WorkspaceAction.Forget(id) : WorkspaceAction.Remove(id), lifetime.Token); }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private async Task RevealWorkspaceAsync(string path)
    {
        try { await host.ApplyWorkspaceActionAsync(WorkspaceAction.Reveal(path), lifetime.Token); }
        catch (Exception error) when (error is not OperationCanceledException) { Report(new IOException("Couldn't reveal the workspace. " + error.Message)); }
    }

    private ContextMenu WorkspaceActions(WorkspaceRecord worktree, Button kebab)
    {
        var menu = new ContextMenu { Name = "WorkspaceActions", Placement = PlacementMode.BottomEdgeAlignedRight, PlacementTarget = kebab };
        var openIn = AddWorkspaceActions(menu, worktree, header: false);
        menu.Opened += (_, _) => _ = LoadEditorsAsync(openIn, worktree.Path);
        return menu;
    }

    /// <summary>The one set of workspace actions, shown by a Projects row and by the header's workspace menu.</summary>
    private MenuItem AddWorkspaceActions(ContextMenu menu, WorkspaceRecord worktree, bool header)
    {
        var openIn = new MenuItem { Header = "Open in", Name = "WorkspaceOpenIn" };
        FillEditors(openIn, worktree.Path);
        menu.Items.Add(openIn);
        var copy = Ui.Menu("Copy absolute path", () => _ = CopyTextAsync(worktree.Path));
        copy.Name = "WorkspaceCopyPath";
        menu.Items.Add(copy);
        var copyName = Ui.Menu("Copy name", () => _ = CopyTextAsync(WorkspaceName(worktree.Path)));
        copyName.Name = "WorkspaceCopyName";
        menu.Items.Add(copyName);
        var reveal = Ui.Menu("Reveal in file manager", () => _ = RevealWorkspaceAsync(worktree.Path));
        reveal.Name = "WorkspaceReveal";
        menu.Items.Add(reveal);
        if (worktree.Kind == WorkspaceKinds.Default) return openIn;
        // An attached worktree is the user's: SharpRail neither renames nor removes it, it only stops listing it.
        if (worktree.Kind == WorkspaceKinds.Managed)
        {
            var rename = Ui.Menu("Rename", () => StartRename(worktree.Path, header));
            rename.Name = "WorkspaceRename";
            menu.Items.Add(rename);
        }
        menu.Items.Add(new Separator());
        var remove = Ui.Menu(worktree.Kind == WorkspaceKinds.External ? "Remove from SharpRail" : "Remove worktree…", () => _ = RemoveWorkspaceAsync(worktree),
            !WorktreeLocked(worktree.Path));
        remove.Name = "WorkspaceRemove";
        menu.Items.Add(remove);
        return openIn;
    }

    private async Task LoadEditorsAsync(MenuItem openIn, string path)
    {
        if (editors is not null) return;
        try { editors = await host.ListEditorsAsync(lifetime.Token); }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); return; }
        FillEditors(openIn, path);
    }

    private void FillEditors(MenuItem openIn, string path)
    {
        openIn.Items.Clear();
        if (editors is null) { openIn.Items.Add(new MenuItem { Header = "Looking for editors…", IsEnabled = false }); return; }
        if (editors.Count == 0) { openIn.Items.Add(new MenuItem { Header = "No editors found", IsEnabled = false }); return; }
        foreach (var editor in editors)
        {
            var item = Ui.Menu(editor.Label, () => _ = OpenInEditorAsync(editor.Id, path));
            item.Name = "WorkspaceOpenInEditor";
            openIn.Items.Add(item);
        }
    }

    private async Task OpenInEditorAsync(string editorId, string path)
    {
        try { await host.OpenInEditorAsync(editorId, path, lifetime.Token); }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    private async Task CopyTextAsync(string text)
    {
        if (Clipboard is not null) await Clipboard.SetTextAsync(text);
    }

    private void StartRename(string path, bool header = false)
    {
        renaming = path; renameDraft = renameOriginal = WorkspaceName(path); renameCommitPending = false;
        renameInHeader = header; renameBox = null;
        UpdateScopeLabels();
        toolContent.Remove("projects"); surface.RefreshContents("projects");
    }

    private Control RenameBox(Thickness margin, double height)
    {
        var box = new TextBox { Name = "WorkspaceRenameInput", Text = renameDraft, MinHeight = height, Margin = margin };
        if (height < 28) { box.Height = height; box.Padding = new Thickness(8, 2); box.MinWidth = 160; }
        AutomationProperties.SetName(box, "Workspace name");
        renameBox = box;
        box.TextChanged += (_, _) => { if (ReferenceEquals(renameBox, box)) renameDraft = box.Text ?? ""; };
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        // Leaving the box within this window commits. Focus moving to another window, or a rail rebuilt
        // by a broadcast replacing this box, keeps the rename open; the check is deferred until focus settles.
        box.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var focused = FocusManager?.GetFocusedElement() as Visual;
            if (ReferenceEquals(renameBox, box) && box.IsAttachedToVisualTree() && !box.IsKeyboardFocusWithin &&
                (focused is null || TopLevel.GetTopLevel(focused) == this)) CommitRename();
        });
        box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(renameBox, box)) return;
            box.Focus(); box.SelectAll();
        });
        return box;
    }

    /// <summary>
    /// Commits the draft through the host. An unchanged draft leaves a label another client set meanwhile;
    /// while the host is unreachable the input stays open and commits after reconnecting.
    /// </summary>
    private async void CommitRename()
    {
        if (renaming is not { } path || renameCommitting) return;
        var label = renameDraft.Trim();
        if (label.Length == 0 || label == renameOriginal) { EndRename(); return; }
        renameCommitting = true;
        try { await state.ChangeAsync(HostStateChange.Label(path, label)); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (!state.Connected) { renameCommitPending = true; return; }
            Report(new IOException("The host could not save this change: " + error.Message));
            EndRename(); return;
        }
        finally { renameCommitting = false; }
        if (renaming == path) EndRename();
    }

    private void EndRename()
    {
        renaming = null; renameBox = null; renameCommitPending = false;
        UpdateScopeLabels();
        toolContent.Remove("projects"); surface.RefreshContents("projects");
    }
}