using System.Collections.Concurrent;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;
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
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Dispatcher.UIThread.RunJobs(); Require(done(), "Startup operation timed out.");
    }

    private static void Await(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static string? Branch(WorkbenchWindow window) => window.FindControl<TextBlock>("BranchLabel")!.Text;

    internal static void Run(string root)
    {
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
        Require(window.WorkspaceMounted && window.FindControl<TextBlock>("ConnectionStatus")!.Text == "Connected",
            "Git failure made an accessible workspace unusable.");
        window.Close();
        Console.WriteLine("PASS usable fresh/restored startup with pending Git, cancellation, project switching, stale results and Git failure");
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
        private readonly ProjectServices inner = new(root);
        public ConcurrentQueue<GitRequest> Requests { get; } = new();
        public IAsyncEnumerable<FileChange> WatchFilesAsync(CancellationToken ct = default) => inner.WatchFilesAsync(ct);
        public ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken ct = default) => inner.SaveFileAsync(request, ct);
        public ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken ct = default) => inner.OpenProjectAsync(path, ct);
        public ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string path, CancellationToken ct = default) => inner.ListFilesAsync(path, ct);
        public ValueTask<FileDocument> ReadFileAsync(string path, CancellationToken ct = default) => inner.ReadFileAsync(path, ct);
        public ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken ct = default) => inner.ListSpecsAsync(ct);
        public ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken ct = default) => inner.GetSpecGraphAsync(ct);
        public ValueTask<bool> HasDurableSpecsAsync(CancellationToken ct = default) => inner.HasDurableSpecsAsync(ct);
        public ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken ct = default) => inner.InspectProjectPathAsync(path, ct);
        public ValueTask PrewarmWorkspaceAsync(string path, CancellationToken ct = default) => inner.PrewarmWorkspaceAsync(path, ct);
        public ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparison, CancellationToken ct = default) => inner.ListCommitsAsync(comparison, ct);
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
        public ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken ct = default) => inner.OpenInEditorAsync(editorId, worktreePath, ct);
        public async ValueTask<GitSnapshot> GetGitAsync(string comparison = "", CancellationToken ct = default, string scope = "all")
        {
            var request = new GitRequest(ct);
            Requests.Enqueue(request);
            return await request.Result.Task;
        }
    }
}