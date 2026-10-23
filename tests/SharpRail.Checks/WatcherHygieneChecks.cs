using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.Blueprint.Host;
using SharpRail.Plugins.SpecDialect;

namespace SharpRail.Checks;

internal static class WatcherHygieneChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Balanced(WorkspaceWatches.WatchAccounting baseline)
    {
        var current = WorkspaceWatches.Accounting();
        Require(current.Created - current.Disposed == current.Allocated, "Every allocated managed watcher must have exactly one live registry entry.");
        Require(current.Allocated == baseline.Allocated && current.Subscribers == baseline.Subscribers && current.Retries == baseline.Retries,
            "Watcher allocations, logical leases and queued retries must return to baseline.");
    }

    internal static async Task Run(string root)
    {
        await StateObservationContext(root);
        await NativeFSEventsChecks.ProveInterception(root);
        var nativeBaseline = NativeFSEventsChecks.Snapshot();
        _ = WatcherTelemetry.Log; // Attach to an existing provider, as diagnostics do in a running host.
        using var telemetry = new Listener();
        var baseline = WorkspaceWatches.Accounting();
        var project = Path.Combine(root, "watcher-hygiene");
        Directory.CreateDirectory(project);
        var worktree = project + "-worktree";
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(project, "SPEC.md"), "---\nid: hygiene\ntype: module-design\n---\n# Hygiene\n");
        File.WriteAllText(Path.Combine(project, "BLUEPRINT.md"), "# Blueprint\n");
        var state = new HostStateStore(Path.Combine(root, "watcher-hygiene-state"));
        // Specs and Blueprint are off by default; this check reads through both.
        await state.ChangeAsync([HostStateChange.OpenProject(project), HostStateChange.PluginEnabled(SpecDialectContract.Id, true), HostStateChange.PluginEnabled("blueprint", true)]);
        await using (var runtime = new PluginRuntime(new()
        {
            State = state,
            StateDirectory = Path.Combine(root, "watcher-hygiene-state"),
            Builtins = [(SpecDialectManifest.Manifest, new SpecDialectHost()), (BlueprintContract.Manifest, new BlueprintHost())],
            Worktrees = (_, _) => Task.FromResult<IReadOnlyList<WorktreeInfo>>([new(project, "main", true, false), new(worktree, "feature", false, false)])
        }))
        {
            await runtime.Start();
            async Task Read()
            {
                await runtime.CallAsync(new(SpecDialectContract.Id, SpecDialectContract.Graph.Name, new SpecGraphParams(project), "hygiene"));
                await runtime.CallAsync(new("blueprint", BlueprintContract.Get.Name, new BlueprintScope(project), "hygiene"));
            }
            await Read();
            var admitted = WorkspaceWatches.Accounting();
            var nativeAdmitted = NativeFSEventsChecks.Snapshot();
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Read()));
            for (var i = 0; i < 32; i++) await Read();
            NativeFSEventsChecks.Plateau(nativeAdmitted);
            var watched = WorkspaceWatches.Accounting();
            var workspace = watched.Roots.Single(entry => entry.Root == project);
            Require(watched.Created == admitted.Created && watched.Allocated == admitted.Allocated && workspace.Owners.Count == 2 && workspace.AllocatedPaths.Count == 1,
                "Concurrent/repeated real Specs and Blueprint reads must plateau at one shared managed watcher and one lease per activation.");
            Require(workspace.Owners.Any(owner => owner.StartsWith("plugin:spec-dialect:", StringComparison.Ordinal)) &&
                workspace.Owners.Any(owner => owner.StartsWith("plugin:blueprint:", StringComparison.Ordinal)), "Every retained subscription identifies its plugin activation owner.");
            using (WorkspaceWatches.Subscribe(project + Path.DirectorySeparatorChar, _ => { }, owner: "hygiene-client"))
            {
                var shared = WorkspaceWatches.Accounting();
                Require(shared.Created == admitted.Created && shared.Roots.Single(entry => entry.Root == project).Owners.Count == 3,
                    "A client stream and canonical path alias reuse the plugins' existing watcher set.");
            }
            await state.ChangeAsync([HostStateChange.PluginEnabled("blueprint", false)]);
            await Until(() => WorkspaceWatches.Inspect(project)?.Subscribers == 1);
            Require(WorkspaceWatches.Accounting().Created == admitted.Created, "Deactivating one plugin preserves the other plugin's watcher without recreation.");
            state.ChangeWorkspaces(_ => [
                new("default-hygiene", project, WorkspaceKinds.Default, project, "main", "main"),
                new("external-hygiene", project, WorkspaceKinds.External, worktree, "feature", "main")]);
            await runtime.CallAsync(new(SpecDialectContract.Id, SpecDialectContract.Graph.Name, new SpecGraphParams(worktree), "hygiene"));
            Require(WorkspaceWatches.Inspect(worktree)?.Subscribers == 1, "Reading another worktree owns one named activation lease.");
            state.ChangeWorkspaces(current => current.Where(workspace => workspace.Path != worktree));
            Require(WorkspaceWatches.Inspect(worktree) is null,
                "Workspace removal releases leases synchronously even when add/remove snapshots coalesce.");
            await state.ChangeAsync([HostStateChange.CloseProject(project)]);
            await Until(() => WorkspaceWatches.Inspect(project) is null);
            Balanced(baseline);
            await state.ChangeAsync([HostStateChange.OpenProject(project)]);
            await runtime.CallAsync(new(SpecDialectContract.Id, SpecDialectContract.Graph.Name, new SpecGraphParams(project), "hygiene"));
            Require(WorkspaceWatches.Inspect(project)?.Subscribers == 1, "A reopened project acquires a fresh activation lease.");
        }
        Balanced(baseline);
        await AdmissionRace(root, deactivate: false);
        await AdmissionRace(root, deactivate: true);
        Require(telemetry.Events.Contains(1) && telemetry.Events.Contains(2) && !telemetry.Events.Contains(0),
            "The EventSource must emit valid allocation and ownership events without provider errors: " + string.Join("; ", telemetry.Errors));
        Balanced(baseline);
        await NativeFSEventsChecks.Returned(nativeBaseline);
        Console.WriteLine("PASS watcher hygiene: repeated/concurrent Specs and Blueprint reads plateau, client sharing, activation ownership, disable, project close/reopen and runtime teardown return to baseline");
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        private int posts;
        public int Posts => Volatile.Read(ref posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private static async Task StateObservationContext(string root)
    {
        var context = new RecordingContext();
        var state = new HostStateStore(Path.Combine(root, "watcher-context-state"));
        await using var runtime = new PluginRuntime(new()
        {
            State = state,
            StateDirectory = null,
            Builtins = [(SpecDialectManifest.Manifest, new SpecDialectHost())]
        });
        var previous = SynchronizationContext.Current;
        Task starting;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            starting = runtime.Start();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await starting.WaitAsync(TimeSpan.FromSeconds(10));
        await state.ChangeAsync([HostStateChange.PluginEnabled(SpecDialectContract.Id, false)]);
        await runtime.Schedule();
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Require(context.Posts == 0, "Host state observation and cancellation must not capture the context used to start plugin activation.");
    }

    private sealed class Listener : EventListener
    {
        private ConcurrentQueue<int>? events;
        private ConcurrentQueue<string>? errors;
        public ConcurrentQueue<int> Events => LazyInitializer.EnsureInitialized(ref events);
        public ConcurrentQueue<string> Errors => LazyInitializer.EnsureInitialized(ref errors);
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "SharpRail-WorkspaceWatchers") EnableEvents(source, EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            Events.Enqueue(data.EventId);
            if (data.EventId == 0) Errors.Enqueue(string.Join(", ", data.Payload ?? []));
        }
    }

    private static async Task AdmissionRace(string root, bool deactivate)
    {
        var project = Path.Combine(root, "watcher-admission-" + deactivate);
        Directory.CreateDirectory(project);
        var state = new HostStateStore(Path.Combine(root, "watcher-admission-state-" + deactivate));
        // Specs and Blueprint are off by default; this check reads through both.
        await state.ChangeAsync([HostStateChange.OpenProject(project), HostStateChange.PluginEnabled(SpecDialectContract.Id, true), HostStateChange.PluginEnabled("blueprint", true)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var runtime = new PluginRuntime(new()
        {
            State = state,
            StateDirectory = null,
            Builtins = [(SpecDialectManifest.Manifest, new SpecDialectHost())],
            Worktrees = async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    entered.TrySetResult();
                    await resume.Task;
                }
                return [];
            }
        });
        await runtime.Start();
        var reading = runtime.CallAsync(new(SpecDialectContract.Id, SpecDialectContract.Graph.Name, new SpecGraphParams(project), "hygiene-race")).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await state.ChangeAsync([deactivate ? HostStateChange.PluginEnabled(SpecDialectContract.Id, false) : HostStateChange.CloseProject(project)]);
        if (deactivate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await runtime.ListAsync()).Single(entry => entry.Id == SpecDialectContract.Id).Status != PluginStatus.Disabled)
                await Task.Delay(20, timeout.Token);
        }
        resume.TrySetResult();
        await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Require(WorkspaceWatches.Inspect(project) is null, "A delayed admission must not retain watchers after project close or activation teardown.");
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
}