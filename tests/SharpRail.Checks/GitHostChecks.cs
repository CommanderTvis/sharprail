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
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException) { return error.Message; }
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
            IProjectServices local = new LocalProjectAdapter(new ProjectServices(localRoot));
            await local.OpenProjectAsync(localRoot);
            var localLog = await Scenarios(local, localRoot);

            await using var server = RemoteServer.Create(remoteRoot, IPAddress.Loopback, 0, "git-host-test");
            await server.StartAsync();
            try
            {
                var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var remote = new RemoteProjectAdapter(new Uri(address), "git-host-test");
                await remote.OpenProjectAsync(remoteRoot);
                var remoteLog = await Scenarios(remote, remoteRoot);
                Require(localLog.SequenceEqual(remoteLog),
                    "Local and remote Git results differ:\n" + string.Join('\n', localLog.Zip(remoteLog).Where(pair => pair.First != pair.Second).Select(pair => pair.First + "  <>  " + pair.Second)));
            }
            finally { await server.StopAsync(); }
        }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
        Console.WriteLine("PASS Git host checks: ref shapes at every door, locally and over gRPC");
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

    private static async Task<List<string>> Scenarios(IProjectServices host, string root)
    {
        var log = new List<string>();
        await RefDoors(host, root, log);
        return log;
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