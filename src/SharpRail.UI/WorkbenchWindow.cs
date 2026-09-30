using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow : Window
{
    private readonly IProjectServices host;
    private readonly Workbench workbench;
    private readonly ProfileStore profile;
    private readonly WindowProfile slot;
    private readonly SharedState state;
    private readonly string initialRoot;
    private readonly bool remote;
    private readonly Terminal.TerminalFactory terminals;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim projectGate = new(1, 1);
    private readonly Dictionary<string, Control> toolContent = [];
    private readonly Dictionary<string, Control> documentContent = [];
    private readonly Dictionary<string, FileDocument> documents = [];
    private readonly TextBlock projectLabel;
    private readonly TextBlock branchLabel;
    private readonly Control branchIcon;
    private readonly TextBlock status;
    private readonly TextBlock errorText;
    private readonly DockSurface surface;
    private readonly Grid root;
    private long projectRequest;
    private string projectRoot = "";
    private string workspaceRoot = "";
    private GitSnapshot git = new(false, "", [], [], []);
    public LayoutSession Layout { get; }
    public bool WorkspaceMounted { get; private set; }
    public string WorkspaceRoot => workspaceRoot;
    public Preferences Preferences => state.Preferences;
    public Func<CancellationToken, Task<GitHubStatus>>? GitHubStatusProbe { get; set; }
    public Workbench Workbench => workbench;
    public WindowProfile Slot => slot;
    internal IProjectServices Host => host;

    /// <summary>A standalone window with its own workbench over the profile's local host state; it restores the profile's first window.</summary>
    public WorkbenchWindow(IProjectServices host, string rootPath, ProfileStore profile, Terminal.TerminalFactory terminals, bool remote = false)
        : this(Standalone(profile, terminals, remote), host, profile.Data.Windows[0], rootPath) => workbench.Attach(this);

    private static Workbench Standalone(ProfileStore profile, Terminal.TerminalFactory terminals, bool remote)
    {
        var store = profile.OpenState();
        return new(profile, new SharedState(new Host.Client.LocalStateAdapter(store), profile.Data.Preferences, store.Current), terminals, remote, null);
    }

    internal WorkbenchWindow(Workbench workbench, IProjectServices host, WindowProfile slot, string rootPath)
    {
        this.workbench = workbench; this.host = host; this.slot = slot; profile = workbench.Profile; state = workbench.State;
        initialRoot = rootPath; remote = workbench.Remote; terminals = workbench.Terminals;
        FontSize = Ui.FontSize;
        AvaloniaXamlLoader.Load(this);
        root = this.FindControl<Grid>("WorkbenchRoot")!;
        projectLabel = this.FindControl<TextBlock>("ProjectLabel")!;
        branchLabel = this.FindControl<TextBlock>("BranchLabel")!;
        status = this.FindControl<TextBlock>("ConnectionStatus")!;
        errorText = this.FindControl<TextBlock>("WorkspaceError")!;
        branchIcon = Ui.Icon("gitBranch", Ui.Muted, 14);
        this.FindControl<ContentControl>("BranchIcon")!.Content = branchIcon;
        WireHeader();
        Layout = new(slot.Layout);
        Layout.Navigating += group => AdvanceNavigation(group);
        Layout.Focused += () => { slot.Layout = Layout.State; SaveProfile(); };
        Layout.Changed += () => { slot.Layout = Layout.State; SaveProfile(); PruneDocuments(); };
        Layout.Changed += UpdateActiveChangeRows;
        Layout.SelectionChanged += _ => UpdateActiveChangeRows();
        Layout.Focused += UpdateActiveChangeRows;
        surface = new DockSurface(Layout, RenderContent);
        Ui.Place(root, surface, 1);
        WireGestureNotification();
        WireEditorLifetime();
        WireHostSync();
        ApplyAppearance();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Opened += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            RememberGitSelection(); lifetime.Cancel(); StopWatching(); profile.Save();
            ClearDocumentContent();
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (command && e.Key == Key.O) { _ = PickProjectAsync(); e.Handled = true; }
            else if (command && e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { NewWindow(); e.Handled = true; }
            else if (command && e.Key == Key.N) { _ = CreateWorkspaceDialogAsync(); e.Handled = true; }
            else if (command && e.Key == Key.OemComma) { ShowSettings(); e.Handled = true; }
            else if (command && e.Key == Key.J && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            { Layout.Visible("bottom", !Layout.State.BottomVisible); e.Handled = true; }
            else if (command && e.Key == Key.B)
            { Layout.Visible("left", !Layout.State.LeftVisible); e.Handled = true; }
            else if (command && e.Key == Key.J)
            { Layout.Visible("right", !Layout.State.RightVisible); e.Handled = true; }
            else if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; }
        }, RoutingStrategies.Bubble);
    }

    private void WireHeader()
    {
        var header = this.FindControl<Grid>("MainHeader")!;
        header.Margin = new Thickness(OperatingSystem.IsMacOS() ? 80 : 12, 0, 12, 0);
        header.ContextMenu = ViewMenu();
        this.FindControl<ContentControl>("BrandIcon")!.Content = Ui.Icon("brand", Ui.Accent, 28);
        var settings = this.FindControl<Button>("SettingsButton")!;
        settings.Content = Ui.Icon("settings");
        settings.Click += (_, _) => ShowSettings();
        var frame = this.FindControl<Border>("WindowTitleBar")!;
        frame.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed || e.Source is not Control source ||
                source is Button || source.GetLogicalAncestors().OfType<Button>().Any()) return;
            e.Handled = true;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
    }

    private ContextMenu ViewMenu()
    {
        var menu = new ContextMenu();
        foreach (var tool in DockState.ToolNames)
            menu.Items.Add(Ui.Menu("Show " + DockState.Tool(tool).Title, () =>
            {
                var group = Layout.State.Groups.FirstOrDefault(item => item.Tools.Any(tab => tab.Id == tool));
                if (group is null) Layout.RestoreTool(tool);
                else { Layout.Visible(group.Region, true); if (group.Folded) Layout.Fold(group.Id); Layout.Select(group.Id, tool); }
            }));
        foreach (var region in new[] { "left", "right", "bottom" })
            menu.Items.Add(Ui.Menu("Toggle " + region, () => Layout.Visible(region, region switch
            { "left" => !Layout.State.LeftVisible, "right" => !Layout.State.RightVisible, _ => !Layout.State.BottomVisible })));
        menu.Items.Add(Ui.Menu("Reset frame", () => Layout.ApplyPreset(DefaultPreset())));
        menu.Items.Add(new Separator());
        var window = Ui.Menu("New window", () => NewWindow(), workbench.CanOpenWindows);
        window.Name = "NewWindow";
        window.InputGesture = new KeyGesture(Key.N, (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | KeyModifiers.Shift);
        menu.Items.Add(window);
        return menu;
    }

    /// <summary>The window-local default preset; a custom one deleted or renamed elsewhere falls back to Balanced.</summary>
    internal DockState DefaultPreset() =>
        Preferences.CustomPresets.GetValueOrDefault(slot.DefaultPreset) ?? DockState.Preset(slot.DefaultPreset is "focus" or "review" ? slot.DefaultPreset : "balanced");

    public WorkbenchWindow? NewWindow() => workbench.NewWindow(this);

    public async Task OpenProjectAsync(string path) => await OpenWorkspaceAsync(path, true);

    private async Task OpenWorkspaceAsync(string path, bool project, bool home = false)
    {
        RememberGitSelection();
        var previous = WorkspaceMounted ? (atHome ? "" : workspaceRoot) : null;
        var request = ++projectRequest; WorkspaceMounted = false;
        gitRefresh?.Cancel(); StopWatching();
        try { await projectGate.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (request != projectRequest) return;
            status.Text = "Loading";
            var workspace = await host.OpenProjectAsync(path, lifetime.Token);
            if (request != projectRequest) return;
            if (home && workspace.RootPath != workspace.ProjectRoot)
            {
                workspace = await host.OpenProjectAsync(workspace.ProjectRoot, lifetime.Token);
                if (request != projectRequest) return;
            }
            var files = await Task.Run(async () => await host.ListFilesAsync("", lifetime.Token), lifetime.Token);
            if (request != projectRequest) return;
            workspaceRoot = workspace.RootPath;
            if (project && projectRoot != workspace.ProjectRoot) selectionHistory.Clear();
            else if (previous is not null) { selectionHistory.Remove(previous); selectionHistory.Add(previous); }
            if (project) projectRoot = workspace.ProjectRoot;
            atHome = home; cleanWelcome = false;
            UpdateScopeLabels();
            branchLabel.Text = "";
            branchIcon.IsVisible = false;
            git = new(false, "", [], [], []); gitLoading = true; gitError = null;
            RestoreGitSelection(); folderCache.Clear(); expandedFolders.Clear();
            folderCache[""] = files;
            toolContent.Clear();
            if (!state.Current.Projects.Contains(projectRoot) || state.Current.RecentProjects.Contains(projectRoot))
                _ = ShareAsync(HostStateChange.OpenProject(projectRoot));
            slot.LastProject = workspaceRoot;
            slot.LastProjectRoot = projectRoot;
            slot.LastAtHome = home;
            WorkspaceMounted = true; restorePending = false;
            Layout.SwitchWorkspace(home ? HomeKey(projectRoot) : workspaceRoot);
            status.Text = remote ? "Remote" : "Connected";
            errorText.IsVisible = false;
            ReportProfileError();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateLayout();
                Console.WriteLine($"SHARPRAIL_WORKSPACE_LAYOUT {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                Console.WriteLine($"SHARPRAIL_TREE logical={this.GetLogicalDescendants().Distinct().Count()} files={files.Count}");
            }, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
        finally { projectGate.Release(); }
        if (request == projectRequest && WorkspaceMounted)
            Dispatcher.UIThread.Post(() => { _ = RefreshGitAsync(request); StartWatching(request); }, DispatcherPriority.Background);
    }

    private Control RenderContent(DockTab? tab)
    {
        if (tab is null)
        {
            if (atHome || cleanWelcome) return Welcome();
            var empty = new StackPanel
            {
                Name = "WorkspacePlaceholder",
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var isDefault = workspaceRoot == projectRoot;
            empty.Children.Add(Ui.Text(isDefault ? "DEFAULT WORKSPACE" : "WORKSPACE READY", size: 12));
            empty.Children.Add(Ui.Text(isDefault ? projectLabel.Text ?? "" : WorkspaceName(workspaceRoot), Ui.TextBrush));
            readyBranch = Ui.Text(ReadyBranchText());
            readyBranch.Name = "WorkspaceReadyBranch";
            empty.Children.Add(readyBranch);
            empty.Children.Add(Ui.Text(isDefault
                ? "Files, changes, and worktrees run directly in your project folder."
                : "Files, changes, and terminals are scoped to this workspace."));
            foreach (var label in empty.Children.OfType<TextBlock>())
                label.HorizontalAlignment = HorizontalAlignment.Center;
            var open = Ui.Button("Open file", () => RevealFiles(), "fileText"); open.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(open); return empty;
        }
        if (tab.IsTool)
        {
            if (!toolContent.TryGetValue(tab.Id, out var content))
            {
                content = tab.Id switch
                {
                    "projects" => ProjectsPanel(),
                    "files" => FilesPanel(),
                    "specs" => SpecsPanel(),
                    "changes" => ChangesPanel(),
                    _ => ReviewPanel()
                };
                toolContent[tab.Id] = content;
            }
            return content;
        }
        if (tab.Kind == "terminal" && !WorkspaceMounted) return Ui.Text("Loading terminal…");
        var key = workspaceRoot + ":" + tab.Id;
        if (documentContent.TryGetValue(key, out var existing)) return existing;
        if (tab.Kind == "terminal")
        {
            var terminal = new Terminal.TerminalView(terminals, new(workspaceRoot, "", Path.Combine(profile.DirectoryPath, "clipboard")))
            { Name = "TerminalSurface_" + tab.Id.Replace(':', '_') };
            documentContent[key] = terminal;
            return terminal;
        }
        if (documents.TryGetValue(key, out var document))
        {
            Control content;
            if (document.ImageData is not null)
                content = new ScrollViewer { Content = new Image { Source = new Bitmap(new MemoryStream(document.ImageData)), Stretch = Stretch.Uniform } };
            else if (tab.Kind == "markdown")
                content = new MarkdownDocumentView(document, host, Preferences, (path, anchor) => _ = OpenDocumentAsync(path, false, anchor));
            else if (tab.Kind == "diff")
                content = DiffDocument(document, tab, key);
            else if (tab.Kind == "file" && OperatingSystem.IsMacOS())
                content = CodeDocument(document, tab, key);
            else
                content = new ScrollViewer
                {
                    Content = MarkdownPreview.Code(document.Text),
                    Margin = new Thickness(20),
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                };
            documentContent[key] = content; return content;
        }
        var loading = Ui.Text("Loading document…");
        if (WorkspaceMounted && restoringDocuments.Add(key)) _ = RestoreDocumentAsync(tab, key);
        return loading;
    }

    private async Task RestoreDocumentAsync(DockTab tab, string key)
    {
        try
        {
            var request = projectRequest;
            var file = tab.Kind == "diff"
                ? new FileDocument(tab.Path, await host.GetDiffAsync(tab.Path, tab.Scope, tab.Comparison, lifetime.Token))
                : await host.ReadFileAsync(tab.Path, lifetime.Token);
            if (request != projectRequest || !LiveDocuments().Contains(key)) return;
            documents[key] = file; surface.RefreshContents();
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
        finally { restoringDocuments.Remove(key); }
    }

    public async Task OpenDocumentAsync(string path, bool keep = false, string? anchor = null)
    {
        if (!WorkspaceMounted) return;
        if (atHome) await OpenWorkspaceAsync(projectRoot, false);
        if (!WorkspaceMounted || atHome) return;
        var request = BeginNavigation(); var workspace = workspaceRoot;
        try
        {
            var document = await host.ReadFileAsync(path, lifetime.Token);
            var destination = AcceptNavigation(request);
            if (destination is null) return;
            var kind = Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" ? "markdown" : "file";
            var tab = new DockTab(kind + ":" + path, Path.GetFileName(path), kind, path);
            var key = workspace + ":" + tab.Id;
            if (documentContent.GetValueOrDefault(key) is not Editor.CodeDocumentView)
            { documents[key] = document; DropDocumentContent(key); }
            Layout.Open(tab, keep, destination, activate: destination == Layout.View.FocusedCenter);
            if (anchor is not null && documentContent.GetValueOrDefault(key) is MarkdownDocumentView preview)
                Dispatcher.UIThread.Post(() => preview.ScrollToAnchor(anchor), DispatcherPriority.Loaded);
        }
        catch (Exception error) when (error is not OperationCanceledException) { Report(error); }
    }

    public async Task RefreshAsync()
    {
        if (!WorkspaceMounted) return;
        var request = projectRequest;
        try
        {
            var files = await Task.Run(async () => await host.ListFilesAsync("", lifetime.Token), lifetime.Token);
            if (request != projectRequest) return;
            folderCache.Clear(); folderCache[""] = files;
            status.Text = remote ? "Remote" : "Connected"; errorText.IsVisible = false;
            toolContent.Remove("files"); toolContent.Remove("specs"); surface.RefreshContents("files", "specs");
            await RefreshGitAsync(request);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OperationCanceledException)
        { if (request == projectRequest) Report(error); }
    }

    private void Report(Exception error)
    {
        errorText.Text = error.Message; errorText.IsVisible = true; status.Text = "Error";
        Console.Error.WriteLine(error);
    }

    private void SaveProfile()
    {
        profile.Save();
        ReportProfileError();
    }

    private void ReportProfileError()
    {
        if (profile.LastError is { } message)
            Report(new IOException("Workspace settings could not be saved: " + message));
    }

    private void RevealFiles()
    {
        var group = Layout.State.Groups.FirstOrDefault(item => item.Tools.Any(tab => tab.Id == "files"));
        if (group is null) Layout.RestoreTool("files");
        else { Layout.Visible(group.Region, true); if (group.Folded) Layout.Fold(group.Id); Layout.Select(group.Id, "files"); }
    }

    /// <summary>System mode inherits the application's variant, which follows the operating system; fixed mode pins the theme's own.</summary>
    private void ApplyTheme()
    {
        var system = Preferences.ThemeMode == "system";
        if (system) RequestedThemeVariant = ThemeVariant.Default;
        var theme = Themes.Resolve(Preferences, ActualThemeVariant == ThemeVariant.Light ? "light" : "dark").Theme;
        if (!system) RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Ui.Apply(theme);
    }

    private void ApplyAppearance()
    {
        ApplyTheme();
        var previous = Ui.FontSize; Ui.FontSize = Preferences.FontSize;
        foreach (var label in root.GetLogicalDescendants().OfType<TextBlock>().Where(label => label.FontSize == previous && !label.Classes.Contains("dock-tab-title")))
            label.FontSize = Preferences.FontSize;
        FontSize = Preferences.FontSize;
    }

    public void ShowSettings()
    {
        var scrim = new Border { Background = new SolidColorBrush(Colors.Black, .5) };
        Grid.SetRowSpan(scrim, 3);
        root.Children.Add(scrim);
        var settings = new SettingsWindow(this, () =>
        {
            ApplyAppearance(); ClearDocumentContent(preserveDocuments: true); toolContent.Clear(); surface.RefreshContents();
            ReportProfileError();
        }, GitHubStatusProbe);
        settings.Closed += (_, _) => root.Children.Remove(scrim);
        _ = settings.ShowDialog(this);
    }
}
