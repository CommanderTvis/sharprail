using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks.E2E;

internal sealed class E2eWorkspace : IDisposable
{
    internal static string MarkdownSourceText(Control source) => source is SharpRail.Scintilla.ScintillaEditor editor
        ? editor.Text
        : string.Join('\n', source.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(block =>
            string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text) ?? [])));

    internal WorkbenchWindow Window { get; }
    internal E2eHost Host { get; }
    internal E2eTerminals Terminals { get; }
    internal Workbench Workbench { get; }
    /// <summary>The local host's shared state behind every window of this workbench.</summary>
    internal HostStateStore? State { get; }
    internal PluginRuntime? PluginRuntime => pluginRuntime;
    internal string Center => Window.Layout.View.FocusedCenter;
    internal IReadOnlyList<DockTab> Tabs => Window.Layout.Tabs(Center);
    internal string Root { get; }
    private readonly bool ownsTerminals;
    private readonly bool peer;

    private E2eWorkspace(E2eWorkspace owner, WorkbenchWindow window)
    {
        Root = owner.Root; Host = owner.Host; Terminals = owner.Terminals; Workbench = owner.Workbench; State = owner.State;
        Window = window; peer = true;
    }

    /// <summary>Opens another window of the same app with Mod+Shift+N and drives it like the first.</summary>
    internal E2eWorkspace NewWindow()
    {
        var count = Workbench.Windows.Count;
        Window.Focus();
        Window.KeyPress(Key.N, (OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control) | RawInputModifiers.Shift, PhysicalKey.N, null);
        Until(() => Workbench.Windows.Count == count + 1);
        var window = Workbench.Windows[^1];
        Until(() => window.WorkspaceMounted || window.ShowsWelcome);
        return new(this, window);
    }

    private readonly IDisposable? remoteState;
    private readonly RemotePluginAdapter? remotePlugins;
    private readonly PluginRuntime? pluginRuntime;
    private readonly LoopbackServer? loopback;

    /// <summary>
    /// A remote client of a real gRPC host at <paramref name="endpoint"/>, restoring <paramref name="startPath"/>;
    /// with <paramref name="plugins"/> it reaches the host's plugin runtime as the app does.
    /// </summary>
    internal E2eWorkspace(Uri endpoint, string token, string root, string profileRoot, string startPath, bool plugins = true, E2eTerminals? terminals = null)
    {
        Root = root;
        var profile = new ProfileStore(profileRoot);
        var service = new RemoteStateAdapter(endpoint, token);
        remoteState = service;
        remotePlugins = plugins ? new RemotePluginAdapter(endpoint, token) : null;
        State = null;
        Host = new(new RemoteProjectAdapter(endpoint, token));
        Terminals = terminals ?? new();
        var first = true;
        Workbench = new(profile, new SharedState(service, profile.Data.Preferences), Terminals.Factory, true,
            () => { if (!first) return new E2eHost(new RemoteProjectAdapter(endpoint, token)); first = false; return Host; }, remotePlugins)
        { Endpoint = endpoint.ToString() };
        Window = Workbench.Open(profile.Data.Windows[0], startPath);
        Window.Width = 1352; Window.Height = 848;
        Window.Show();
    }

    internal E2eWorkspace(string root, bool openFiles = true, string? profileRoot = null, E2eTerminals? terminals = null, string? startPath = null,
        Action<E2eHost>? prepare = null, Func<IHostStateService, IHostStateService>? state = null,
        Func<IPluginService, IPluginService>? plugins = null)
    {
        Root = root;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "README.md"), "# sample-project\n");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "plain-text-fixture\n");
        File.WriteAllText(Path.Combine(root, "DIAGRAM.md"), "# Diagram demo\n\n```mermaid\nflowchart TD; Start --> Finish; Finish --> Step0; " + string.Join("; ", Enumerable.Range(0, 12).Select(i => $"Step{i} --> Step{i + 1}")) + "\n```\n\n```mermaid\nflowchart TD; Start --> --> broken\n```\n\n```bash\necho plain-fence-stays-code\n```\n");
        File.WriteAllText(Path.Combine(root, "LARGE.md"), "# Large document\n\n" + string.Join("\n\n", Enumerable.Repeat("A fixture paragraph.", 100)));
        File.WriteAllText(Path.Combine(root, "ALERTS.md"), "# Alert callouts\n\n> [!NOTE]\n> Useful information users should know.\n\n> [!TIP]\n> Helpful advice for doing things better.\n\n> [!IMPORTANT]\n> Key information to achieve a goal.\n\n> [!WARNING]\n> Urgent info needing immediate attention.\n\n> [!CAUTION]\n> Advises about risky outcomes.\n\n> A plain blockquote, no marker, so it stays a quote.\n");
        File.WriteAllText(Path.Combine(root, "LINKS.md"), "# Link demo\n\nJump to [Section two](#section-two), open [the spec](SPEC.md), and see the logo:\n\n![logo](logo.png)\n\n## Section two\n\nTarget of the in-document anchor.\n");
        File.WriteAllText(Path.Combine(root, "SPEC.md"), "---\nid: sample-root\ntype: goal-and-requirements\ntitle: Sample Project\n---\n\n## Goal\n\nA throwaway fixture project.\n");
        Directory.CreateDirectory(Path.Combine(root, "styles"));
        File.WriteAllText(Path.Combine(root, "styles", "COLOR.md"), "# Colour system\n\nSee [`themes/SPEC.md`](../themes/SPEC.md).\n");
        Directory.CreateDirectory(Path.Combine(root, "themes"));
        File.WriteAllText(Path.Combine(root, "themes", "SPEC.md"), "# Theme spec target\n\nReached through a parent-relative Markdown link.\n");
        File.WriteAllBytes(Path.Combine(root, "logo.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
        var profile = new ProfileStore(profileRoot ?? root + "-profile");
        State = profile.OpenState();
        // As in App.cs: the app's own host runs its plugins in process, from the profile directory's plugins/.
        var server = loopback = new LoopbackServer(null);
        pluginRuntime = new PluginRuntime(new() { StateDirectory = profile.DirectoryPath, State = State, PublicBaseUrl = () => server.BaseUrl });
        loopback.Plugins = pluginRuntime;
        pluginRuntime.Start();
        var allowsExternalFile = (Func<string, string, bool>)pluginRuntime.AllowsExternalFile;
        Host = new(new ProjectServices(root, State, allowsExternalFile));
        prepare?.Invoke(Host);
        ownsTerminals = terminals is null;
        Terminals = terminals ?? new();
        IHostStateService service = new LocalStateAdapter(State);
        var first = true;
        Workbench = new(profile, new SharedState(state?.Invoke(service) ?? service, profile.Data.Preferences, State.Current), Terminals.Factory, false,
            () => { if (!first) return new E2eHost(new ProjectServices(root, State, allowsExternalFile)); first = false; return Host; },
            plugins?.Invoke(new LocalPluginAdapter(pluginRuntime)) ?? new LocalPluginAdapter(pluginRuntime));
        Window = Workbench.Open(profile.Data.Windows[0], startPath ?? root);
        Window.Width = 1352; Window.Height = 848;
        Window.Show();
        if (startPath is not null) return;
        Until(() => Window.WorkspaceMounted);
        if (openFiles) Click(Find<Button>("Tab_files"));
    }

    internal static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Until(Func<bool> condition)
    {
        // A liveness bound, not an assertion: workspace flows start a dozen Git processes, which a loaded machine stretches past ten seconds.
        var deadline = Awake.Now.AddSeconds(30);
        while (!condition() && Awake.Now < deadline) Pump();
        Dispatcher.UIThread.RunJobs(); Require(condition(), "E2E condition timed out.");
    }

    internal static void Settle(int milliseconds = 300)
    {
        var deadline = Awake.Now.AddMilliseconds(milliseconds);
        while (Awake.Now < deadline) Pump();
    }

    private static void Pump()
    {
        using var stop = new CancellationTokenSource(10);
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    internal T Find<T>(string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
        return Window.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
    }

    internal Button Tab(string path) => Find<Button>("Tab_" + (path.EndsWith(".txt", StringComparison.Ordinal) ? "file_" : "markdown_") + path.Replace('/', '_'));

    internal Control FileRow(string path)
    {
        Until(() => Window.GetLogicalDescendants().OfType<TreeView>().Any(item => item.Name == "FilesTree"));
        var tree = Find<TreeView>("FilesTree");
        Until(() => tree.Items.Count > 0);
        var node = tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(item => item.Tag is ProjectFile file && file.Path == path);
        return (Control)node.Header!;
    }

    internal void ExpandFolder(string path)
    {
        var tree = Find<TreeView>("FilesTree");
        var node = tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(item => item.Tag is ProjectFile file && file.Path == path);
        Click(node.GetVisualDescendants().OfType<ToggleButton>().First());
        Until(() => node.IsExpanded && node.Items.OfType<TreeViewItem>().All(item => item.Tag is ProjectFile));
    }

    internal Control Click(Control control, bool twice = false, bool freshGesture = true, MouseButton mouseButton = MouseButton.Left)
    {
        var name = control.Name;
        var tag = control.Tag;
        var row = control.GetLogicalAncestors().OfType<Control>().FirstOrDefault(item => item.Name is not null && item.Tag is string);
        var rowName = row?.Name;
        var rowTag = row?.Tag;
        var owner = control.GetLogicalAncestors().OfType<Control>().FirstOrDefault(item => item.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true)?.Name;
        var filePath = control.GetLogicalAncestors().OfType<TreeViewItem>().Select(item => item.Tag).OfType<ProjectFile>().FirstOrDefault()?.Path;
        if (freshGesture)
        {
            Window.MouseMove(new Point(Window.Bounds.Width - 2, Window.Bounds.Height - 2));
            Settle(550);
        }
        if (TopLevel.GetTopLevel(control) is null)
        {
            if (name is not null && rowName is not null)
            {
                var currentRow = Window.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == rowName && Equals(item.Tag, rowTag));
                control = currentRow.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == name);
            }
            else if (name is not null && owner is not null) control = Find<Control>(owner).GetLogicalDescendants().OfType<Control>().Single(item => item.Name == name);
            else if (name is not null && tag is not null)
            {
                Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
                control = Window.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == name && Equals(item.Tag, tag));
            }
            else if (name is not null) control = Find<Control>(name);
            else if (filePath is not null) control = FileRow(filePath);
        }
        Window.UpdateLayout();
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel(control)!.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var input = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(Math.Min(24, control.Bounds.Width / 2), control.Bounds.Height / 2), input)!.Value;
        Require(control.Bounds.Width > 0 && control.Bounds.Height > 0, "E2E input target is not arranged.");
        input.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var hit = input.InputHitTest(point) as Control;
        Require(hit is not null && (ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control)),
            $"E2E target {control.Name ?? control.GetType().Name} ({control.Bounds}) is occluded at {point} by {hit?.GetType().Name}/{hit?.Name}; ancestors: {string.Join(",", hit?.GetVisualAncestors().OfType<Control>().Select(item => item.GetType().Name + "/" + item.Name) ?? [])}.");
        input.MouseDown(point, mouseButton); input.MouseUp(point, mouseButton);
        if (twice) { input.MouseDown(point, mouseButton); input.MouseUp(point, mouseButton); }
        Dispatcher.UIThread.RunJobs();
        return control;
    }

    internal void ContextAction(Control control, string title)
    {
        control = Click(control, mouseButton: MouseButton.Right);
        var menu = control.ContextMenu!;
        Until(() => menu.IsOpen);
        var item = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, title));
        Require(item.IsEnabled, "E2E menu action is disabled: " + title);
        TopLevel.GetTopLevel(item)!.UpdateLayout();
        Click(item, freshGesture: false);
    }

    internal void Open(string path, bool keep = false)
    {
        Click(FileRow(path), keep);
        Until(() => Tabs.Any(tab => tab.Path == path && (!keep || !tab.Preview)) && Window.Layout.Selected(Center)?.Path == path);
    }

    // Closing detaches the windows' terminals; an app that shares its terminals with a later window keeps them.
    public void Dispose()
    {
        if (!peer)
            foreach (var other in Workbench.Windows.Where(window => window != Window).ToArray()) other.Close();
        Window.Close();
        if (peer) return;
        remoteState?.Dispose();
        remotePlugins?.Dispose();
        if (pluginRuntime is not null || loopback is not null)
            Task.Run(async () =>
            {
                if (pluginRuntime is not null) await pluginRuntime.DisposeAsync();
                if (loopback is not null) await loopback.DisposeAsync();
            }).GetAwaiter().GetResult();
        if (ownsTerminals) Terminals.Quit();
    }
}

internal sealed class E2eHost(IProjectServices inner) : IProjectServices
{
    private TaskCompletionSource? watchGate;
    internal TaskCompletionSource HoldWatch() => watchGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var gate = watchGate; watchGate = null;
        if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
        await foreach (var changes in inner.WatchFilesAsync(cancellationToken)) yield return changes;
    }
    private readonly Dictionary<string, TaskCompletionSource> gates = [];
    private TaskCompletionSource? openGate;
    internal TaskCompletionSource HoldOpen() => openGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? gitGate;
    internal TaskCompletionSource HoldGit() => gitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Dictionary<string, int> Reads { get; } = [];
    internal TaskCompletionSource Hold(string path)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gates.Add(path, gate); return gate;
    }
    public async ValueTask<FileDocument> ReadFileAsync(string path, CancellationToken ct = default)
    {
        Reads[path] = Reads.GetValueOrDefault(path) + 1;
        gates.Remove(path, out var gate);
        var document = await inner.ReadFileAsync(path, ct);
        if (gate is not null) await gate.Task.WaitAsync(ct);
        return document;
    }
    public ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken ct = default) => inner.SaveFileAsync(request, ct);
    public async ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken ct = default)
    {
        var gate = openGate; openGate = null;
        var workspace = await inner.OpenProjectAsync(path, ct);
        if (gate is not null) await gate.Task.WaitAsync(ct);
        return workspace;
    }
    public ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string path, CancellationToken ct = default) => inner.ListFilesAsync(path, ct);
    /// <summary>While set, listing specs fails with this error.</summary>
    internal Exception? SpecsFailure { get; set; }
    public ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken ct = default) =>
        SpecsFailure is { } failure ? ValueTask.FromException<IReadOnlyList<SpecDocument>>(failure) : inner.ListSpecsAsync(ct);
    public ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken ct = default) => inner.GetSpecGraphAsync(ct);
    public ValueTask<bool> HasDurableSpecsAsync(CancellationToken ct = default) => inner.HasDurableSpecsAsync(ct);
    public ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken ct = default) => inner.InspectProjectPathAsync(path, ct);
    public ValueTask PrewarmWorkspaceAsync(string path, CancellationToken ct = default) => inner.PrewarmWorkspaceAsync(path, ct);
    public ValueTask<SearchHits> SearchAsync(string query, CancellationToken ct = default) => inner.SearchAsync(query, ct);
    public ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparison, CancellationToken ct = default) => inner.ListCommitsAsync(comparison, ct);
    public ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken ct = default) => inner.GetCommitAsync(sha, ct);
    public ValueTask<string> CreateProjectAsync(string parentPath, string name, CancellationToken ct = default) => inner.CreateProjectAsync(parentPath, name, ct);
    public ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken ct = default) => inner.CloneProjectAsync(url, parentPath, name, depth, ct);
    public async ValueTask<GitSnapshot> GetGitAsync(string comparison = "", CancellationToken ct = default, string scope = "all")
    {
        var gate = gitGate;
        var snapshot = await inner.GetGitAsync(comparison, ct, scope);
        if (gate is not null) await gate.Task.WaitAsync(ct);
        if (ReferenceEquals(gitGate, gate)) gitGate = null;
        return snapshot;
    }
    public ValueTask<string> GetDiffAsync(string path, string scope, string comparison = "", CancellationToken ct = default) => inner.GetDiffAsync(path, scope, comparison, ct);
    public ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparison = "", CancellationToken ct = default) => inner.GetDiffSidesAsync(path, scope, comparison, ct);
    public ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken ct = default) => inner.ReadContentBytesAsync(path, revision, ct);
    /// <summary>Runs once the client has read what it will revert, so a check can move the file under it.</summary>
    internal Action? BeforeRevert { get; set; }
    public ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparison, RevertTarget target, ChangeExpectation expect, CancellationToken ct = default)
    {
        BeforeRevert?.Invoke();
        return inner.RevertChangeAsync(path, scope, comparison, target, expect, ct);
    }
    public ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken ct = default) => inner.UndoChangeAsync(receiptId, expectModifiedHash, ct);
    public ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken ct = default) => inner.ApplyGitActionAsync(action, ct);
    public ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken ct = default) => inner.ListBranchesAsync(fetchDefault, ct);
    public ValueTask<DiffStats?> GetDiffStatsAsync(string workspacePath, CancellationToken ct = default) => inner.GetDiffStatsAsync(workspacePath, ct);
    /// <summary>Rewrites a pull request lookup, for states a fixture origin cannot produce.</summary>
    internal Func<OpenReview?, OpenReview?>? Review { get; set; }
    public async ValueTask<OpenReview?> GetOpenReviewAsync(bool fresh, CancellationToken ct = default)
    {
        var review = await inner.GetOpenReviewAsync(fresh, ct);
        return Review is null ? review : Review(review);
    }
    public ValueTask<PrDraft> PreviewPrAsync(CancellationToken ct = default) => inner.PreviewPrAsync(ct);
    public ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken ct = default) => inner.OpenPrAsync(request, ct);
    public ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken ct = default) => inner.ListEditorsAsync(ct);
    public ValueTask ApplyFileActionAsync(FileAction action, CancellationToken ct = default) => inner.ApplyFileActionAsync(action, ct);
    public ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken ct = default) => inner.OpenInEditorAsync(editorId, worktreePath, ct);
    public ValueTask<WorkspaceCatalog> ListWorkspacesAsync(string projectRoot, CancellationToken ct = default) => inner.ListWorkspacesAsync(projectRoot, ct);
    public ValueTask<WorkspaceRecord?> ApplyWorkspaceActionAsync(WorkspaceAction action, CancellationToken ct = default) => inner.ApplyWorkspaceActionAsync(action, ct);
}