using System.Net;
using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// The host's terminal catalog: which tabs exist is shared, persisted and broadcast, separately from shells.
internal static partial class TerminalHostChecks
{
    private static async Task Catalog(string root, string workspace)
    {
        await using (var local = new PtyTerminalService())
        {
            await Membership(new LocalTerminalCatalogAdapter(local), new LocalTerminalAdapter(local), workspace, "local");
            await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "catalog-token", terminals: local);
            await app.StartAsync();
            try
            {
                using var catalog = new RemoteTerminalCatalogAdapter(Address(app), "catalog-token");
                using var terminals = new RemoteTerminalAdapter(Address(app), "catalog-token");
                await Membership(catalog, terminals, workspace, "remote");
                using var rejected = new RemoteTerminalCatalogAdapter(Address(app), "wrong-token");
                try { await rejected.ReserveAsync(workspace, new("denied", "Denied")); throw new InvalidOperationException("The remote catalog accepted a bad token."); }
                catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.Unauthenticated) { }
            }
            finally { await app.StopAsync(); }
        }
        CatalogStorage(root);
        await CatalogRevival(root, workspace);
        await RemovedWorkspace(root);
    }

    private static async Task Membership(ITerminalCatalogService catalog, ITerminalService terminals, string workspace, string mode)
    {
        var area = Path.Combine(workspace, mode + "-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        using var watching = new CancellationTokenSource();
        var seen = new[] { TerminalCatalog.Empty, TerminalCatalog.Empty };
        var watchers = seen.Select((_, index) => Task.Run(async () =>
        {
            try { await foreach (var snapshot in catalog.WatchAsync(watching.Token)) Volatile.Write(ref seen[index], snapshot); }
            catch (Exception) when (watching.IsCancellationRequested) { }
        })).ToArray();
        IReadOnlyList<TerminalTab> Tabs(TerminalCatalog snapshot) => snapshot.Workspaces.GetValueOrDefault(area) ?? [];
        Task Everyone(Func<IReadOnlyList<TerminalTab>, bool> expected, string what) =>
            Until(() => Task.FromResult(seen.All(snapshot => expected(Tabs(Volatile.Read(ref snapshot))))), $"{mode} catalog broadcast: {what}");

        // A workspace the host has not heard of gets its one initial terminal, and no shell.
        var opened = await catalog.OpenWorkspaceAsync(area, []);
        Require(Tabs(opened) is [{ Key: TerminalTab.InitialKey, Title: TerminalTab.InitialTitle }], $"{mode}: a new workspace must be given the initial terminal.");
        await Everyone(tabs => tabs.Count == 1, "initial terminal");
        var initial = TerminalTab.SessionFor(area, TerminalTab.InitialKey);
        try { await using var resumed = await terminals.AttachAsync(new(initial, area, "client", 80, 24, 1)); throw new InvalidOperationException($"{mode}: reserving a terminal started its shell."); }
        catch (IOException) { }

        var reserved = await catalog.ReserveAsync(area, new("tab-a", "Reserved"));
        Require(Tabs(reserved).Select(tab => tab.Key).SequenceEqual([TerminalTab.InitialKey, "tab-a"]), $"{mode}: a reservation must append to the catalog.");
        var again = await catalog.ReserveAsync(area, new("tab-a", "Ignored rename"));
        Require(again.Revision == reserved.Revision && Tabs(again)[1].Title == "Reserved", $"{mode}: reserving an existing tab must change nothing.");
        await Everyone(tabs => tabs.Count == 2, "reservation");

        foreach (var invalid in new TerminalTab[] { new("", "Terminal"), new(new string('x', TerminalTab.MaxKeyLength + 1), "Terminal"), new("tab-b", ""), new("tab-b", new string('x', TerminalTab.MaxTitleLength + 1)) })
            try { await catalog.ReserveAsync(area, invalid); throw new InvalidOperationException($"{mode}: an invalid terminal tab was reserved."); }
            catch (ArgumentException) { }
        Require(Tabs(await catalog.ReserveAsync(area, new(new string('k', TerminalTab.MaxKeyLength), new string('t', TerminalTab.MaxTitleLength)))).Count == 3, $"{mode}: the longest allowed key and title must be accepted.");

        // The shell is born by attaching, never by the catalog, and a tab's session is the same for every client.
        var session = await terminals.AttachAsync(new(TerminalTab.SessionFor(area, "tab-a"), area, "client", 100, 30));
        var screen = new Screen(session);
        Require(session.Created, $"{mode}: the first attach of a reserved tab must start its shell.");
        await screen.Run("printf 'PID_%s_\\n' \"$$\"");
        var shell = int.Parse(await screen.WaitForMatch(@"PID_(\d+)_", mode));
        Require(Tabs(await catalog.CloseTabAsync(area, "tab-a")).All(tab => tab.Key != "tab-a"), $"{mode}: closing a tab must remove it from the catalog.");
        await Until(() => Task.FromResult(!Running(shell)), mode + " closing a catalogued tab ends its shell");
        await Everyone(tabs => tabs.Count == 2, "close");
        await session.DisposeAsync();

        // Ending a session by its id, as a tab body does, removes its tab too; the initial terminal never returns.
        await terminals.CloseAsync(initial);
        await Everyone(tabs => tabs.All(tab => tab.Key != TerminalTab.InitialKey), "initial terminal closed by session");
        var revisited = await catalog.OpenWorkspaceAsync(area, [new(TerminalTab.InitialKey, TerminalTab.InitialTitle), new("tab-adopted", "Adopted")]);
        Require(Tabs(revisited).All(tab => tab.Key != TerminalTab.InitialKey) && Tabs(revisited).Any(tab => tab.Key == "tab-adopted"),
            $"{mode}: a known workspace must gain a client's own tabs but never its closed initial terminal.");

        // A client that brings its tabs to an unknown workspace keeps exactly those.
        var legacy = area + "-legacy";
        Directory.CreateDirectory(legacy);
        Require((await catalog.OpenWorkspaceAsync(legacy, [new("old-1", "Terminal 1"), new("old-2", "Terminal 2")])).Workspaces[legacy].Select(tab => tab.Key).SequenceEqual(["old-1", "old-2"]),
            $"{mode}: a client's existing tabs must become the unknown workspace's catalog.");

        var full = area + "-full";
        Directory.CreateDirectory(full);
        await catalog.OpenWorkspaceAsync(full, Enumerable.Range(0, TerminalTab.MaxPerWorkspace).Select(index => new TerminalTab("tab-" + index, "Terminal " + index)).ToArray());
        try { await catalog.ReserveAsync(full, new("tab-over-limit", "Terminal")); throw new InvalidOperationException($"{mode}: the catalog exceeded its limit."); }
        catch (InvalidOperationException error) when (error.Message.Contains("limited to 256", StringComparison.Ordinal)) { }

        // A removed workspace takes every shell rooted in it, catalogued or not, and is forgotten.
        var kept = await terminals.AttachAsync(new(TerminalTab.SessionFor(area, "tab-adopted"), area, "client", 100, 30));
        var loose = await terminals.AttachAsync(new(mode + "-loose-" + Guid.NewGuid().ToString("N"), area, "client", 100, 30));
        var pids = new List<int>();
        foreach (var attached in new[] { kept, loose })
        {
            var output = new Screen(attached);
            await output.Run("printf 'PID_%s_\\n' \"$$\"");
            pids.Add(int.Parse(await output.WaitForMatch(@"PID_(\d+)_", mode)));
        }
        Require(!(await catalog.CloseWorkspaceAsync(area)).Workspaces.ContainsKey(area), $"{mode}: a removed workspace must leave the catalog.");
        await Until(() => Task.FromResult(pids.All(pid => !Running(pid))), mode + " removing a workspace ends its shells");
        await Until(() => Task.FromResult(seen.All(snapshot => !Volatile.Read(ref snapshot).Workspaces.ContainsKey(area))), mode + " removed workspace broadcast");
        await kept.DisposeAsync(); await loose.DisposeAsync();
        await catalog.CloseWorkspaceAsync(legacy); await catalog.CloseWorkspaceAsync(full);
        await watching.CancelAsync();
        await Task.WhenAll(watchers);
        Console.WriteLine($"PASS host terminal catalog ({mode}): reservation without a shell, idempotent and bounded, broadcast to every watcher, close by tab and by session, a final initial terminal, adoption, and a removed workspace ending its shells");
    }

    private static void CatalogStorage(string root)
    {
        var directory = Path.Combine(root, "catalog-store");
        var first = new TerminalCatalogStore(directory);
        Require(!first.Loaded, "A directory without a catalog must not read as loaded.");
        first.OpenWorkspace("/work/a", []);
        first.Reserve("/work/a", new("tab-a", "Kept"));
        // Membership is on disk after every change, not only when the host stops.
        var second = new TerminalCatalogStore(directory);
        Require(second.Loaded && second.Current.Workspaces["/work/a"].Select(tab => tab.Key).SequenceEqual([TerminalTab.InitialKey, "tab-a"]), "The catalog must persist on every change.");
        first.Remove("/work/a", "tab-a"); first.Remove("/work/a", TerminalTab.InitialKey);
        Require(new TerminalCatalogStore(directory).Current.Workspaces["/work/a"].Count == 0, "A workspace whose terminals were all closed must stay known and empty.");

        var repaired = Path.Combine(root, "catalog-repair");
        Directory.CreateDirectory(repaired);
        var tabs = Enumerable.Range(0, 300).Select(index => new { Key = "tab-" + index, Title = "Terminal" }).ToList();
        tabs[1] = new { Key = "", Title = "Dropped" };
        tabs[2] = new { Key = "tab-2", Title = "" };
        File.WriteAllText(Path.Combine(repaired, TerminalCatalogStore.FileName), JsonSerializer.Serialize(new { Workspaces = new Dictionary<string, object> { ["/work/b"] = tabs } }));
        var loaded = new TerminalCatalogStore(repaired).Current.Workspaces["/work/b"];
        Require(loaded.Count == TerminalTab.MaxPerWorkspace - 1 && loaded[^1].Key == "tab-255" && loaded.All(tab => tab.Key.Length > 0) && loaded[1] == new TerminalTab("tab-2", "Terminal"),
            "A loaded catalog must be truncated, lose invalid keys and have invalid titles repaired.");
        File.WriteAllText(Path.Combine(repaired, TerminalCatalogStore.FileName), "{ not json");
        var corrupt = new TerminalCatalogStore(repaired);
        Require(corrupt.Current.Workspaces.Count == 0 && corrupt.LastError is not null, "A corrupt catalog must start empty and report why.");

        // A reservation that cannot be saved is undone and reaches nobody.
        var blocked = Path.Combine(root, "catalog-blocked");
        File.WriteAllText(blocked, "a file where the directory should be");
        var failing = new TerminalCatalogStore(blocked);
        using var watching = new CancellationTokenSource();
        var published = 0;
        var watcher = Task.Run(async () =>
        {
            try { await foreach (var snapshot in failing.WatchAsync(watching.Token)) Interlocked.Increment(ref published); }
            catch (OperationCanceledException) { }
        });
        try { failing.Reserve("/work/c", new("tab-a", "Reserved")); throw new InvalidOperationException("An unsaved reservation was accepted."); }
        catch (IOException) { }
        Thread.Sleep(200);
        Require(failing.Current.Workspaces.Count == 0 && failing.Current.Revision == 0 && Volatile.Read(ref published) <= 1, "A failed reservation must leave the catalog unchanged and unpublished.");
        watching.Cancel(); watcher.GetAwaiter().GetResult();
        Console.WriteLine("PASS terminal catalog storage: saved on every change, empty workspaces stay known, oversized/invalid/corrupt files repaired, and an unsaved reservation is undone without a broadcast");
    }

    private static async Task CatalogRevival(string root, string workspace)
    {
        var directory = Path.Combine(root, "catalog-revival");
        var loose = "loose-" + Guid.NewGuid().ToString("N");
        string Recording(string session) => Path.Combine(directory, Convert.ToHexString(Encoding.UTF8.GetBytes(session)) + ".rec");
        async Task Mark(ITerminalService terminals, string session, string marker)
        {
            var (attached, screen) = await Open(terminals, session, workspace);
            await screen.Run($"printf '{marker}_%s\\n' SEEN");
            await screen.WaitFor(marker + "_SEEN", "catalog revival");
            await attached.DisposeAsync();
        }
        await using (var first = new PtyTerminalService(recordingsDirectory: directory))
        {
            await first.OpenWorkspaceAsync(workspace, [new("kept", "Kept"), new("closed", "Closed")]);
            await Mark(first, TerminalTab.SessionFor(workspace, "kept"), "KEPT");
            await Mark(first, TerminalTab.SessionFor(workspace, "closed"), "CLOSED");
            await Mark(first, loose, "LOOSE");
        }
        Require(File.Exists(Recording(loose)) && File.Exists(Recording(TerminalTab.SessionFor(workspace, "closed"))), "A graceful stop must record every session.");
        // The tab is closed while the host is down: its catalog entry goes, its recording stays behind.
        new TerminalCatalogStore(directory).Remove(workspace, "closed");
        await using (var second = new PtyTerminalService(recordingsDirectory: directory))
        {
            Require(!File.Exists(Recording(loose)) && !File.Exists(Recording(TerminalTab.SessionFor(workspace, "closed"))), "Recordings no catalogued tab names must be discarded on start.");
            Require(second.Catalog.Workspaces[workspace] is [{ Key: "kept", Title: "Kept" }], "The revived catalog must hold exactly the surviving tabs.");
            var (session, screen) = await Open(second, TerminalTab.SessionFor(workspace, "kept"), workspace);
            Require(session.Created && screen.Text.Contains("KEPT_SEEN", StringComparison.Ordinal), "A catalogued tab must revive its recorded screen in a new shell.");
            var (gone, blank) = await Open(second, TerminalTab.SessionFor(workspace, "closed"), workspace);
            Require(!blank.Text.Contains("CLOSED_SEEN", StringComparison.Ordinal), "A closed tab's screen must not come back.");
            await session.DisposeAsync(); await gone.DisposeAsync();
            await second.CloseWorkspaceAsync(workspace);
        }
        Require(!Directory.EnumerateFiles(directory, "*.rec").Any(), "A removed workspace must leave no recordings behind.");
        Console.WriteLine("PASS host terminal revival by catalog: surviving tabs revive their screens, recordings without a catalogued tab are discarded, and a removed workspace leaves none");
    }

    // A worktree removed through the project host ends its terminals on a real remote host.
    private static async Task RemovedWorkspace(string root)
    {
        using var git = new E2E.IsolatedGit(Path.Combine(root, "catalog-git"));
        var repository = E2E.IsolatedGit.Repository(Path.Combine(root, "catalog-repository"));
        await using var app = RemoteServer.Create(repository, IPAddress.Loopback, 0, "removal-token");
        await app.StartAsync();
        try
        {
            using var projects = new RemoteProjectAdapter(Address(app), "removal-token");
            using var catalog = new RemoteTerminalCatalogAdapter(Address(app), "removal-token");
            using var terminals = new RemoteTerminalAdapter(Address(app), "removal-token");
            var project = await projects.OpenProjectAsync(repository);
            var worktree = Path.Combine(Path.GetDirectoryName(project.RootPath)!, "catalog-worktree-" + Guid.NewGuid().ToString("N"));
            var snapshot = await projects.ApplyGitActionAsync(new("create-worktree", worktree, "catalog-" + Guid.NewGuid().ToString("N")));
            var path = snapshot.Worktrees.Single(tree => !tree.IsMain).Path;
            await catalog.OpenWorkspaceAsync(path, []);
            var session = await terminals.AttachAsync(new(TerminalTab.SessionFor(path, TerminalTab.InitialKey), path, "client", 100, 30));
            var screen = new Screen(session);
            await screen.Run("printf 'PID_%s_\\n' \"$$\"");
            var shell = int.Parse(await screen.WaitForMatch(@"PID_(\d+)_", "removed workspace"));
            await screen.Run("cd /");
            await projects.ApplyGitActionAsync(new("remove-worktree", path));
            await Until(() => Task.FromResult(!Running(shell)), "removing a worktree ends its shells");
            using var once = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await foreach (var current in catalog.WatchAsync(once.Token))
                if (!current.Workspaces.ContainsKey(path)) break;
            await session.DisposeAsync();
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS host terminals of a removed workspace: removing a worktree through a remote host ends its shells and drops it from the catalog");
    }
}