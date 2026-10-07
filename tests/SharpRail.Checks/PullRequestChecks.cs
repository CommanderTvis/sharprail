using System.Diagnostics;
using System.Net;

using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Open-PR lookup and PR opening against local bare origins and PATH shims for gh; nothing reaches a network.</summary>
internal static class PullRequestChecks
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
        return await output;
    }

    internal const string GhShim = """
        #!/bin/bash
        echo "$@" >> "$SHIM_DIR/log"
        [ -f "$SHIM_DIR/hang" ] && { (sleep 30) & sleep 30; exit 0; }
        [ -f "$SHIM_DIR/slow" ] && sleep 1
        case "$1 $2" in
          "pr list") [ -f "$SHIM_DIR/fail-list" ] && exit 1; cat "$SHIM_DIR/pr.json" 2>/dev/null || echo '[]' ;;
          "pr create")
            echo '[{"number":7,"url":"https://github.com/o/r/pull/7"}]' > "$SHIM_DIR/pr.json"
            [ -f "$SHIM_DIR/fail-create" ] && exit 1
            echo https://github.com/o/r/pull/7 ;;
          "pr edit") exit 0 ;;
          "auth status") [ -f "$SHIM_DIR/unauth" ] && exit 1; exit 0 ;;
        esac
        """;

    private static int Calls(string shim, string text) =>
        File.Exists(Path.Combine(shim, "log")) ? File.ReadAllLines(Path.Combine(shim, "log")).Count(line => line.StartsWith(text, StringComparison.Ordinal)) : 0;

    internal static async Task<string> MakeRepo(string parent, string name, bool github)
    {
        var bare = Path.Combine(parent, name + ".git");
        var seed = Path.Combine(parent, name + "-seed");
        var work = Path.Combine(parent, name);
        Directory.CreateDirectory(seed);
        await Git(parent, "init", "--bare", "-b", "main", bare);
        await Git(seed, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(seed, "a.txt"), "a\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "-m", "Seed");
        await Git(seed, "remote", "add", "origin", bare);
        await Git(seed, "push", "origin", "main");
        await Git(parent, "clone", bare, work);
        await Git(work, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(work, "b.txt"), "b\n");
        await Git(work, "add", ".");
        await Git(work, "commit", "-m", "Add b");
        if (github)
        {
            await Git(work, "remote", "set-url", "origin", "https://github.com/o/r.git");
            await Git(work, "config", "url." + bare + ".pushInsteadOf", "https://github.com/o/r.git");
        }
        return work;
    }

    public static Task Run(string fixture) => OperatingSystem.IsWindows() ? Task.CompletedTask : RunShimmed(fixture);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task RunShimmed(string fixture)
    {
        var parent = Path.Combine(fixture, "pr-checks");
        var shim = Path.Combine(parent, "shim");
        var gitOnly = Path.Combine(parent, "git-only");
        Directory.CreateDirectory(shim);
        Directory.CreateDirectory(gitOnly);
        var gh = Path.Combine(shim, "gh");
        await File.WriteAllTextAsync(gh, GhShim.Replace("\n        ", "\n").TrimStart());
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var gitPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(dir => Path.Combine(dir, "git")).First(File.Exists);
        File.CreateSymbolicLink(Path.Combine(gitOnly, "git"), gitPath);
        var config = Path.Combine(parent, "gitconfig");
        await File.WriteAllTextAsync(config, "[user]\n\tname = T\n\temail = t@example.com\n[commit]\n\tgpgsign = false\n[http]\n\tproxy = http://127.0.0.1:1\n");

        var saved = new[] { "PATH", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "SHIM_DIR", "SHARPRAIL_GH_OFFLINE", "GIT_SSH_COMMAND" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            Environment.SetEnvironmentVariable("SHIM_DIR", shim);
            Environment.SetEnvironmentVariable("SHARPRAIL_GH_OFFLINE", null);
            Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", null);
            await Plain(parent, gitOnly);
            Environment.SetEnvironmentVariable("PATH", shim + ":" + gitOnly + ":/usr/bin:/bin");
            await GitHub(parent, shim, gitOnly);
            Console.WriteLine("PASS pull request lookup and opening through gh shims and bare origins, locally and over gRPC");
        }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }

    private static async Task Plain(string parent, string gitOnly)
    {
        Environment.SetEnvironmentVariable("PATH", gitOnly);
        var work = await MakeRepo(parent, "plain", github: false);
        var bare = Path.Combine(parent, "plain.git");
        IProjectServices host = new LocalProjectAdapter(new ProjectServices(work));
        await host.OpenProjectAsync(work);
        Require(await host.GetOpenReviewAsync(true) is null, "A non-GitHub origin must have no open review.");
        var draft = await host.PreviewPrAsync();
        Require(draft.Title == "feature" && draft.Body == "- Add b", $"Unexpected draft: {draft}.");

        await File.WriteAllTextAsync(Path.Combine(work, "dirty.txt"), "x");
        var pushed = await host.OpenPrAsync(new("t", false, "", false));
        Require(pushed.Action == "pushed" && pushed.DirtyFiles == 1, "A non-GitHub origin should only push, reporting dirty files.");
        Require((await Git(bare, "rev-parse", "refs/heads/feature")).Trim() == (await Git(work, "rev-parse", "HEAD")).Trim(), "The branch did not reach origin.");

        var mainBefore = (await Git(bare, "rev-parse", "refs/heads/main")).Trim();
        await Git(work, "checkout", "main");
        await Git(work, "commit", "--allow-empty", "-m", "Local main work");
        try { await host.OpenPrAsync(new("t", false, "", false)); throw new InvalidOperationException("The default branch was pushed."); }
        catch (InvalidOperationException error) when (error.Message.Contains("default branch", StringComparison.Ordinal)) { }
        Require((await Git(bare, "rev-parse", "refs/heads/main")).Trim() == mainBefore, "Origin's default branch moved.");

        await Git(work, "checkout", "feature");
        await Git(work, "remote", "set-head", "origin", "-d");
        var featureBefore = (await Git(bare, "rev-parse", "refs/heads/feature")).Trim();
        await Git(work, "commit", "--allow-empty", "-m", "More");
        try { await host.OpenPrAsync(new("t", false, "", false)); throw new InvalidOperationException("A non-origin base was accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("not on origin", StringComparison.Ordinal)) { }
        Require((await Git(bare, "rev-parse", "refs/heads/feature")).Trim() == featureBefore, "A rejected base still pushed.");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task GitHub(string parent, string shim, string gitOnly)
    {
        var work = await MakeRepo(parent, "hub", github: true);
        var bare = Path.Combine(parent, "hub.git");
        IProjectServices host = new LocalProjectAdapter(new ProjectServices(work));
        await host.OpenProjectAsync(work);
        var found = "[{\"number\":7,\"url\":\"https://github.com/o/r/pull/7\"}]";

        // Lookup: empty answers are cached, fresh bypasses, failures and non-https links are not served.
        Require(await host.GetOpenReviewAsync(false) is null, "No pull request expected yet.");
        await File.WriteAllTextAsync(Path.Combine(shim, "pr.json"), found);
        Require(await host.GetOpenReviewAsync(false) is null && Calls(shim, "pr list") == 1, "A repeated lookup within the TTL must be served from cache.");
        var review = await host.GetOpenReviewAsync(true);
        Require(review is { Number: 7, Provider: "github", Url: "https://github.com/o/r/pull/7" } && Calls(shim, "pr list") == 2, "A fresh lookup must bypass the cache.");
        await File.WriteAllTextAsync(Path.Combine(shim, "pr.json"), found.Replace("https://", "javascript://"));
        Require(await host.GetOpenReviewAsync(true) is null, "A non-https url must be dropped.");
        await File.WriteAllTextAsync(Path.Combine(shim, "pr.json"), found);
        await File.WriteAllTextAsync(Path.Combine(shim, "fail-list"), "");
        Require(await host.GetOpenReviewAsync(true) is null, "A failing gh must degrade to no review.");
        File.Delete(Path.Combine(shim, "fail-list"));
        Require(await host.GetOpenReviewAsync(false) is { Number: 7 }, "A failure must not be cached.");

        // Concurrent lookups share one gh call.
        await File.WriteAllTextAsync(Path.Combine(shim, "slow"), "");
        File.Delete(Path.Combine(shim, "log"));
        await Git(work, "checkout", "-b", "other");
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ => await host.GetOpenReviewAsync(true)));
        Require(Calls(shim, "pr list") == 1, $"Concurrent lookups ran gh {Calls(shim, "pr list")} times.");
        await Git(work, "checkout", "feature");
        File.Delete(Path.Combine(shim, "slow"));

        // A hung gh whose grandchild keeps the pipe open is killed within the budget.
        await File.WriteAllTextAsync(Path.Combine(shim, "hang"), "");
        var clock = Stopwatch.StartNew();
        Require(await host.GetOpenReviewAsync(true) is null && clock.Elapsed < TimeSpan.FromSeconds(14), "A hung gh was not stopped in time.");
        File.Delete(Path.Combine(shim, "hang"));

        // Opening: an existing pull request is edited by number, the title only when edited.
        File.Delete(Path.Combine(shim, "log"));
        var updated = await host.OpenPrAsync(new("New title", false, "Body", false));
        var edit = File.ReadAllLines(Path.Combine(shim, "log")).Single(line => line.StartsWith("pr edit", StringComparison.Ordinal));
        Require(updated is { Action: "updated", Number: 7 } && edit.StartsWith("pr edit 7", StringComparison.Ordinal) && !edit.Contains("--title", StringComparison.Ordinal),
            "An untouched title must not be sent when updating.");
        await host.OpenPrAsync(new("New title", true, "Body", false));
        Require(File.ReadAllLines(Path.Combine(shim, "log")).Any(line => line.StartsWith("pr edit 7", StringComparison.Ordinal) && line.Contains("--title New title", StringComparison.Ordinal)),
            "An edited title must be sent.");
        Require((await Git(bare, "rev-parse", "refs/heads/feature")).Trim() == (await Git(work, "rev-parse", "HEAD")).Trim(), "Opening did not push the branch.");
        Require((await host.GetOpenReviewAsync(true))?.UnpushedCommits == 0, "Unpushed commits should be zero after a push.");

        // Creating names the base explicitly, and a create that failed after succeeding is still created.
        File.Delete(Path.Combine(shim, "pr.json"));
        File.Delete(Path.Combine(shim, "log"));
        var created = await host.OpenPrAsync(new("T", false, "B", true));
        Require(created is { Action: "created", Number: 7 } && File.ReadAllText(Path.Combine(shim, "log")).Contains("pr create --base main --head feature", StringComparison.Ordinal) &&
            File.ReadAllText(Path.Combine(shim, "log")).Contains("--draft", StringComparison.Ordinal), "Create must pass an explicit base and honour draft.");
        File.Delete(Path.Combine(shim, "pr.json"));
        await File.WriteAllTextAsync(Path.Combine(shim, "fail-create"), "");
        Require((await host.OpenPrAsync(new("T", false, "B", false))).Action == "created", "A failed create followed by a found pull request must report created.");
        File.Delete(Path.Combine(shim, "fail-create"));

        // gh failure falls back to a compare link with a capped body and the problem.
        File.Delete(Path.Combine(shim, "pr.json"));
        await File.WriteAllTextAsync(Path.Combine(shim, "fail-list"), "");
        await File.WriteAllTextAsync(Path.Combine(shim, "unauth"), "");
        var compare = await host.OpenPrAsync(new("T", false, new string('a', 6000), false));
        Require(compare.Action == "compare" && compare.GhProblem == "unauthenticated" && compare.Url.StartsWith("https://github.com/o/r/compare/main...feature?quick_pull=1", StringComparison.Ordinal) &&
            compare.Url.Contains(new string('a', 4000), StringComparison.Ordinal) && !compare.Url.Contains(new string('a', 4001), StringComparison.Ordinal), "Unexpected compare fallback: " + compare);
        File.Delete(Path.Combine(shim, "fail-list"));
        File.Delete(Path.Combine(shim, "unauth"));

        // The offline seam never calls gh; a missing gh is reported as missing.
        File.Delete(Path.Combine(shim, "log"));
        Environment.SetEnvironmentVariable("SHARPRAIL_GH_OFFLINE", "1");
        Require((await host.OpenPrAsync(new("T", false, "", false))).Action == "compare" && Calls(shim, "pr") == 0, "Offline mode must not call gh.");
        Environment.SetEnvironmentVariable("SHARPRAIL_GH_OFFLINE", null);
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", gitOnly);
        try { Require((await host.OpenPrAsync(new("T", false, "", false))).GhProblem == "missing", "A missing gh must be reported."); }
        finally { Environment.SetEnvironmentVariable("PATH", path); }

        // A rejected SSH login is classified instead of surfacing as a generic failure.
        var key = Path.Combine(parent, "deny-ssh");
        await File.WriteAllTextAsync(key, "#!/bin/bash\necho 'git@github.com: Permission denied (publickey).' >&2\nexit 255\n");
        File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Git(work, "config", "url.ssh://git@github.com/o/r.git.pushInsteadOf", "https://github.com/o/r.git");
        await Git(work, "config", "--unset", "url." + bare + ".pushInsteadOf");
        Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", key);
        Require((await host.OpenPrAsync(new("T", false, "", false))).Action == "authFailed", "An SSH authentication failure must be classified.");
        Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", null);
        await Git(work, "config", "--unset", "url.ssh://git@github.com/o/r.git.pushInsteadOf");
        await Git(work, "config", "url." + bare + ".pushInsteadOf", "https://github.com/o/r.git");

        // The remote adapter returns what the local one does.
        await File.WriteAllTextAsync(Path.Combine(shim, "pr.json"), found);
        await using var server = RemoteServer.Create(work, IPAddress.Loopback, 0, "pr-test");
        await server.StartAsync();
        var address = server.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
        using var remote = new RemoteProjectAdapter(new Uri(address), "pr-test");
        await remote.OpenProjectAsync(work);
        Require(await remote.GetOpenReviewAsync(true) == await host.GetOpenReviewAsync(true), "Remote open review differs.");
        Require(await remote.PreviewPrAsync() == await host.PreviewPrAsync(), "Remote draft differs.");
        var request = new PrRequest("T", false, "B", false);
        Require(await remote.OpenPrAsync(request) == await host.OpenPrAsync(request), "Remote pull request result differs.");
        await server.StopAsync();
    }
}