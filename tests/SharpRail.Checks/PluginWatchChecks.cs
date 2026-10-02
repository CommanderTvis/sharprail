using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.Checks;

// A plugin's explicit WatchWorkspaceAsync of a workspace no window has mounted, as the fork's
// watchWorkspaceForLiveContent: one shared watch per workspace, readiness from the host's first batch, broad
// invalidation at readiness and after a dropped stream, and the watch ending with the activations holding it.
internal static class PluginWatchChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        var local = Path.Combine(root, "plugin-watch-local");
        Directory.CreateDirectory(local);
        Cases(Path.Combine(root, "plugin-watch-local-app"), local, () => new LocalProjectAdapter(new ProjectServices(local)));

        var remote = Path.Combine(root, "plugin-watch-remote");
        Directory.CreateDirectory(remote);
        var server = RemoteServer.Create(remote, IPAddress.Loopback, 0, "plugin-watch", Path.Combine(root, "plugin-watch-state"));
        Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            Cases(Path.Combine(root, "plugin-watch-remote-app"), remote, () => new RemoteProjectAdapter(address, "plugin-watch"));
        }
        finally { Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult(); }

        Unsessioned(Path.Combine(root, "plugin-watch-fixture"), local);
        Console.WriteLine("PASS plugin workspace watching: local/gRPC inactive workspaces, shared preparation, held readiness, drop recovery, disable and fixtures");
    }

    private static void Cases(string app, string workspace, Func<IProjectServices> open)
    {
        var sessions = new List<Gated>();
        using var fixture = new Fixture(app, () => { var session = new Gated(open()); lock (sessions) sessions.Add(session); return session; });
        var (first, second) = (fixture.First.Context!, fixture.Second.Context!);

        var held = 0;
        using var heldObserver = first.ObserveFileRevision(workspace, "held.txt", revision => held = revision);
        var a = first.WatchWorkspaceAsync(workspace).AsTask();
        var b = first.WatchWorkspaceAsync(workspace).AsTask();
        var c = second.WatchWorkspaceAsync(workspace).AsTask();
        E2eWorkspace.Until(() => sessions.Count == 1 && sessions[0].Watching);
        File.WriteAllText(Path.Combine(workspace, "held.txt"), "written while readiness is held");
        E2eWorkspace.Settle(300);
        Require(!a.IsCompleted && !b.IsCompleted && !c.IsCompleted, "Watching completes only when the host's first batch arrives.");
        Require(sessions.Count == 1, "Concurrent calls from one or several activations share one watch.");
        sessions[0].Gate.SetResult();
        E2eWorkspace.Until(() => a.IsCompletedSuccessfully && b.IsCompletedSuccessfully && c.IsCompletedSuccessfully);
        Require(held > 0, "Readiness invalidates paths that may have changed while the watch was being prepared.");

        var written = 0;
        using var writtenObserver = first.ObserveFileRevision(workspace, "inactive.txt", revision => written = revision);
        File.WriteAllText(Path.Combine(workspace, "inactive.txt"), "x");
        E2eWorkspace.Until(() => written > 0);
        var repeated = first.WatchWorkspaceAsync(workspace).AsTask();
        Require(repeated.IsCompletedSuccessfully && sessions.Count == 1, "A workspace already watched is ready at once.");

        var before = held;
        sessions[0].Drop = true;
        File.WriteAllText(Path.Combine(workspace, "trigger.txt"), "drops the stream");
        E2eWorkspace.Until(() => held > before);
        Require(sessions.Count == 1, "A dropped stream is restored on the same watch, invalidating what it may have missed.");

        fixture.Enable("watch-a", false);
        E2eWorkspace.Settle(200);
        Require(!sessions[0].Disposed, "The watch stays while another activation holds it.");
        fixture.Enable("watch-b", false);
        E2eWorkspace.Until(() => sessions[0].Disposed);
        fixture.Enable("watch-a", true);
        E2eWorkspace.Until(() => fixture.First.Context != first);
        var again = fixture.First.Context!.WatchWorkspaceAsync(workspace).AsTask();
        E2eWorkspace.Until(() => sessions.Count == 2);
        sessions[1].Gate.SetResult();
        E2eWorkspace.Until(() => again.IsCompletedSuccessfully);
        fixture.Enable("watch-a", false);
        E2eWorkspace.Until(() => sessions[1].Disposed);

        var previous = fixture.First.Context;
        fixture.Enable("watch-a", true);
        E2eWorkspace.Until(() => fixture.First.Context != previous);
        var pending = fixture.First.Context!.WatchWorkspaceAsync(workspace).AsTask();
        E2eWorkspace.Until(() => sessions.Count == 3 && sessions[2].Watching);
        fixture.Enable("watch-a", false);
        E2eWorkspace.Until(() => sessions[2].Disposed && pending.IsCompleted);
        Require(!pending.IsCompletedSuccessfully, "Disabling the plugin abandons a watch still being prepared.");
    }

    private static void Unsessioned(string app, string workspace)
    {
        using var fixture = new Fixture(app, null);
        var ready = fixture.First.Context!.WatchWorkspaceAsync(workspace).AsTask();
        Require(ready.IsCompletedSuccessfully, "Without project sessions, watching completes at once and leaves revisions to mounted windows.");
    }

    private sealed class Probe : PluginUIModule
    {
        internal IPluginUIContext? Context;
        public override PluginDisposer? Activate(IPluginUIContext context) { Context = context; return null; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SharedState state;
        private readonly Workbench workbench;
        private readonly PluginLoader loader;
        internal Probe First { get; } = new();
        internal Probe Second { get; } = new();

        internal Fixture(string root, Func<IProjectServices>? sessions)
        {
            Directory.CreateDirectory(root);
            var profile = new ProfileStore(root + "-profile");
            var store = profile.OpenState();
            static PluginManifest Manifest(string id) => new(id, id, "puzzle", "1", PluginApi.Generation, 1);
            var host = new FakePluginHost(new LocalStateAdapter(store), store.Current,
                new("watch-a", "A", "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled),
                new("watch-b", "B", "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled));
            state = new SharedState(host, profile.Data.Preferences);
            workbench = new Workbench(profile, state, E2eTerminals.Plain, false, sessions, host);
            loader = new PluginLoader(workbench, [(Manifest("watch-a"), () => First), (Manifest("watch-b"), () => Second)]);
            loader.Start();
            _ = state.ChangeAsync(HostStateChange.PluginEnabled("watch-a", true), HostStateChange.PluginEnabled("watch-b", true));
            E2eWorkspace.Until(() => loader.Registry.Active.Contains("watch-a") && loader.Registry.Active.Contains("watch-b") && First.Context is not null && Second.Context is not null);
        }

        internal void Enable(string id, bool enabled)
        {
            _ = state.ChangeAsync(HostStateChange.PluginEnabled(id, enabled));
            E2eWorkspace.Until(() => loader.Registry.Active.Contains(id) == enabled && loader.Idle.IsCompleted);
        }

        public void Dispose() { loader.Stop(); workbench.Dispose(); }
    }

    // A project session that holds its first ready batch until released and can drop its stream once.
    private sealed class Gated(IProjectServices inner) : IProjectServices, IDisposable
    {
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Watching;
        internal volatile bool Drop;
        internal volatile bool Disposed;

        public void Dispose() { Disposed = true; (inner as IDisposable)?.Dispose(); }

        public async IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = !Gate.Task.IsCompleted;
            await foreach (var changes in inner.WatchFilesAsync(cancellationToken))
            {
                Watching = true;
                if (first) { first = false; await Gate.Task.WaitAsync(cancellationToken); }
                if (Drop) { Drop = false; throw new IOException("The stream dropped."); }
                yield return changes;
            }
        }

        public ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default) => inner.OpenProjectAsync(path, cancellationToken);
        public ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default) => inner.ListFilesAsync(relativePath, cancellationToken);
        public ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default) => inner.SaveFileAsync(request, cancellationToken);
        public ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default) => inner.ReadFileAsync(relativePath, cancellationToken);
        public ValueTask<SearchHits> SearchAsync(string query, CancellationToken cancellationToken = default) => inner.SearchAsync(query, cancellationToken);
        public ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all") => inner.GetGitAsync(comparisonBranch, cancellationToken, scope);
        public ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default) => inner.ListCommitsAsync(comparisonBranch, cancellationToken);
        public ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) => inner.GetCommitAsync(sha, cancellationToken);
        public ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken ct = default) => inner.ListSpecsAsync(ct);
        public ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken ct = default) => inner.GetSpecGraphAsync(ct);
        public ValueTask<bool> HasDurableSpecsAsync(CancellationToken ct = default) => inner.HasDurableSpecsAsync(ct);
        public ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken ct = default) => inner.InspectProjectPathAsync(path, ct);
        public ValueTask PrewarmWorkspaceAsync(string path, CancellationToken ct = default) => inner.PrewarmWorkspaceAsync(path, ct);
        public ValueTask<WorkspaceRecord?> ApplyWorkspaceActionAsync(WorkspaceAction action, CancellationToken ct = default) => inner.ApplyWorkspaceActionAsync(action, ct);
        public ValueTask<WorkspaceCatalog> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default) => inner.ListWorkspacesAsync(projectRoot, cancellationToken);
        public ValueTask<string> CreateProjectAsync(string parentPath, string name, CancellationToken cancellationToken = default) => inner.CreateProjectAsync(parentPath, name, cancellationToken);
        public ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken cancellationToken = default) => inner.CloneProjectAsync(url, parentPath, name, depth, cancellationToken);
        public ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default) => inner.GetDiffAsync(path, scope, comparisonBranch, cancellationToken);
        public ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default) => inner.GetDiffSidesAsync(path, scope, comparisonBranch, cancellationToken);
        public ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken ct = default) => inner.ReadContentBytesAsync(path, revision, ct);
        public ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparison, RevertTarget target, ChangeExpectation expect, CancellationToken ct = default) => inner.RevertChangeAsync(path, scope, comparison, target, expect, ct);
        public ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken ct = default) => inner.UndoChangeAsync(receiptId, expectModifiedHash, ct);
        public ValueTask<OpenReview?> GetOpenReviewAsync(bool fresh, CancellationToken ct = default) => inner.GetOpenReviewAsync(fresh, ct);
        public ValueTask<PrDraft> PreviewPrAsync(CancellationToken ct = default) => inner.PreviewPrAsync(ct);
        public ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken ct = default) => inner.OpenPrAsync(request, ct);
        public ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default) => inner.ApplyGitActionAsync(action, cancellationToken);
        public ValueTask ApplyFileActionAsync(FileAction action, CancellationToken cancellationToken = default) => inner.ApplyFileActionAsync(action, cancellationToken);
        public ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default) => inner.ListBranchesAsync(fetchDefault, cancellationToken);
        public ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default) => inner.ListEditorsAsync(cancellationToken);
        public ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default) => inner.OpenInEditorAsync(editorId, worktreePath, cancellationToken);
    }
}