using System.Diagnostics;
using System.Net;

using Grpc.Core;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Git host behaviour beyond status and diffs, through the embedded host and a real gRPC host with equal results.</summary>
internal static class GitHostChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<string> Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
        return (await output).Trim();
    }

    /// <summary>The failure text of a call that must be refused, the same whichever transport carried it.</summary>
    private static async Task<string> Refused(Func<Task> call, string what)
    {
        try { await call(); }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { return error.Status.Detail; }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException) { return error.Message; }
        throw new InvalidOperationException(what + " was accepted.");
    }

    /// <summary>An origin, and a clone of it on <c>feature</c> one commit ahead of <c>main</c>.</summary>
    private static async Task<string> Fixture(string parent)
    {
        var bare = Path.Combine(parent, "origin.git");
        var work = Path.Combine(parent, "work");
        Directory.CreateDirectory(parent);
        await Git(parent, "init", "-q", "--bare", "-b", "main", bare);
        await Git(parent, "init", "-q", "-b", "main", work);
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "one\ntwo\n");
        await Git(work, "add", ".");
        await Git(work, "commit", "-q", "-m", "Seed");
        await Git(work, "remote", "add", "origin", bare);
        await Git(work, "push", "-q", "-u", "origin", "main");
        await Git(work, "remote", "set-head", "origin", "main");
        await Git(work, "checkout", "-q", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(work, "b.txt"), "b\n");
        await Git(work, "add", ".");
        await Git(work, "commit", "-q", "-m", "Add b");
        return work;
    }

    public static async Task Run(string fixture)
    {
        CheckRefShapes();
        var directory = Path.Combine(fixture, "git-host-checks");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "gitconfig");
        await File.WriteAllTextAsync(config, "[user]\n\tname = T\n\temail = t@example.com\n[commit]\n\tgpgsign = false\n");
        var saved = new[] { "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            var localRoot = await Fixture(Path.Combine(directory, "local"));
            var remoteRoot = await Fixture(Path.Combine(directory, "remote"));
            var store = new HostStateStore(Path.Combine(directory, "local-state"));
            IProjectServices local = new LocalProjectAdapter(new ProjectServices(localRoot, store));
            await local.OpenProjectAsync(localRoot);
            var localLog = await Scenarios(local, new LocalStateAdapter(store), localRoot);
            Require(new HostStateStore(Path.Combine(directory, "local-state")).Current.WorkspaceBases.Count == 0, "A removed workspace's review target was persisted.");

            await using var server = RemoteServer.Create(remoteRoot, IPAddress.Loopback, 0, "git-host-test", Path.Combine(directory, "remote-state"));
            await server.StartAsync();
            try
            {
                var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var remote = new RemoteProjectAdapter(new Uri(address), "git-host-test");
                await remote.OpenProjectAsync(remoteRoot);
                using var remoteState = new RemoteStateAdapter(new Uri(address), "git-host-test");
                var remoteLog = await Scenarios(remote, remoteState, remoteRoot);
                Require(localLog.SequenceEqual(remoteLog),
                    "Local and remote Git results differ:\n" + string.Join('\n', localLog.Zip(remoteLog).Where(pair => pair.First != pair.Second).Select(pair => pair.First + "  <>  " + pair.Second)));
            }
            finally { await server.StopAsync(); }
        }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
        Console.WriteLine("PASS Git host checks: ref shapes at every door, creation base and re-pointed review target, pinned scope, badge totals, prefetch moves and nudges, locally and over gRPC");
    }

    private static void CheckRefShapes()
    {
        foreach (var good in new[] { "main", "origin/main", "feature/x.y", "HEAD", "v1.0", "a@b", "0123abcd", "refs/remotes/up/stream/main", "ümlaut" })
            Require(GitRefs.IsSafe(good), "A usable ref was refused: " + good);
        foreach (var bad in new[]
        {
            "", "-x", "--output=/tmp/x", "a..b", "main@{1}", "@{u}", "@", "a.", "a/", "/a", "a//b", ".a", "a/.b", "a.lock", "a.lock/b",
            "a b", "a\tb", "a\nb", "a\u007fb", "a~1", "a^", "a:b", "a?", "a*", "a[b", "a\\b"
        })
            Require(!GitRefs.IsSafe(bad), "An unusable ref passed: " + bad);
    }

    private static async Task<List<string>> Scenarios(IProjectServices host, IHostStateService state, string root)
    {
        var log = new List<string>();
        await RefDoors(host, root, log);
        await Pinned(host, root, log);
        await DiffBases(host, state, root, log);
        await Totals(host, state, root, log);
        await Prefetch(host, state, root, log);
        return log;
    }

    /// <summary>A workspace's badge totals cover the range its Changes panel opens on.</summary>
    private static async Task Totals(IProjectServices host, IHostStateService state, string root, List<string> log)
    {
        log.Add("no target " + await host.GetDiffStatsAsync(root));
        Require(log[^1] == "no target " + new DiffStats(0, 0), "A clean workspace without a target has no totals: " + log[^1]);
        await state.ChangeAsync([HostStateChange.DiffBase(root, "origin/main")]);
        log.Add("against target " + await host.GetDiffStatsAsync(root));
        Require(log[^1] == "against target " + new DiffStats(2, 0), "Totals must span the commits since the target's merge base: " + log[^1]);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "one\nthree\nfour\n");
        log.Add("with edits " + await host.GetDiffStatsAsync(root));
        Require(log[^1] == "with edits " + new DiffStats(3, 1), "Totals must include working edits: " + log[^1]);
        var snapshot = await host.GetGitAsync("origin/main");
        Require(snapshot.Changes.Sum(change => change.Added) == 3 && snapshot.Changes.Sum(change => change.Removed) == 1, "The badge and the Changes list must measure one range.");
        await Git(root, "checkout", "-q", "--", "a.txt");
        log.Add(await Refused(async () => await host.GetDiffStatsAsync(Path.GetTempPath()), "Totals of a folder outside the project"));
    }

    /// <summary>A fetch reports whether the tracking ref moved, and a move reaches the workspaces measured against it.</summary>
    private static async Task Prefetch(IProjectServices host, IHostStateService state, string root, List<string> log)
    {
        var pusher = Path.Combine(root, "..", "pusher");
        await Git(Path.Combine(root, ".."), "clone", "-q", Path.Combine(root, "..", "origin.git"), pusher);
        async Task Advance(string text)
        {
            await File.WriteAllTextAsync(Path.Combine(pusher, "upstream.txt"), text);
            await Git(pusher, "add", ".");
            await Git(pusher, "commit", "-q", "-m", text);
            await Git(pusher, "push", "-q", "origin", "main");
        }
        var common = Path.Combine(root, ".git");
        var moves = new List<string>();
        void Moved(string directory, string reference) { lock (moves) if (directory == common) moves.Add(reference); }
        ProjectServices.BaseMoved += Moved;
        try
        {
            await Advance("first");
            using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var changes = host.WatchFilesAsync(watch.Token).GetAsyncEnumerator(watch.Token);
            Require(await changes.MoveNextAsync(), "The watcher did not start.");
            var before = await Git(root, "rev-parse", "origin/main");
            await host.ListBranchesAsync(true);
            Require(await Git(root, "rev-parse", "origin/main") != before, "The background prefetch did not fetch the default base.");
            lock (moves) log.Add("moved " + string.Join(",", moves));
            Require(log[^1] == "moved origin/main", "A fetch that advanced the tracking ref must report the move: " + log[^1]);
            // This workspace's review target is origin/main, so the move must reach its watcher.
            Require(await changes.MoveNextAsync(), "A moved review target did not nudge the workspace measured against it.");

            await host.ListBranchesAsync(true);
            lock (moves) Require(moves.Count == 1, "A fetch that changed nothing must not report a move.");
            Require(!await ProjectServices.FetchRemoteAsync(root, "main", CancellationToken.None), "A local branch has nothing to fetch.");
            await Advance("second");
            Require(await ProjectServices.FetchRemoteAsync(root, "origin/main", CancellationToken.None), "A second advance must be reported as a move.");
            Require(!await ProjectServices.FetchRemoteAsync(root, "origin/main", CancellationToken.None), "An unchanged ref must not be reported as moved.");
            log.Add(await Refused(async () => await ProjectServices.FetchRemoteAsync(root, "origin/main..x", CancellationToken.None), "A range as a fetched ref"));
        }
        finally { ProjectServices.BaseMoved -= Moved; }
        await state.ChangeAsync([HostStateChange.DiffBase(root, "")]);
    }

    private static string Names(GitSnapshot snapshot) => string.Join(",", snapshot.Changes.Select(change => $"{change.Path}+{change.Added}-{change.Removed}").Order(StringComparer.Ordinal));

    /// <summary>A pinned scope measures one immutable commit against the working tree, wherever the branch goes.</summary>
    private static async Task Pinned(IProjectServices host, string root, List<string> log)
    {
        var seed = await Git(root, "rev-parse", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "one\ntwo\nthree\n");
        await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "n\n");
        var pinned = await host.GetGitAsync(seed, scope: "pinned");
        log.Add("pinned " + Names(pinned));
        Require(Names(pinned) == "a.txt+1-0,b.txt+1-0,new.txt+1-0", "A pinned scope spans commits, working edits and untracked files: " + Names(pinned));
        Require(Names(await host.GetGitAsync(seed[..8], scope: "pinned")) == Names(pinned), "An abbreviated pin must resolve to the same range.");
        Require(pinned.Commits.Count == 0, "A pinned snapshot carries a commit id, not a target to list commits against.");

        var sides = await host.GetDiffSidesAsync("a.txt", "pinned", seed);
        Require(sides.Original == "one\ntwo\n" && sides.Modified == "one\ntwo\nthree\n" && sides.OriginalCommit == seed, "Pinned diff sides must read the pin and the working tree.");
        var diff = await host.GetDiffAsync("a.txt", "pinned", seed);
        Require(diff.Contains("+three", StringComparison.Ordinal), "A pinned diff must show the working edit.");
        Require((await host.GetDiffAsync("new.txt", "pinned", seed)).Contains("+n", StringComparison.Ordinal), "An untracked file is an addition under a pin.");
        var added = await host.GetDiffSidesAsync("b.txt", "pinned", seed);
        Require(added.Original.Length == 0 && added.Modified == "b\n", "A file committed after the pin is absent on its original side.");

        // Committing moves HEAD and the branch; the pin does not move with them.
        await Git(root, "add", "a.txt");
        await Git(root, "commit", "-q", "-m", "Third line");
        Require(Names(await host.GetGitAsync(seed, scope: "pinned")) == Names(pinned), "A pinned range moved with the branch.");
        Require(Names(await host.GetGitAsync("", scope: "uncommitted")) == "new.txt+1-0", "The uncommitted scope should now hold the untracked file alone.");

        log.Add(await Refused(async () => await host.GetGitAsync("--output=leak", scope: "pinned"), "An option-shaped pin"));
        log.Add(await Refused(async () => await host.GetGitAsync("main", scope: "pinned"), "A ref name as a pin"));
        log.Add(await Refused(async () => await host.GetGitAsync("deadbeefcafe", scope: "pinned"), "An unknown pin"));
        log.Add(await Refused(async () => await host.GetDiffSidesAsync("a.txt", "pinned", "deadbeefcafe"), "Diff sides at an unknown pin"));
        Require(log[^2] == "Unknown commit: deadbeefcafe" && log[^1] == log[^2], "An unknown pin must be named as such: " + log[^2]);

        var receipt = await host.RevertChangeAsync("a.txt", "pinned", seed, new(),
            new(sides.OriginalHash, sides.ModifiedHash));
        Require(await File.ReadAllTextAsync(Path.Combine(root, "a.txt")) == "one\ntwo\n", "Reverting under a pin restores the pinned content.");
        await host.UndoChangeAsync(receipt.Id, receipt.After.Hash);
        File.Delete(Path.Combine(root, "new.txt"));
    }

    /// <summary>The host records what a workspace was created from and, apart from it, where its review now points.</summary>
    private static async Task DiffBases(IProjectServices host, IHostStateService state, string root, List<string> log)
    {
        var path = Path.GetFullPath(Path.Combine(root, "..", "review"));
        async Task<string> Record()
        {
            var current = await state.GetStateAsync();
            return $"base={current.WorkspaceBases.GetValueOrDefault(path)} target={current.WorkspaceDiffBases.GetValueOrDefault(path)} effective={current.DiffBase(path)}";
        }
        await host.ApplyGitActionAsync(new("create-worktree", path, "review", "origin/main"));
        log.Add(await Record());
        Require(log[^1] == "base=origin/main target= effective=origin/main", "Creation must record the base as the review target: " + log[^1]);

        await state.ChangeAsync([HostStateChange.DiffBase(path, "feature")]);
        log.Add(await Record());
        Require(log[^1] == "base=origin/main target=feature effective=feature", "Re-pointing must leave the creation base alone: " + log[^1]);

        // A target that does not resolve is still the user's choice; reading against it reports the failure.
        await state.ChangeAsync([HostStateChange.DiffBase(path, "gone-branch")]);
        log.Add(await Record());
        Require(log[^1].EndsWith("effective=gone-branch", StringComparison.Ordinal), "A well-formed target must be stored even when it does not resolve.");

        await state.ChangeAsync([HostStateChange.DiffBase(path, "origin/main")]);
        log.Add(await Record());
        Require(log[^1] == "base=origin/main target= effective=origin/main", "Pointing back at the creation base must drop the override: " + log[^1]);
        await state.ChangeAsync([HostStateChange.DiffBase(path, "feature")]);
        await state.ChangeAsync([HostStateChange.DiffBase(path, "")]);
        log.Add(await Record());
        Require(log[^1] == "base=origin/main target= effective=origin/main", "An empty target must restore the creation base: " + log[^1]);

        try { await state.ChangeAsync([HostStateChange.DiffBase(path, "main..feature")]); throw new InvalidOperationException("A range was stored as a review target."); }
        catch (Exception error) when (error is ArgumentException or RpcException) { }

        await state.ChangeAsync([HostStateChange.DiffBase(path, "feature")]);
        await host.ApplyGitActionAsync(new("remove-worktree", path));
        log.Add(await Record());
        Require(log[^1] == "base= target= effective=", "Removing a workspace must drop its base and target: " + log[^1]);
        await Git(root, "branch", "-D", "review");
    }

    private static async Task RefDoors(IProjectServices host, string root, List<string> log)
    {
        var before = await Git(root, "for-each-ref");
        foreach (var bad in new[] { "--output=leak", "main..feature", "main@{1}", "main~1", "main^{tree}" })
        {
            log.Add(await Refused(async () => await host.GetGitAsync(bad), "A snapshot against " + bad));
            log.Add(await Refused(async () => await host.ListCommitsAsync(bad), "A commit list against " + bad));
            log.Add(await Refused(async () => await host.GetDiffAsync("a.txt", "branch", bad), "A diff against " + bad));
            log.Add(await Refused(async () => await host.GetDiffSidesAsync("a.txt", "branch", bad), "Diff sides against " + bad));
            log.Add(await Refused(async () => await host.ApplyGitActionAsync(new("create-worktree", Path.Combine(root, "..", "wt-base"), "ok-branch", bad)), "A workspace based on " + bad));
            log.Add(await Refused(async () => await host.ApplyGitActionAsync(new("create-worktree", Path.Combine(root, "..", "wt-branch"), bad, "main")), "A workspace branch named " + bad));
        }
        Require(log.All(line => line.StartsWith("Not a usable git ref: ", StringComparison.Ordinal)), "A ref was refused by Git rather than at the door:\n" + string.Join('\n', log));
        Require(await Git(root, "for-each-ref") == before && !File.Exists(Path.Combine(root, "leak")), "A refused ref still reached Git.");
        Require((await host.GetGitAsync("origin/main")).Commits.Count == 1, "A well-formed comparison target must still resolve.");

        // The repository names its own default base; a crafted origin/HEAD is a ref like any other.
        var head = Path.Combine(root, ".git", "refs", "remotes", "origin", "HEAD");
        var original = await File.ReadAllTextAsync(head);
        Require((await host.ListBranchesAsync(false)).DefaultBase == "origin/main", "The default base should be origin's HEAD.");
        await File.WriteAllTextAsync(head, "ref: refs/remotes/origin/main..feature\n");
        log.Add("crafted default base -> " + (await host.ListBranchesAsync(false)).DefaultBase);
        await File.WriteAllTextAsync(head, original);
        Require(log[^1] == "crafted default base -> feature", "A crafted origin/HEAD became the default base: " + log[^1]);
    }
}