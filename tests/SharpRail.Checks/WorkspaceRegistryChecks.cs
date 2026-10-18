using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>The workspace registry through direct and WebSocket RPC adapters: the same scenario must leave the same records, events and files.</summary>
internal static class WorkspaceRegistryChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Git(string directory, params string[] arguments) => IsolatedGit.Run(directory, arguments).Trim();

    private static async Task<string> Refused(Func<ValueTask<WorkspaceRecord?>> action, string what)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or SharpRail.Host.Protocol.RpcException)
        {
            return error is SharpRail.Host.Protocol.RpcException rpc ? rpc.Status.Detail : error.Message;
        }
        throw new InvalidOperationException(what);
    }

    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "workspace-registry");
        using var git = new IsolatedGit(Path.Combine(directory, "git"));
        var localState = Path.Combine(directory, "local-state");
        var store = new HostStateStore(localState);
        var localLog = await Scenario(new LocalProjectAdapter(new ProjectServices(directory, store)), new LocalStateAdapter(store), Path.Combine(directory, "local"));
        Require(Describe(new HostStateStore(localState).Current.Workspaces, Path.Combine(directory, "local")) == localLog[^1],
            "The registry must persist beside the host and reload with the same ids, kinds and markers.");

        var remoteState = Path.Combine(directory, "remote-state");
        await using var server = RemoteServer.Create(directory, IPAddress.Loopback, 0, "registry-test", remoteState);
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemoteProjectAdapter(address, "registry-test");
            using var remoteService = new RemoteStateAdapter(address, "registry-test");
            var remoteLog = await Scenario(remote, remoteService, Path.Combine(directory, "remote"));
            Require(localLog.SequenceEqual(remoteLog), "Local and remote registries differ:\n" +
                string.Join('\n', localLog.Zip(remoteLog).Where(pair => pair.First != pair.Second).Select(pair => pair.First + "\n  <>  " + pair.Second)));
        }
        finally { await server.StopAsync(); }
        await Settling(Path.Combine(directory, "settling"));
        Console.WriteLine("PASS workspace registry: ids, kinds, attach, names, no-track, pending marker, folder truth, reclaim, scratch directory and pushed lifecycle at local/remote parity");
    }

    /// <summary>
    /// Settled workspaces' host facts: overrides through both adapters, Default refused, the idle-day setting, activity
    /// clearing overrides and coalescing, HEAD moves across watcher restarts, terminal input and the upgrade backfill.
    /// </summary>
    private static async Task Settling(string directory)
    {
        async Task<List<string>> Scenario(IProjectServices host, IHostStateService state, string root)
        {
            var log = new List<string>();
            var repo = IsolatedGit.Repository(Path.Combine(root, "project"));
            await state.ChangeAsync([HostStateChange.OpenProject(repo)]);
            var catalog = await host.ListWorkspacesAsync(repo);
            var created = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "Quiet")))!;
            var listed = (await state.GetStateAsync()).Workspaces.Single(workspace => workspace.Id == created.Id);
            Require(listed.LastActiveAt is > 0 && listed.SettledOverride is null && catalog.Workspaces[0].LastActiveAt is > 0,
                "A created workspace and the Default are stamped live by construction.");
            var settled = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Settle(created.Id)))!;
            log.Add($"settle {settled.SettledOverride} {settled.SettledAt is > 0}");
            var mirrored = (await state.GetStateAsync()).Workspaces.Single(workspace => workspace.Id == created.Id);
            Require(mirrored.SettledOverride == SettledOverrides.Settled && mirrored.SettledAt == settled.SettledAt, "Host state carries the override and its stamp.");
            log.Add($"keep {(await host.ApplyWorkspaceActionAsync(WorkspaceAction.Unsettle(created.Id)))!.SettledOverride}");
            log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Settle(catalog.Workspaces[0].Id)), "The Default workspace settled."));
            log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Unsettle(catalog.Workspaces[0].Id)), "The Default workspace was pinned."));
            Require((await state.GetStateAsync()).Settings.SettleIdleDays == HostSettings.DefaultSettleIdleDays, "The idle window defaults to a week.");
            await state.ChangeAsync([HostStateChange.Setting("settle-idle-days", "never")]);
            log.Add($"never {(await state.GetStateAsync()).Settings.SettleIdleDays?.ToString() ?? "null"}");
            await state.ChangeAsync([HostStateChange.Setting("settle-idle-days", "3")]);
            log.Add($"three {(await state.GetStateAsync()).Settings.SettleIdleDays}");
            try { await state.ChangeAsync([HostStateChange.Setting("settle-idle-days", "0")]); log.Add("zero accepted"); }
            catch (Exception error) when (error is ArgumentException or SharpRail.Host.Protocol.RpcException) { log.Add("zero refused"); }
            return log;
        }
        var store = new HostStateStore(Path.Combine(directory, "local-state"));
        var local = await Scenario(new LocalProjectAdapter(new ProjectServices(directory, store)), new LocalStateAdapter(store), Path.Combine(directory, "local"));
        var remoteState = Path.Combine(directory, "remote-state");
        await using (var server = RemoteServer.Create(directory, IPAddress.Loopback, 0, "settling-test", remoteState))
        {
            await server.StartAsync();
            try
            {
                var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var remote = new RemoteProjectAdapter(address, "settling-test");
                using var remoteService = new RemoteStateAdapter(address, "settling-test");
                Require((await remoteService.GetHandshakeAsync()).Supports(HostProtocol.WorkspaceSettle), "A current host advertises settling.");
                var remoteLog = await Scenario(remote, remoteService, Path.Combine(directory, "remote"));
                Require(local.SequenceEqual(remoteLog), "Local and remote settling differ:\n" + string.Join("\n", local) + "\n  <>\n" + string.Join("\n", remoteLog));
            }
            finally { await server.StopAsync(); }
        }
        Require(local[0] == "settle settled True" && local[1] == "keep active" && local[2].Contains("never settles") && local[4] == "never null" &&
            local[5] == "three 3" && local[6] == "zero refused", "Settling scenario: " + string.Join(" | ", local));

        // Real activity clears an override and is coalesced otherwise; the clock is the store's.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10L * 24 * 60 * 60 * 1000;
        store.Clock = () => now;
        var quiet = store.Current.Workspaces.Single(workspace => workspace.Kind == WorkspaceKinds.Managed);
        await new LocalProjectAdapter(new ProjectServices(directory, store)).ApplyWorkspaceActionAsync(WorkspaceAction.Settle(quiet.Id));
        store.RecordWorkspaceActivity(quiet.Path);
        var cleared = store.Current.Workspaces.Single(workspace => workspace.Id == quiet.Id);
        Require(cleared is { SettledOverride: null, SettledAt: null } && cleared.LastActiveAt == now, "Activity supersedes an explicit park.");
        var revision = store.Current.Revision;
        now += 30_000;
        store.RecordWorkspaceActivity(quiet.Path);
        Require(store.Current.Revision == revision, "Activity within a minute of the stored stamp writes nothing.");
        now += 60_000;
        store.RecordWorkspaceActivity(quiet.Path);
        Require(store.Current.Workspaces.Single(workspace => workspace.Id == quiet.Id).LastActiveAt == now, "Activity after the coalescing window is stamped.");

        // HEAD: the first observation seeds; a move, even while no watcher ran, is activity.
        now += 120_000;
        store.ObserveWorkspaceHead(quiet.Path);
        Require(store.Current.Workspaces.Single(workspace => workspace.Id == quiet.Id).LastActiveAt == now - 120_000, "Seeding HEAD is not activity.");
        File.WriteAllText(Path.Combine(quiet.Path, "work.txt"), "work\n");
        Git(quiet.Path, "add", "work.txt");
        Git(quiet.Path, "commit", "-m", "work");
        store.ObserveWorkspaceHead(quiet.Path);
        Require(store.Current.Workspaces.Single(workspace => workspace.Id == quiet.Id).LastActiveAt == now, "A moved HEAD is activity.");
        now += 120_000;
        store.ObserveWorkspaceHead(quiet.Path);
        Require(store.Current.Workspaces.Single(workspace => workspace.Id == quiet.Id).LastActiveAt == now - 120_000, "An unchanged HEAD is not activity.");

        // Accepted terminal input is activity in the terminal's workspace.
        now += 120_000;
        await using (var terminals = new PtyTerminalService())
        {
            store.Terminals = terminals;
            await using var shell = await terminals.AttachAsync(new("settling-input", quiet.Path, "settling-client", 80, 24));
            await shell.WriteAsync("\n"u8.ToArray());
            Require(store.Current.Workspaces.Single(workspace => workspace.Id == quiet.Id).LastActiveAt == now, "Accepted terminal input is activity.");
            store.Terminals = null;
        }

        // An upgraded registry without stamps backfills from the worktree's .git file, and a missing folder gets now.
        var legacy = Path.Combine(directory, "legacy-state");
        Directory.CreateDirectory(legacy);
        var marker = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(quiet.Path, ".git"))).ToUnixTimeMilliseconds();
        var gone = Path.Combine(directory, "gone");
        File.WriteAllText(Path.Combine(legacy, HostStateStore.FileName), System.Text.Json.JsonSerializer.Serialize(new
        {
            Workspaces = new object[]
            {
                new { Id = "a", ProjectRoot = quiet.ProjectRoot, Kind = WorkspaceKinds.Managed, Path = quiet.Path, Branch = quiet.Branch, BaseBranch = quiet.BaseBranch },
                new { Id = "b", ProjectRoot = quiet.ProjectRoot, Kind = WorkspaceKinds.Managed, Path = gone, Branch = "gone", BaseBranch = "main" },
                new { Id = "c", ProjectRoot = quiet.ProjectRoot, Kind = WorkspaceKinds.Default, Path = quiet.ProjectRoot, Branch = "main", BaseBranch = "main", SettledOverride = "settled" }
            }
        }));
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var upgraded = new HostStateStore(legacy).Current.Workspaces;
        Require(upgraded.Single(workspace => workspace.Id == "a").LastActiveAt == marker && upgraded.Single(workspace => workspace.Id == "b").LastActiveAt >= before &&
            upgraded.Single(workspace => workspace.Id == "c").SettledOverride is null, "Backfill reads the worktree's .git time, degrades to now, and Default never carries an override.");
        Require(new HostStateStore(legacy).Current.Workspaces.Single(workspace => workspace.Id == "a").LastActiveAt == marker, "The backfill is persisted once.");
        Console.WriteLine("PASS settled workspaces: overrides, Default refusal, idle setting at local/remote parity; activity, coalescing, HEAD, terminal input and backfill");
    }

    private static string Describe(IEnumerable<WorkspaceRecord> workspaces, string root) => string.Join(" | ", workspaces.Select(workspace =>
        $"{workspace.Kind} {Path.GetRelativePath(root, workspace.Path)} {workspace.Branch} from {workspace.BaseBranch}{(workspace.InitialTerminalPending ? " pending" : "")}"));

    private static async Task<List<string>> Scenario(IProjectServices host, IHostStateService state, string root)
    {
        var log = new List<string>();
        var repo = IsolatedGit.Repository(Path.Combine(root, "project"));
        await state.ChangeAsync([HostStateChange.OpenProject(repo)]);

        // The lifecycle channel replays nothing, so the scenario starts once a first event proves the subscription is live.
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var events = new List<LifecycleEvent>();
        var reader = Task.Run(async () =>
        {
            try { await foreach (var item in state.WatchLifecycleAsync(lifetime.Token)) lock (events) events.Add(item); }
            catch (Exception error) when (error is OperationCanceledException or SharpRail.Host.Protocol.RpcException) { }
        });
        async Task Seen(Func<LifecycleEvent, bool> match, Func<Task>? nudge = null)
        {
            while (true)
            {
                lock (events) if (events.Any(match)) return;
                lifetime.Token.ThrowIfCancellationRequested();
                if (nudge is not null) await nudge();
                await Task.Delay(50);
            }
        }
        var probe = Path.Combine(root, "probe");
        await Seen(item => item.ProjectRoot == probe, async () =>
        {
            await state.ChangeAsync([HostStateChange.OpenProject(probe)]);
            await state.ChangeAsync([HostStateChange.ForgetProject(probe)]);
        });
        var marker = Path.Combine(root, "marker");
        await state.ChangeAsync([HostStateChange.OpenProject(marker)]);
        await Seen(item => item is { Channel: LifecycleEvent.Projects, Kind: "opened" } && item.ProjectRoot == marker);
        int start;
        lock (events) start = events.Count;

        var worktrees = repo + "-worktrees";
        async Task<WorkspaceCatalog> List()
        {
            var catalog = await host.ListWorkspacesAsync(repo);
            Require(Describe((await state.GetStateAsync()).WorkspacesOf(repo), root) == Describe(catalog.Workspaces, root), "Host state must carry the listed registry.");
            log.Add(Describe(catalog.Workspaces, root) + " ; existing " + string.Join(",", catalog.Existing.Select(tree => Path.GetFileName(tree.Path) + ":" + tree.Branch)));
            return catalog;
        }

        // A project met for the first time adopts what Git already lists, by where it lives.
        Git(repo, "worktree", "add", Path.Combine(worktrees, "old"), "-b", "old");
        Git(repo, "worktree", "add", Path.Combine(root, "elsewhere"), "-b", "side");
        Git(repo, "worktree", "add", "--detach", Path.Combine(root, "loose"));
        var first = await List();
        Require(first.Workspaces.Count == 3 && first.Workspaces.Single(workspace => workspace.Branch == "old").Kind == WorkspaceKinds.Managed &&
            first.Workspaces.Single(workspace => workspace.Branch == "side").Kind == WorkspaceKinds.External &&
            first.Workspaces[0] is { Branch: "main", BaseBranch: "main", InitialTerminalPending: true } && first.Workspaces[0].Path == repo &&
            first.Workspaces.Skip(1).All(workspace => !workspace.InitialTerminalPending),
            "Listing ensures one pending Default workspace first and adopts existing worktrees as already complete: " + log[^1]);
        Require(first.Existing.Single() is { IsDetached: true } loose && loose.Path == Path.Combine(root, "loose"), "A detached worktree stays attachable-listed but unadopted.");
        var revision = (await state.GetStateAsync()).Revision;
        var again = await List();
        Require(again.Workspaces.Select(workspace => workspace.Id).SequenceEqual(first.Workspaces.Select(workspace => workspace.Id)) &&
            (await state.GetStateAsync()).Revision == revision, "Ids are stable and a drift-free list publishes nothing.");

        // Attaching records a worktree in place.
        var late = Path.Combine(root, "late");
        Git(repo, "worktree", "add", late, "-b", "late");
        Require((await List()).Existing.Any(tree => tree.Path == late && tree.Branch == "late"), "A worktree made after the first list waits to be attached.");
        var head = Git(late, "rev-parse", "HEAD");
        var attached = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Attach(repo, late)))!;
        Require(attached is { Kind: WorkspaceKinds.External, Branch: "late", BaseBranch: "main", InitialTerminalPending: true } && attached.Path == late,
            "Attaching yields a pending external workspace measured against the repository default.");
        Require((await host.ApplyWorkspaceActionAsync(WorkspaceAction.Attach(repo, late)))!.Id == attached.Id, "Attaching twice is idempotent.");
        Require(Git(late, "rev-parse", "HEAD") == head && Git(late, "status", "--porcelain").Length == 0 && !Directory.Exists(Path.Combine(late, ".sharprail")),
            "Attaching must not touch the checkout.");
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Attach(repo, Path.Combine(root, "loose"))), "A detached worktree was attached."));
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Attach(repo, root)), "A folder that is no worktree was attached."));
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Remove(attached.Id)), "An external workspace was removed from disk."));
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.ForceRemove(attached.Id)), "An external workspace was force removed."));
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Reclaim(attached.Id)), "An external workspace was reclaimed."));
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Forget(first.Workspaces[0].Id)), "The Default workspace was forgotten."));
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Reclaim(first.Workspaces[0].Id)), "The Default workspace was reclaimed."));
        log.Add(await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.ForceRemove(first.Workspaces[0].Id)), "The Default workspace was force removed."));
        await state.ChangeAsync([HostStateChange.Label(late, "Late work")]);
        Require((await host.ApplyWorkspaceActionAsync(WorkspaceAction.Forget(attached.Id)))!.Id == attached.Id && Directory.Exists(late) &&
            !(await state.GetStateAsync()).WorkspaceLabels.ContainsKey(late), "Forgetting drops the record and its label and keeps the worktree.");
        Require(await host.ApplyWorkspaceActionAsync(WorkspaceAction.Forget(attached.Id)) is null, "Forgetting an unknown workspace returns nothing.");
        Require((await List()).Existing.Any(tree => tree.Path == late), "A forgotten worktree can be attached again.");

        // A name is the label and derives a kebab branch that is unique against refs and directories.
        var named = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "  Fix  Login Flow! ")))!;
        Require(named is { Kind: WorkspaceKinds.Managed, Branch: "fix-login-flow", BaseBranch: "main", InitialTerminalPending: true } &&
            named.Path == Path.Combine(worktrees, "fix-login-flow") && (await state.GetStateAsync()).WorkspaceLabels[named.Path] == "Fix Login Flow!",
            "A named workspace keeps its name as the label and cuts a kebab branch.");
        Require(!Directory.Exists(Path.Combine(named.Path, ".sharprail")) && Git(named.Path, "status", "--porcelain").Length == 0,
            "A created workspace has no .sharprail directory, no inner .gitignore and no Git footprint.");
        var scratch = WorktreePaths.WorkspaceScratchDirectory(repo, named.Path, Path.Combine(root, "state-probe"));
        Require(scratch.StartsWith(Path.GetFullPath(Path.Combine(root, "state-probe")) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !scratch.StartsWith(named.Path, StringComparison.Ordinal) &&
            Path.GetFileName(scratch) == "fix-login-flow" && scratch.Contains(Path.DirectorySeparatorChar + "scratch" + Path.DirectorySeparatorChar),
            "A workspace's scratch directory resolves under the state directory, outside the project.");
        Git(repo, "branch", "fix-login-flow-2");
        Require((await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "Fix login flow")))!.Branch == "fix-login-flow-3",
            "A taken branch or directory name moves on to the next free suffix.");
        Require((await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo)))! is { Branch: "workspace-1" } auto &&
            !(await state.GetStateAsync()).WorkspaceLabels.ContainsKey(auto.Path), "An unnamed workspace takes the next workspace-N and no label.");

        // A remote base never becomes the upstream, and the tracking ref wins over a local branch of the same name.
        var origin = Path.Combine(root, "origin.git");
        Git(root, "clone", "--bare", repo, origin);
        Git(repo, "remote", "add", "origin", origin);
        Git(repo, "fetch", "origin");
        File.WriteAllText(Path.Combine(repo, "later.txt"), "later\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "later");
        Git(repo, "branch", "origin/main");
        var tracked = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "From remote", "origin/main")))!;
        Require(tracked.BaseBranch == "origin/main" && Git(tracked.Path, "rev-parse", "HEAD") == Git(repo, "rev-parse", "refs/remotes/origin/main") &&
            Git(tracked.Path, "rev-parse", "HEAD") != Git(repo, "rev-parse", "refs/heads/origin/main") &&
            Git(repo, "for-each-ref", "--format=%(upstream)", "refs/heads/from-remote").Length == 0,
            "A remote base is cut from its qualified tracking ref and leaves the branch without an upstream.");
        Git(repo, "update-ref", "refs/remotes/origin/kept", "refs/remotes/origin/main");
        Directory.Move(origin, origin + ".gone");
        Require((await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "Offline", "origin/kept")))!.BaseBranch == "origin/kept",
            "A failed fetch does not fail a create whose tracking ref is present.");
        var missing = await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "Missing", "origin/absent")), "A base that cannot be fetched created a workspace.");
        Require(missing.StartsWith("Could not fetch origin/absent: ", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(worktrees, "missing")),
            "A fetch that never lands the ref fails the create with Git's own message: " + missing);
        Directory.Move(origin + ".gone", origin);

        // The pending marker clears once and stays cleared.
        Require((await host.ApplyWorkspaceActionAsync(WorkspaceAction.ReserveTerminal(named.Id)))! is { InitialTerminalPending: false } &&
            (await state.GetStateAsync()).Workspaces.Single(workspace => workspace.Id == named.Id) is { InitialTerminalPending: false },
            "Reserving the first terminal clears the durable marker for every client.");

        // A checkout in the project folder reaches every client through the next Git read, not a reload.
        using (var watch = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            await using var stream = state.WatchAsync(watch.Token).GetAsyncEnumerator(watch.Token);
            await stream.MoveNextAsync();
            Git(repo, "switch", "-c", "moved");
            await host.OpenProjectAsync(repo);
            await host.GetGitAsync();
            Require(await stream.MoveNextAsync() && stream.Current.WorkspacesOf(repo).First() is { Kind: WorkspaceKinds.Default, Branch: "moved", BaseBranch: "origin/main" },
                "A terminal checkout in the Default workspace must publish its branch and default base.");
            watch.Cancel();
        }

        // Ordinary removal refuses dirty files; approved force removal keeps the branch and still refuses locks.
        File.WriteAllText(Path.Combine(named.Path, "dirty.txt"), "dirty\n");
        log.Add((await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.Remove(named.Id)), "A dirty worktree was removed.")).Split('\n')[0].Replace(root, "."));
        Require((await state.GetStateAsync()).Workspaces.Any(workspace => workspace.Id == named.Id) && Directory.Exists(named.Path), "A refused removal keeps the workspace.");
        Git(repo, "worktree", "lock", named.Path);
        await Refused(() => host.ApplyWorkspaceActionAsync(WorkspaceAction.ForceRemove(named.Id)), "A locked worktree was force removed.");
        Require(Directory.Exists(named.Path) && (await state.GetStateAsync()).Workspaces.Any(workspace => workspace.Id == named.Id) &&
            (await state.GetStateAsync()).WorkspaceLabels.ContainsKey(named.Path), "A failed force removal keeps the folder, registry and label.");
        Git(repo, "worktree", "unlock", named.Path);
        await host.ApplyWorkspaceActionAsync(WorkspaceAction.ForceRemove(named.Id));
        Require(!Directory.Exists(named.Path) && !(await state.GetStateAsync()).Workspaces.Any(workspace => workspace.Id == named.Id) &&
            !(await state.GetStateAsync()).WorkspaceLabels.ContainsKey(named.Path) && Git(repo, "branch", "--list", "fix-login-flow").Length > 0 &&
            !Git(repo, "worktree", "list").Contains("fix-login-flow\n", StringComparison.Ordinal), "Approved force removal removes a dirty worktree, its record and label, and keeps the branch.");
        await host.ApplyWorkspaceActionAsync(WorkspaceAction.Remove(tracked.Id));
        Require(!Directory.Exists(tracked.Path), "A clean managed worktree is removed.");
        var reclaimed = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(repo, "Reclaim fixture")))!;
        File.WriteAllText(Path.Combine(reclaimed.Path, "dirty.txt"), "dirty\n");
        await host.ApplyWorkspaceActionAsync(WorkspaceAction.Reclaim(reclaimed.Id));
        Require(!Directory.Exists(reclaimed.Path) && Git(repo, "branch", "--list", reclaimed.Branch).Length > 0,
            "The separate reclaim action retains its existing dirty-removal behavior.");

        // The existing Git actions keep the registry in step, and a worktree removed behind the host's back leaves it.
        var explicitPath = Path.Combine(root, "explicit");
        await host.ApplyGitActionAsync(new("create-worktree", explicitPath, "explicit-branch"));
        Require((await state.GetStateAsync()).Workspaces.Single(workspace => workspace.Path == explicitPath) is { Kind: WorkspaceKinds.Managed, Branch: "explicit-branch" },
            "A worktree created through the Git action is a managed workspace.");
        await host.ApplyGitActionAsync(new("remove-worktree", explicitPath));
        Require(!(await state.GetStateAsync()).Workspaces.Any(workspace => workspace.Path == explicitPath), "Removing through the Git action drops the record.");
        Git(repo, "worktree", "remove", Path.Combine(worktrees, "old"));
        var last = await List();
        Require(!last.Workspaces.Any(workspace => workspace.Branch == "old"), "A worktree Git no longer lists leaves the registry.");
        await state.ChangeAsync([HostStateChange.CloseProject(marker)]);
        await Seen(item => item is { Channel: LifecycleEvent.Projects, Kind: "closed" } && item.ProjectRoot == marker);
        lifetime.Cancel();
        await reader;
        List<LifecycleEvent> pushed;
        lock (events) pushed = events.Skip(start).ToList();
        Require(pushed.Where(item => item.Channel == LifecycleEvent.Workspaces).All(item => (item.Kind == "removed") == (item.Workspace is null) &&
                (item.Workspace is null || item.Workspace.Id == item.WorkspaceId && item.Workspace.ProjectRoot == item.ProjectRoot)) &&
            pushed.Count(item => item.Kind == "created") == pushed.Where(item => item.Kind == "created").Select(item => item.WorkspaceId).Distinct().Count(),
            "Created and updated events carry their record, removed ones only its id, and an id is created once.");
        Require(pushed.First(item => item.Kind == "created").Workspace is { Kind: WorkspaceKinds.Default } &&
            pushed.Any(item => item is { Kind: "updated", Workspace: { Kind: WorkspaceKinds.Default, Branch: "moved" } }) &&
            pushed.Any(item => item.Kind == "removed" && item.WorkspaceId == named.Id) && pushed[^1].Channel == LifecycleEvent.Projects,
            "The lifecycle channel must push the Default ensure, the folder-truth update, removals and project changes.");
        log.AddRange(pushed.Select(item => $"{item.Channel}.{item.Kind} {Path.GetRelativePath(root, item.Workspace?.Path ?? item.ProjectRoot)} {item.Workspace?.Kind} {item.Workspace?.Branch}"));
        log.Add(Describe((await state.GetStateAsync()).Workspaces, root));
        return log;
    }
}