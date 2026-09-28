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
            Require(await remote.ReadFileAsync("README.md") == markdown, "Remote text differs.");
            Require((await remote.ListSpecsAsync()).SequenceEqual(await local.ListSpecsAsync()), "Remote specs differ.");
            var snapshot = await remote.GetGitAsync();
            Require(!snapshot.IsRepository, "Non-git directory detected as repository.");
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
        await Git(parent, "clone", "--no-local", "--depth=1", "--no-checkout", new Uri(Path.GetFullPath(source)).AbsoluteUri, root);
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
        snapshot = await host.ApplyGitActionAsync(new("unstage", unusual));
        Require(snapshot.Changes.Any(change => change.Path == unusual && change.IndexStatus == "?"), "Unstaging failed.");
        var branch = "sharprail-check-" + Guid.NewGuid().ToString("N")[..8];
        var worktree = root + "-worktree";
        snapshot = await host.ApplyGitActionAsync(new("create-worktree", worktree, branch));
        Require(snapshot.Worktrees.Any(tree => tree.Path == worktree && tree.Branch == branch), "Worktree creation/listing failed.");
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
        Require((await host.GetGitAsync("HEAD")).Changes.Count == 0, "Branch comparison failed.");
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, "git-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "git-test");
            await remote.OpenProjectAsync(root);
            Require((await remote.GetGitAsync()).Changes.SequenceEqual((await host.GetGitAsync()).Changes), "Remote git differs.");
            Require(await remote.GetDiffAsync(unusual, "untracked") == await host.GetDiffAsync(unusual, "untracked"), "Remote diff differs.");
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS isolated git staging/diffs, unusual filenames, branch comparison, worktrees and remote parity");
    }
}
