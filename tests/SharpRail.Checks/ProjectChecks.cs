using System.Diagnostics;
using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class ProjectChecks
{
    private static bool GitDetail(string message, string folder) =>
        message.StartsWith("fatal:", StringComparison.Ordinal) &&
        (message.Contains(folder, StringComparison.Ordinal) || message.Contains("not a git repository", StringComparison.Ordinal));

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static async Task Run(string fixture)
    {
        var core = new ProjectServices(fixture);
        IProjectServices local = new LocalProjectAdapter(core);
        var info = await local.OpenProjectAsync(fixture);
        var files = await local.ListFilesAsync("");
        var markdown = await local.ReadFileAsync("README.md");
        Require(markdown.Text.Contains("# Preview", StringComparison.Ordinal), "Document read failed.");
        Require((await local.ListSpecsAsync()).Count >= 2, "Spec catalog failed.");
        try
        {
            await local.ReadFileAsync("../outside.txt");
            throw new InvalidOperationException("Workspace path traversal accepted.");
        }
        catch (UnauthorizedAccessException) { }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await local.ListFilesAsync("", cancellation.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }

        await using var server = RemoteServer.Create(fixture, IPAddress.Loopback, 0, "project-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "project-test");
            Require(await remote.OpenProjectAsync(fixture) == info, "Remote project differs.");
            Require((await remote.ListFilesAsync("")).SequenceEqual(files), "Remote files differ.");
            await CheckWatching(local, remote, fixture);
            Require(await remote.ReadFileAsync("README.md") == markdown, "Remote text differs.");
            // Like the reference, valid UTF-8 opens as text even with NUL control characters; only invalid UTF-8 is binary.
            await File.WriteAllTextAsync(Path.Combine(fixture, "controls.txt"), "before\0after\n");
            Require((await local.ReadFileAsync("controls.txt")).Text == "before\0after\n" && (await remote.ReadFileAsync("controls.txt")).Text == "before\0after\n",
                "A UTF-8 file with NUL characters must open as text.");
            await File.WriteAllBytesAsync(Path.Combine(fixture, "blob.bin"), [0xFF, 0xFE, 0x00, 0x01]);
            try { await local.ReadFileAsync("blob.bin"); throw new InvalidOperationException("Invalid UTF-8 opened as text."); }
            catch (IOException error) { Require(error.Message == "Binary files cannot be previewed.", "Invalid UTF-8 must be reported as binary."); }
            Require((await remote.ListSpecsAsync()).SequenceEqual(await local.ListSpecsAsync()), "Remote specs differ.");
            var snapshot = await remote.GetGitAsync();
            Require(!snapshot.IsRepository, "Non-git directory detected as repository.");
            var broken = Path.Combine(fixture, "broken-git");
            Directory.CreateDirectory(broken);
            await File.WriteAllTextAsync(Path.Combine(broken, ".git"), "gitdir: " + Path.Combine(broken, "missing-admin"));
            await local.OpenProjectAsync(broken);
            try { await local.GetGitAsync(); throw new InvalidOperationException("Broken Git metadata was hidden as a non-repository."); }
            // Git's wording differs by version; some builds print "(null)" instead of the broken folder.
            catch (IOException error) { Require(GitDetail(error.Message, broken), $"Git probe failure detail was lost: {error.Message}"); }
            await remote.OpenProjectAsync(broken);
            try { await remote.GetGitAsync(); throw new InvalidOperationException("Remote Git probe failure was hidden."); }
            catch (Grpc.Core.RpcException error)
            {
                Require(error.StatusCode == Grpc.Core.StatusCode.FailedPrecondition && GitDetail(error.Status.Detail, broken),
                    "Remote Git probe failure detail differs.");
            }
            await remote.OpenProjectAsync(fixture);
            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var watching = remote.WatchFilesAsync(shutdownTimeout.Token).GetAsyncEnumerator();
            Require(await watching.MoveNextAsync(), "The shutdown probe must have an active file subscription.");
            var pendingChange = watching.MoveNextAsync().AsTask();
            await server.StopAsync(shutdownTimeout.Token);
            try { Require(!await pendingChange, "A file stream must finish when the host stops."); }
            catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.Cancelled) { }
            Console.WriteLine("PASS remote shutdown releases an active filesystem subscription");
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS project/files/specs local and gRPC parity, traversal and cancellation");

        var source = Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE");
        if (string.IsNullOrEmpty(source))
        {
            Console.WriteLine("SKIP git/worktree integration: set SHARPRAIL_TEST_GIT_SOURCE to an existing repository with a commit.");
            return;
        }
        await CheckGit(source, fixture + "-git");
    }

    private static async Task CheckWatching(IProjectServices local, IProjectServices remote, string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var first = local.WatchFilesAsync(timeout.Token).GetAsyncEnumerator();
        await using var second = remote.WatchFilesAsync(timeout.Token).GetAsyncEnumerator();
        Require(await first.MoveNextAsync() && first.Current.Rescan && await second.MoveNextAsync() && second.Current.Rescan,
            "Both file subscriptions must start with a rescan after registration.");
        async Task Observe(IAsyncEnumerator<FileChange> stream, string path)
        {
            while (await stream.MoveNextAsync())
                if (stream.Current.Rescan || stream.Current.Paths.Contains(path)) return;
            throw new InvalidOperationException("File subscription ended before " + path);
        }
        async Task Changed(string path, Action mutation, bool exists)
        {
            var observations = Task.WhenAll(Observe(first, path), Observe(second, path));
            mutation();
            await observations;
            foreach (var service in new[] { local, remote })
                Require((await service.ListFilesAsync("", timeout.Token)).Any(file => file.Path == path) == exists,
                    "Watcher invalidations must expose the actual filesystem through both transports.");
        }
        var original = Path.Combine(root, "watch-created.txt");
        var renamed = Path.Combine(root, "watch-renamed.txt");
        await Changed("watch-created.txt", () => File.WriteAllText(original, "created"), true);
        await Changed("watch-created.txt", () => File.AppendAllText(original, " changed"), true);
        Require((await remote.ReadFileAsync("watch-created.txt")).Text == "created changed", "External edits must be readable after notification.");
        await Changed("watch-renamed.txt", () => File.Move(original, renamed), true);
        Require(!(await remote.ListFilesAsync("")).Any(file => file.Path == "watch-created.txt"), "Rename must remove the old path.");
        await Changed("watch-renamed.txt", () => File.Delete(renamed), false);
        timeout.Cancel();
        try { await first.MoveNextAsync(); throw new InvalidOperationException("File watcher ignored cancellation."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS filesystem create/edit/rename/delete notifications locally and over gRPC, with cancellation");
    }

    private static async Task<string> Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output; var error = await errors;
        if (process.ExitCode != 0) throw new IOException(error);
        return text;
    }

    private static async Task CheckGit(string source, string root)
    {
        var parent = Path.GetDirectoryName(root)!;
        // Reuse existing commits in a disposable clone. This harness never makes or signs commits.
        await Git(parent, "clone", "--no-local", "--depth=2", "--no-checkout", new Uri(Path.GetFullPath(source)).AbsoluteUri, root);
        await Git(root, "sparse-checkout", "init", "--cone");
        await Git(root, "sparse-checkout", "set", ".sharprail-test-only");
        await Git(root, "checkout", "HEAD");
        var host = new ProjectServices(root);
        await host.OpenProjectAsync(root);
        var unusual = "space ü\tfile.txt";
        await File.WriteAllTextAsync(Path.Combine(root, unusual), "Untracked content\n");
        var snapshot = await host.GetGitAsync();
        Require(snapshot.IsRepository && snapshot.Changes.Any(change => change.Path == unusual && change.IndexStatus == "?"), "Untracked filename parsing failed.");
        Require(snapshot.Changes.Single(change => change.Path == unusual).Added == 1, "Untracked line count was missing.");
        snapshot = await host.ApplyGitActionAsync(new("stage", unusual));
        Require(snapshot.Changes.Any(change => change.Path == unusual && change.IndexStatus == "A"), "Staging failed.");
        Require((await host.GetDiffAsync(unusual, "staged")).Contains("Untracked content", StringComparison.Ordinal), "Staged diff failed.");
        Require(await host.GetDiffSidesAsync(unusual, "staged") == new DiffSides("", "Untracked content\n"), "Staged diff sides must compare HEAD with the index.");
        await File.WriteAllTextAsync(Path.Combine(root, unusual), "Current unstaged content\nSecond line\n");
        var pendingDiff = await host.GetDiffAsync(unusual, "uncommitted");
        Require(pendingDiff.Contains("+Current unstaged content", StringComparison.Ordinal) &&
            !pendingDiff.Contains("+Untracked content", StringComparison.Ordinal),
            "Uncommitted diff must include net staged and unstaged content against HEAD.");
        Require(await host.GetDiffSidesAsync(unusual, "uncommitted") == new DiffSides("", "Current unstaged content\nSecond line\n") &&
            await host.GetDiffSidesAsync(unusual, "working") == new DiffSides("Untracked content\n", "Current unstaged content\nSecond line\n"),
            "Diff sides must follow the scope's range, including index-only files.");
        var pending = (await host.GetGitAsync(scope: "uncommitted")).Changes.Single(change => change.Path == unusual);
        var staged = (await host.GetGitAsync(scope: "staged")).Changes.Single(change => change.Path == unusual);
        Require(pending.Added == 2 && pending.Removed == 0 && staged.Added == 1 && staged.Removed == 0,
            "Scope counts must measure their own range rather than sum index and worktree deltas.");
        File.Delete(Path.Combine(root, unusual));
        Require(!(await host.GetGitAsync(scope: "uncommitted")).Changes.Any(change => change.Path == unusual) &&
            (await host.GetGitAsync(scope: "staged")).Changes.Any(change => change.Path == unusual),
            "An index addition undone in the worktree must disappear only from the net Uncommitted list.");
        await File.WriteAllTextAsync(Path.Combine(root, unusual), "Untracked content\n");
        snapshot = await host.ApplyGitActionAsync(new("unstage", unusual));
        Require(snapshot.Changes.Any(change => change.Path == unusual && change.IndexStatus == "?"), "Unstaging failed.");
        var branch = "sharprail-check-" + Guid.NewGuid().ToString("N")[..8];
        var worktree = root + "-worktree";
        snapshot = await host.ApplyGitActionAsync(new("create-worktree", worktree, branch));
        Require(snapshot.Worktrees.Any(tree => tree.Path == worktree && tree.Branch == branch), "Worktree creation/listing failed.");
        var linked = new ProjectServices(worktree);
        var linkedInfo = await linked.OpenProjectAsync(worktree);
        Require(linkedInfo.RootPath == worktree && linkedInfo.ProjectRoot == root,
            "Linked workspace identity did not resolve its main project before loading Git status.");
        await using (var linkedServer = RemoteServer.Create(worktree, IPAddress.Loopback, 0, "linked-test"))
        {
            await linkedServer.StartAsync();
            var linkedAddress = linkedServer.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var linkedRemote = new RemoteProjectAdapter(new Uri(linkedAddress), "linked-test");
            Require(await linkedRemote.OpenProjectAsync(worktree) == linkedInfo, "Remote linked workspace identity differs.");
            await linkedServer.StopAsync();
        }
        await File.WriteAllTextAsync(Path.Combine(worktree, "dirty.txt"), "dirty");
        try
        {
            await host.ApplyGitActionAsync(new("remove-worktree", worktree));
            throw new InvalidOperationException("Dirty worktree was removed.");
        }
        catch (IOException) { }
        Require(Directory.Exists(worktree), "Dirty worktree data disappeared.");
        // The harness owns this exact generated fixture file.
        File.Delete(Path.Combine(worktree, "dirty.txt"));
        snapshot = await host.ApplyGitActionAsync(new("remove-worktree", worktree));
        Require(!snapshot.Worktrees.Any(tree => tree.Path == worktree), "Worktree removal failed.");
        var headComparison = await host.GetGitAsync("HEAD");
        Require(headComparison.Changes.Single().Path == unusual && headComparison.Changes.Single().Added == 1,
            "Comparison with HEAD must retain the untracked working file and its line count.");
        Require(await host.GetDiffAsync(unusual, "branch", "HEAD") == UntrackedDiff(unusual),
            "Branch comparisons must open untracked files as added lines.");
        await CheckAdvancedTarget(host, root, unusual);
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, "git-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "git-test");
            await remote.OpenProjectAsync(root);
            Require((await remote.GetGitAsync()).Changes.SequenceEqual((await host.GetGitAsync()).Changes), "Remote git differs.");
            Require((await remote.GetGitAsync("HEAD")).Changes.SequenceEqual((await host.GetGitAsync("HEAD")).Changes) &&
                await remote.GetDiffAsync(unusual, "branch", "HEAD") == await host.GetDiffAsync(unusual, "branch", "HEAD"),
                "Remote working-tree branch comparison differs.");
            Require(await remote.GetDiffAsync(unusual, "untracked") == await host.GetDiffAsync(unusual, "untracked"), "Remote diff differs.");
            var readme = await host.GetDiffSidesAsync("README.md", "all");
            Require(readme.Original == await Git(root, "show", "HEAD:README.md") && readme.Modified == await File.ReadAllTextAsync(Path.Combine(root, "README.md")) &&
                await remote.GetDiffSidesAsync("README.md", "all") == readme &&
                await remote.GetDiffSidesAsync(unusual, "branch", "HEAD") == new DiffSides("", "Untracked content\n") &&
                await host.GetDiffSidesAsync(unusual, "branch", "HEAD") == new DiffSides("", "Untracked content\n"),
                "Remote diff sides differ from the local host.");
            var localBranches = await host.ListBranchesAsync(false);
            var remoteBranches = await remote.ListBranchesAsync(false);
            Require(remoteBranches.Local.SequenceEqual(localBranches.Local) && remoteBranches.Remote.SequenceEqual(localBranches.Remote) &&
                remoteBranches.DefaultBase == localBranches.DefaultBase &&
                (localBranches.Local.Contains(localBranches.DefaultBase) || localBranches.Remote.Any(branch => branch.Ref == localBranches.DefaultBase)) &&
                remoteBranches.SuggestedPath == localBranches.SuggestedPath && remoteBranches.SuggestedBranch == localBranches.SuggestedBranch &&
                localBranches.SuggestedPath == Path.Combine(root + "-worktrees", localBranches.SuggestedBranch),
                $"Remote branch catalog differs: {localBranches.DefaultBase} {localBranches.SuggestedPath} {root}.");
            Require((await remote.ListEditorsAsync()).SequenceEqual(await host.ListEditorsAsync()), "Remote editor list differs.");
            foreach (var service in new IProjectServices[] { host, remote })
            {
                try
                {
                    await service.OpenInEditorAsync("vscode", Path.GetTempPath());
                    throw new InvalidOperationException("An editor opened a path outside the project's workspaces.");
                }
                catch (Exception error) when (error is UnauthorizedAccessException or Grpc.Core.RpcException) { }
            }
            Require(await remote.GetDiffAsync(unusual, "uncommitted") == UntrackedDiff(unusual) &&
                await remote.GetDiffAsync(unusual, "uncommitted") == await host.GetDiffAsync(unusual, "uncommitted"),
                "Remote Uncommitted diff must retain untracked content.");
            await host.ApplyGitActionAsync(new("stage", unusual));
            await File.WriteAllTextAsync(Path.Combine(root, unusual), "Untracked content\nRemote pending line\n");
            foreach (var scope in new[] { "all", "uncommitted", "staged" })
            {
                var localChanges = (await host.GetGitAsync(scope: scope)).Changes;
                var remoteChanges = (await remote.GetGitAsync(scope: scope)).Changes;
                Require(remoteChanges.SequenceEqual(localChanges), "Remote scope range differs: " + scope);
                var change = remoteChanges.Single(change => change.Path == unusual);
                Require(change.Added == (scope == "staged" ? 1 : 2) && change.Removed == 0,
                    "Remote scope counts used the wrong range: " + scope);
            }
            await File.WriteAllTextAsync(Path.Combine(root, unusual), "Untracked content\n");
            await host.ApplyGitActionAsync(new("unstage", unusual));
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS isolated git staging/diffs, unusual filenames, branch comparison, worktrees and remote parity");
    }

    private static async Task CheckAdvancedTarget(ProjectServices host, string root, string path)
    {
        var target = (await Git(root, "rev-parse", "HEAD")).Trim();
        var fork = (await Git(root, "rev-parse", "HEAD^")).Trim();
        var committed = (await Git(root, "diff", "--name-only", "-z", fork, target, "--"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Require(committed.Length > 0, "The comparison fixture needs an existing commit that changes its tree.");
        var commitSnapshot = await host.GetGitAsync(target, scope: "commit");
        Require(commitSnapshot.Changes.Select(change => change.Path).Order().SequenceEqual(committed.Order()) &&
            !commitSnapshot.Changes.Any(change => change.Path == path),
            "Commit scope must contain only its committed changes, excluding working files.");
        var commitPath = committed[0];
        var commitDiff = await host.GetDiffAsync(commitPath, "commit", target);
        Require(commitDiff == await Git(root, "diff", "--no-renames", "--no-ext-diff", "--no-color", "--unified=5", fork, target, "--", commitPath),
            "Commit diff must use its first-parent range.");
        async Task<string> Blob(string revision)
        {
            try { return await Git(root, "show", revision + ":" + commitPath); }
            catch (IOException) { return ""; }
        }
        Require(await host.GetDiffSidesAsync(commitPath, "commit", target) == new DiffSides(await Blob(fork), await Blob(target)),
            "Commit diff sides must use its first-parent range.");
        var rootNames = (await Git(root, "show", "--format=", "--no-renames", "--name-only", "-z", fork, "--"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Require((await host.GetGitAsync(fork, scope: "commit")).Changes.Select(change => change.Path).Order().SequenceEqual(rootNames.Order()),
            "A commit without an available parent must show its whole committed tree.");
        Require(rootNames.Length > 0 && await host.GetDiffAsync(rootNames[0], "commit", fork) ==
            await Git(root, "show", "--format=", "--no-renames", "--no-ext-diff", "--no-color", "--unified=5", fork, "--", rootNames[0]),
            "A parentless commit diff must use its committed tree rather than current working content.");
        try { await host.GetGitAsync("--all", scope: "commit"); throw new InvalidOperationException("Commit scope accepted an option as a commit."); }
        catch (ArgumentException) { }
        try { await host.GetGitAsync(new string('0', 40), scope: "commit"); throw new InvalidOperationException("An unknown commit became a clean change set."); }
        catch (IOException) { }
        var current = await host.GetGitAsync(fork);
        Require(current.Commits.Count == 1 && current.Commits[0].Sha == target && current.Commits[0].Subject.Length > 0,
            "Commit catalog must contain commits from the independent target to HEAD.");
        Require(committed.All(name => current.Changes.Any(change => change.Path == name)),
            "Comparison must include local committed changes since the fork as well as working changes.");
        var behind = root + "-fork";
        await host.ApplyGitActionAsync(new("create-worktree", behind, "sharprail-fork", fork));
        await Git(root, "branch", "feature/tree/local", fork);
        await Git(root, "remote", "add", "tree/remote", root);
        await Git(root, "update-ref", "refs/remotes/tree/remote/feature/tree/local", fork);
        await Git(root, "symbolic-ref", "refs/remotes/tree/remote/HEAD", "refs/remotes/tree/remote/feature/tree/local");
        var forkHost = new ProjectServices(behind);
        await forkHost.OpenProjectAsync(behind);
        await File.WriteAllTextAsync(Path.Combine(behind, path), "Only local work\n");
        var snapshot = await forkHost.GetGitAsync(target);
        Require(snapshot.Changes.Count == 1 && snapshot.Changes[0].Path == path && snapshot.Changes[0].Added == 1,
            "A target advanced beyond the fork must not add phantom deletions.");
        await forkHost.ApplyGitActionAsync(new("stage", path));
        await File.WriteAllTextAsync(Path.Combine(behind, path), "Current working content\n");
        snapshot = await forkHost.GetGitAsync(target);
        Require(snapshot.Changes.Single().Added == 1 && snapshot.Changes.Single().Removed == 0 &&
            (await forkHost.GetDiffAsync(path, "branch", target)).Contains("Current working content", StringComparison.Ordinal),
            "Branch statistics and diff must measure the net working content, including staged and unstaged edits.");
        await using var server = RemoteServer.Create(behind, IPAddress.Loopback, 0, "fork-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "fork-test");
            await remote.OpenProjectAsync(behind);
            await remote.OpenProjectAsync(root);
            Require((await remote.GetGitAsync(fork)).Commits.SequenceEqual(current.Commits),
                "Remote commit catalog must preserve IDs, subjects, authors and dates.");
            var localCatalog = new LocalProjectAdapter(host);
            Require((await localCatalog.ListCommitsAsync(fork)).SequenceEqual(current.Commits) &&
                (await remote.ListCommitsAsync(fork)).SequenceEqual(current.Commits),
                "Independent local and remote catalogs must preserve the snapshot's commit metadata.");
            var indexPath = Path.Combine(root, ".git", "index");
            var index = await File.ReadAllBytesAsync(indexPath);
            try
            {
                await File.WriteAllTextAsync(indexPath, "invalid-index\n");
                Require((await localCatalog.ListCommitsAsync(fork)).SequenceEqual(current.Commits) &&
                    (await remote.ListCommitsAsync(fork)).SequenceEqual(current.Commits),
                    "Commit catalogs must not read the working index or require a full Git snapshot.");
                try { await host.GetGitAsync(fork); throw new InvalidOperationException("Fixture did not break index reads."); }
                catch (IOException) { }
            }
            finally { await File.WriteAllBytesAsync(indexPath, index); }
            Require((await localCatalog.ListCommitsAsync("sharprail-missing-target")).Count == 0 &&
                (await remote.ListCommitsAsync("sharprail-missing-target")).Count == 0,
                "An unresolved catalog range must return the reference's empty list.");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await localCatalog.ListCommitsAsync("", canceled.Token); throw new InvalidOperationException("Catalog ignored cancellation."); }
                catch (OperationCanceledException) { }
            }
            Console.WriteLine("PASS lightweight local/remote commit catalogs independent of index reads");
            await remote.OpenProjectAsync(behind);
            Require((await remote.GetGitAsync(target)).Commits.Count == 0,
                "A worktree behind its target must not inherit another workspace's commits.");
            Require((await remote.GetGitAsync(target, scope: "commit")).Changes.SequenceEqual(commitSnapshot.Changes) &&
                await remote.GetDiffAsync(commitPath, "commit", target) == commitDiff,
                "Remote commit snapshots and diffs must be independent of the active worktree's dirty content.");
            Require((await remote.GetGitAsync(target)).Changes.SequenceEqual(snapshot.Changes) &&
                await remote.GetDiffAsync(path, "branch", target) == await forkHost.GetDiffAsync(path, "branch", target),
                "Remote advanced-target snapshot and diff must share the local merge-base semantics.");
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS branch merge-base comparison excludes target-only commits and includes net working content");
        Console.WriteLine("PASS commit first-parent/root ranges, dirty-file exclusion, invalid ids and local/remote parity");
    }

    private static string UntrackedDiff(string path) =>
        $"diff --git a/{path} b/{path}\nnew file mode 100644\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1 @@\n+Untracked content\n";
}