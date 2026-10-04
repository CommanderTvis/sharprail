using System.Collections.Concurrent;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class StartupChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> done)
    {
        var deadline = Awake.Now.AddSeconds(15);
        while (!done() && Awake.Now < deadline) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(1); }
        Dispatcher.UIThread.RunJobs(); Require(done(), "Startup operation timed out.");
    }

    private static void Await(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static string? Branch(WorkbenchWindow window) => window.FindControl<TextBlock>("BranchLabel")!.Text;

    internal static void Run(string root)
    {
        PaneRetentionChecks.Run(root);
        DeferredWorkspaceSwitch(root);
        RetainedFiles(root);
        DeferredDocumentChrome(root);
        var profilePath = Path.Combine(root, ".startup-profile");
        var host = new DelayedGitHost(root);
        var window = new WorkbenchWindow(host, root, new ProfileStore(profilePath), E2E.E2eTerminals.Plain);
        window.Show();
        Pump(() => window.WorkspaceMounted && host.Requests.Count == 1);
        var first = host.Requests.Single();
        Require(!first.Result.Task.IsCompleted && window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Loading Git…"),
            "Workspace mounting waited for Git or reported an unloaded repository as absent.");
        Await(window.OpenDocumentAsync("README.md", true));
        Require(window.GetLogicalDescendants().OfType<MarkdownPreview>().Any(), "Pending Git blocked opening a document.");
        window.Close();
        Require(first.Token.IsCancellationRequested, "Closing the window did not cancel Git loading.");
        first.Result.SetResult(new(false, "", [], [], []));

        host = new DelayedGitHost(root);
        window = new WorkbenchWindow(host, root, new ProfileStore(profilePath), E2E.E2eTerminals.Plain);
        window.Show();
        Pump(() => window.WorkspaceMounted && host.Requests.Count == 1 && window.GetLogicalDescendants().OfType<MarkdownPreview>().Any());
        first = host.Requests.Single();
        Require(!first.Result.Task.IsCompleted, "Restored documents waited for Git loading.");
        var startup = first;
        var staleRefresh = window.RefreshAsync();
        Pump(() => host.Requests.Count == 2);
        Require(startup.Token.IsCancellationRequested, "A newer Git refresh did not cancel startup loading.");
        startup.Result.SetResult(new(false, "", [], [], []));
        first = host.Requests.Last();

        var other = Path.Combine(root, "startup-other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "hello.txt"), "Other workspace");
        Await(window.OpenProjectAsync(other));
        Pump(() => host.Requests.Count == 3);
        Require(first.Token.IsCancellationRequested && window.WorkspaceRoot == other && window.WorkspaceMounted,
            "A pending Git refresh blocked project switching or escaped cancellation.");
        var second = host.Requests.Last();
        var tab = window.GetLogicalDescendants().OfType<Button>().First(button => button.Name?.StartsWith("Tab_", StringComparison.Ordinal) == true);
        tab.Focus();
        second.Result.SetResult(new(true, "new-branch", [], [], []));
        Pump(() => Branch(window) == "new-branch");
        Require(tab.IsFocused && window.GetLogicalDescendants().Contains(tab), "Deferred Git replaced the tab strip or lost keyboard focus.");
        first.Result.SetResult(new(true, "stale-branch", [], [], []));
        Await(staleRefresh);
        Await(window.OpenDocumentAsync("hello.txt", true));
        Require(Branch(window) == "new-branch", "A superseded Git result overwrote the current workspace.");

        Await(window.OpenProjectAsync(root));
        Pump(() => host.Requests.Count == 4);
        host.Requests.Last().Result.SetException(new IOException("Git unavailable"));
        Pump(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Git could not be loaded: Git unavailable"));
        Await(window.OpenDocumentAsync("hello.txt", true));
        Require(window.WorkspaceMounted && !window.FindControl<TextBlock>("ConnectionStatus")!.IsVisible,
            "Git failure made an accessible workspace unusable.");
        window.Close();
        Console.WriteLine("PASS usable fresh/restored startup with pending Git, cancellation, project switching, stale results and Git failure");
    }

    private static void RetainedFiles(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "retained-files"), ("same.txt", "main"), ("removed.txt", "main"));
        Directory.CreateDirectory(Path.Combine(project, "folder"));
        File.WriteAllText(Path.Combine(project, "folder", "child.txt"), "main child");
        for (var index = 0; index < 40; index++) File.WriteAllText(Path.Combine(project, $"shared-{index:00}.txt"), "shared");
        IsolatedGit.Run(project, "add", "-A");
        IsolatedGit.Run(project, "commit", "-m", "folder");
        var other = project + "-feature";
        IsolatedGit.Run(project, "worktree", "add", "-b", "files-feature", other);
        File.Delete(Path.Combine(other, "removed.txt"));
        File.WriteAllText(Path.Combine(other, "added.txt"), "feature");
        File.WriteAllText(Path.Combine(other, "same.txt"), "feature content");
        File.WriteAllText(Path.Combine(other, "folder", "added-child.txt"), "feature child");
        var host = new DelayedGitHost(project);
        var window = new WorkbenchWindow(host, project, new ProfileStore(project + "-profile"), E2eTerminals.Plain);
        window.Show();
        TreeView Tree() => window.GetLogicalDescendants().OfType<TreeView>().Single(tree => tree.Name == "FilesTree");
        TreeViewItem Node(string path) => Tree().GetLogicalDescendants().OfType<TreeViewItem>()
            .Single(node => node.Tag is ProjectFile file && file.Path == path);
        bool Listed(string path) => Tree().GetLogicalDescendants().OfType<TreeViewItem>()
            .Any(node => node.Tag is ProjectFile file && file.Path == path);
        try
        {
            Pump(() => window.WorkspaceMounted);
            var filesGroup = window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files"));
            window.Layout.Select(filesGroup.Id, "files");
            Pump(() => Listed("same.txt"));
            var tree = Tree();
            var same = Node("same.txt");
            var folder = Node("folder");
            folder.IsExpanded = true;
            Pump(() => Listed(Path.Combine("folder", "child.txt")));
            var child = Node(Path.Combine("folder", "child.txt"));
            window.UpdateLayout();
            var scroll = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, 100);
            var offset = scroll.Offset;
            Require(offset.Y > 0, "The Files fixture must have a scrolled viewport.");
            var detaches = 0;
            same.DetachedFromVisualTree += (_, _) => detaches++;
            var gitReads = host.Requests.Count;
            var refresh = window.RefreshAsync();
            Pump(() => host.Requests.Count > gitReads);
            host.Requests.Last().Result.SetResult(new(true, "main", [], [], []));
            Await(refresh);
            Require(ReferenceEquals(tree, Tree()) && detaches == 0 && scroll.Offset == offset,
                "An identical listing must preserve the tree, row attachment and scroll offset.");
            host.OpenHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FilesHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var switching = window.OpenProjectAsync(other);
            Require(ReferenceEquals(tree, Tree()) && ReferenceEquals(same, Node("same.txt")),
                "Pending workspace routing must retain the Files tree and rows.");
            host.OpenHold.SetResult();
            Await(switching);
            Require(ReferenceEquals(tree, Tree()) && ReferenceEquals(folder, Node("folder")) && folder.IsExpanded,
                $"Held file loading must retain the tree and expanded folder: tree={ReferenceEquals(tree, Tree())}, folder={ReferenceEquals(folder, Node("folder"))}, expanded={folder.IsExpanded}, workspace={window.WorkspaceRoot}, project={window.ProjectRoot}.");
            host.FilesHold.SetResult();
            Pump(() => Listed("added.txt") && Listed(Path.Combine("folder", "added-child.txt")) && !Listed("removed.txt"));
            Require(ReferenceEquals(same, Node("same.txt")) && ReferenceEquals(child, Node(Path.Combine("folder", "child.txt"))),
                "Changed listings must retain unchanged root and nested rows, even with different file contents.");
            Require(detaches == 0 && scroll.Offset == offset, "Workspace reconciliation must preserve shared row attachment and scroll offset.");
            host.OpenHold = null; host.FilesHold = null;
            Await(window.OpenProjectAsync(project));
            Pump(() => Listed("removed.txt") && !Listed("added.txt"));
            Require(ReferenceEquals(tree, Tree()) && ReferenceEquals(same, Node("same.txt")) && folder.IsExpanded &&
                ReferenceEquals(child, Node(Path.Combine("folder", "child.txt"))), "Switching back must preserve shared rows and expansion.");
        }
        finally
        {
            window.Close();
            host.OpenHold?.TrySetResult(); host.FilesHold?.TrySetResult();
            foreach (var request in host.Requests) request.Result.TrySetResult(new(false, "", [], [], []));
        }
        Console.WriteLine("PASS Files tree and shared nested rows survive held workspace switches and changed listings");
    }

    private static void DeferredWorkspaceSwitch(string root)
    {
        var profile = new ProfileStore(Path.Combine(root, ".switch-profile"));
        var layout = new LayoutSession(profile.Data.Windows[0].Layout);
        layout.SwitchWorkspace(root);
        layout.Open(new("markdown:README.md", "README.md", "markdown", "README.md"), true);
        profile.Data.Windows[0].Layout = layout.State;
        var host = new DelayedGitHost(root)
        {
            OpenHold = new(TaskCreationOptions.RunContinuationsAsynchronously),
            FilesHold = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var window = new WorkbenchWindow(host, root, profile, E2eTerminals.Plain);
        window.Show();
        try
        {
            Pump(() => host.OpenStarted);
            Require(!window.WorkspaceMounted && host.Requests.IsEmpty && !host.FilesStarted,
                "Host routing must run before file or Git loading.");
            var tab = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Tab_markdown_README.md");
            Require(tab.Bounds.Width > 0 && window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Loading workspace…"),
                "The requested document tab and loading body must render while host routing is held.");
            host.OpenHold.SetResult();
            Pump(() => window.WorkspaceMounted && host.FilesStarted && host.Requests.Count == 1);
            Require(!host.FilesHold.Task.IsCompleted && window.GetLogicalDescendants().Contains(tab),
                "File loading must not block mounting or replace the requested tab.");
            var other = Path.Combine(root, "switch-other");
            Directory.CreateDirectory(other);
            host.OpenHold = null;
            Await(window.OpenProjectAsync(other));
            Require(window.WorkspaceRoot == other && window.WorkspaceMounted,
                "A pending file list must not hold the project-switch gate.");
            host.FilesHold.SetResult();
            Pump(() => host.Requests.Count == 2);
        }
        finally
        {
            window.Close();
            host.OpenHold?.TrySetResult(); host.FilesHold?.TrySetResult();
            foreach (var request in host.Requests) request.Result.TrySetResult(new(false, "", [], [], []));
        }
        Console.WriteLine("PASS workspace tabs render before held host routing and file loading does not block switching");
    }

    private static void DeferredDocumentChrome(string root)
    {
        var directory = Path.Combine(root, "deferred-document-chrome");
        using (var app = new E2eWorkspace(directory)) app.Open("README.md", true);
        var host = new E2eHost(new ProjectServices(directory));
        var read = host.Hold("README.md");
        var window = new WorkbenchWindow(host, directory, new ProfileStore(directory + "-profile"), E2E.E2eTerminals.Plain);
        window.Show();
        try
        {
            E2eWorkspace.Until(() => window.WorkspaceMounted && host.Reads.GetValueOrDefault("README.md") == 1);
            T Find<T>(string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            Require(window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Loading document…"),
                "The held restore read must expose a loading document before completion.");
            var tab = Find<Button>("Tab_markdown_README.md");
            var center = window.Layout.View.FocusedCenter;
            var header = Find<Grid>("GroupHeader_" + center);
            var separator = Find<ResizeHandle>("rightSeparator");
            Require(tab.Focus(), "The restored tab must accept keyboard focus while its content is pending.");
            read.SetResult();
            E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<MarkdownPreview>().Any());
            Require(ReferenceEquals(tab, Find<Button>(tab.Name!)) && tab.IsFocused &&
                ReferenceEquals(header, Find<Grid>(header.Name!)) && ReferenceEquals(separator, Find<ResizeHandle>(separator.Name!)),
                "Deferred document completion must replace only its body and preserve the tab, header, separator and focus.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS deferred restored document preserves dock chrome and keyboard focus");
    }

    private sealed class GitRequest(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<GitSnapshot> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DelayedGitHost(string root) : IProjectServices
    {
        public IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync(CancellationToken cancellationToken = default) => new ProjectServices(root).WatchFilesAsync(cancellationToken);
        private readonly ProjectServices inner = new(root);
        public ConcurrentQueue<GitRequest> Requests { get; } = new();
        public TaskCompletionSource? OpenHold { get; set; }
        public TaskCompletionSource? FilesHold { get; set; }
        public bool OpenStarted { get; private set; }
        public bool FilesStarted { get; private set; }
        public ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken ct = default) => inner.SaveFileAsync(request, ct);
        public async ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken ct = default)
        {
            OpenStarted = true;
            if (OpenHold is { } hold) await hold.Task.WaitAsync(ct);
            return await inner.OpenProjectAsync(path, ct);
        }
        public async ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string path, CancellationToken ct = default)
        {
            var files = await inner.ListFilesAsync(path, ct);
            FilesStarted = true;
            if (FilesHold is { } hold) await hold.Task.WaitAsync(ct);
            return files;
        }
        public ValueTask<FileDocument> ReadFileAsync(string path, CancellationToken ct = default) => inner.ReadFileAsync(path, ct);
        public ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken ct = default) => inner.ListSpecsAsync(ct);
        public ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken ct = default) => inner.GetSpecGraphAsync(ct);
        public ValueTask<bool> HasDurableSpecsAsync(CancellationToken ct = default) => inner.HasDurableSpecsAsync(ct);
        public ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken ct = default) => inner.InspectProjectPathAsync(path, ct);
        public ValueTask PrewarmWorkspaceAsync(string path, CancellationToken ct = default) => inner.PrewarmWorkspaceAsync(path, ct);
        public ValueTask<SearchHits> SearchAsync(string query, CancellationToken ct = default) => inner.SearchAsync(query, ct);
        public ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparison, CancellationToken ct = default) => inner.ListCommitsAsync(comparison, ct);
        public ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken ct = default) => inner.GetCommitAsync(sha, ct);
        public ValueTask<string> CreateProjectAsync(string parentPath, string name, CancellationToken ct = default) => inner.CreateProjectAsync(parentPath, name, ct);
        public ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken ct = default) => inner.CloneProjectAsync(url, parentPath, name, depth, ct);
        public ValueTask<string> GetDiffAsync(string path, string scope, string comparison = "", CancellationToken ct = default) => inner.GetDiffAsync(path, scope, comparison, ct);
        public ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparison = "", CancellationToken ct = default) => inner.GetDiffSidesAsync(path, scope, comparison, ct);
        public ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken ct = default) => inner.ReadContentBytesAsync(path, revision, ct);
        public ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparison, RevertTarget target, ChangeExpectation expect, CancellationToken ct = default) => inner.RevertChangeAsync(path, scope, comparison, target, expect, ct);
        public ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken ct = default) => inner.UndoChangeAsync(receiptId, expectModifiedHash, ct);
        public ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken ct = default) => inner.ApplyGitActionAsync(action, ct);
        public ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken ct = default) => ValueTask.FromResult(new BranchCatalog([], [], ""));
        public ValueTask<OpenReview?> GetOpenReviewAsync(bool fresh, CancellationToken ct = default) => ValueTask.FromResult<OpenReview?>(null);
        public ValueTask<PrDraft> PreviewPrAsync(CancellationToken ct = default) => ValueTask.FromResult(new PrDraft("", ""));
        public ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken ct = default) => ValueTask.FromResult(new PrResult("compare", "", 0, 0, ""));
        public ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken ct = default) => inner.ListEditorsAsync(ct);
        public ValueTask ApplyFileActionAsync(FileAction action, CancellationToken ct = default) => inner.ApplyFileActionAsync(action, ct);
        public ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken ct = default) => inner.OpenInEditorAsync(editorId, worktreePath, ct);
        public ValueTask<WorkspaceCatalog> ListWorkspacesAsync(string projectRoot, CancellationToken ct = default) => inner.ListWorkspacesAsync(projectRoot, ct);
        public ValueTask<WorkspaceRecord?> ApplyWorkspaceActionAsync(WorkspaceAction action, CancellationToken ct = default) => inner.ApplyWorkspaceActionAsync(action, ct);
        public async ValueTask<GitSnapshot> GetGitAsync(string comparison = "", CancellationToken ct = default, string scope = "all")
        {
            var request = new GitRequest(ct);
            Requests.Enqueue(request);
            return await request.Result.Task;
        }
    }
}