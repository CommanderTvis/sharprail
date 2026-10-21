using System.Diagnostics;
using System.Net;

using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI;

namespace SharpRail.Checks;

/// <summary>
/// Upstream #771 and #776: the host's branch status (remote facts, review and its details) and branch actions against
/// local bare origins and a gh shim, the header's next-step rule, and remote-adapter parity. Nothing reaches a network.
/// </summary>
internal static class BranchStatusChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private const string Shim = """
        #!/bin/bash
        echo "$@" >> "$SHIM_DIR/log"
        case "$1 $2" in
          "pr list")
            case "$*" in
              *"--state all"*) cat "$SHIM_DIR/details.json" 2>/dev/null || echo '[]' ;;
              *) cat "$SHIM_DIR/pr.json" 2>/dev/null || echo '[]' ;;
            esac ;;
          "repo view") echo '{"mergeCommitAllowed":false,"squashMergeAllowed":true,"rebaseMergeAllowed":true}' ;;
          "pr ready"|"pr reopen") exit 0 ;;
          "pr merge") [ -f "$SHIM_DIR/merged.json" ] && cp "$SHIM_DIR/merged.json" "$SHIM_DIR/details.json"; exit 0 ;;
          "auth status") exit 0 ;;
        esac
        """;

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

    private static string Details(string state, bool draft, string checks, string merge, string owner = "o") =>
        $$"""[{"number":7,"url":"https://github.com/o/r/pull/7","state":"{{state}}","isDraft":{{(draft ? "true" : "false")}},"reviewDecision":"APPROVED","mergeStateStatus":"{{merge}}","headRepositoryOwner":{"login":"{{owner}}"},"headRepository":{"name":"r"},"statusCheckRollup":{{checks}}}]""";

    public static Task Run(string fixture)
    {
        Rules();
        return OperatingSystem.IsWindows() ? Task.CompletedTask : RunShimmed(fixture);
    }

    // The header's one next-step rule.
    private static void Rules()
    {
        BranchStatus Status(bool origin = true, bool published = true, int ahead = 0, int behind = 0, bool onBase = false, ReviewDetails? details = null, string provider = "github") =>
            new("feature", origin, origin ? provider : "", published, ahead, behind, 1, onBase, "main", "abc", null, details);
        ReviewDetails Pr(string state = "OPEN", bool draft = false, string checks = "passing", string merge = "CLEAN") => new(7, "https://github.com/o/r/pull/7", state, draft, checks, "", merge, ["squash"], true);
        Require(WorkbenchWindow.ShipActions(Status(origin: false)) == ("", ""), "No origin offers nothing to push.");
        Require(WorkbenchWindow.ShipActions(Status(published: false)) == ("publish", "open"), "An unpublished branch publishes and may open a PR.");
        Require(WorkbenchWindow.ShipActions(Status(ahead: 2, details: Pr())) == ("push", ""), "Ahead of origin: Push, and the PR waits.");
        Require(WorkbenchWindow.ShipActions(Status(ahead: 1, behind: 1, details: Pr())).Remote == "sync", "Diverged: Sync, never a force push.");
        Require(WorkbenchWindow.ShipActions(Status(details: Pr(draft: true))).PullRequest == "ready", "A draft's next step is Ready for review.");
        Require(WorkbenchWindow.ShipActions(Status(details: Pr())).PullRequest == "merge", "A clean PR in sync merges.");
        Require(WorkbenchWindow.ShipActions(Status(details: Pr(checks: "failing"))).PullRequest == "", "Failing checks do not merge.");
        Require(WorkbenchWindow.ShipActions(Status(details: Pr(merge: "BLOCKED"))).PullRequest == "", "Merge follows merge state, never approvals.");
        Require(WorkbenchWindow.ShipActions(Status(details: Pr(state: "MERGED"))).PullRequest == "delete-remote", "A merged PR offers deleting its remote branch.");
        Require(WorkbenchWindow.ShipActions(Status(details: Pr(state: "CLOSED"))).PullRequest == "reopen", "A closed PR offers Reopen.");
        Require(WorkbenchWindow.ShipActions(Status(onBase: true)).PullRequest == "", "The base branch has no PR step.");
        Require(WorkbenchWindow.ShipActions(Status(provider: "other")).PullRequest == "", "A plain git origin has no PR step.");
        Console.WriteLine("PASS upstream shipActions (#771, #776): one next step per segment from the branch status");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task RunShimmed(string fixture)
    {
        var parent = Path.Combine(fixture, "branch-status");
        var shim = Path.Combine(parent, "shim");
        Directory.CreateDirectory(shim);
        var gh = Path.Combine(shim, "gh");
        await File.WriteAllTextAsync(gh, Shim.Replace("\n        ", "\n").TrimStart());
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = Path.Combine(parent, "gitconfig");
        await File.WriteAllTextAsync(config, "[user]\n\tname = T\n\temail = t@example.com\n[commit]\n\tgpgsign = false\n[http]\n\tproxy = http://127.0.0.1:1\n");
        var saved = new[] { "PATH", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "SHIM_DIR", "SHARPRAIL_GH_OFFLINE" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            Environment.SetEnvironmentVariable("SHIM_DIR", shim);
            Environment.SetEnvironmentVariable("SHARPRAIL_GH_OFFLINE", null);
            Environment.SetEnvironmentVariable("PATH", shim + ":" + saved["PATH"]);
            await Plain(parent);
            await Hub(parent, shim);
        }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }

    private static async Task Plain(string parent)
    {
        var work = await PullRequestChecks.MakeRepo(parent, "plain", github: false);
        var bare = Path.Combine(parent, "plain.git");
        IProjectServices host = new LocalProjectAdapter(new ProjectServices(work));
        await host.OpenProjectAsync(work);
        var status = await host.GetBranchStatusAsync(true);
        Require(status is { HasOrigin: true, Provider: "other", Published: false, OnBase: false, AheadOfBase: 1, Review: null, Details: null }, "Unpublished: " + status);
        Require((await host.ApplyBranchActionAsync(new("push", "feature", status!.Head))).Outcome == "done", "Publish must push.");
        status = await host.GetBranchStatusAsync(true);
        Require(status is { Published: true, Ahead: 0, Behind: 0 }, "Published and in sync: " + status);
        await Git(work, "commit", "--allow-empty", "-m", "Local");
        try { await host.ApplyBranchActionAsync(new("push", "feature", status!.Head)); throw new InvalidOperationException("A push for a moved branch was accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("moved", StringComparison.Ordinal)) { }
        status = await host.GetBranchStatusAsync(false);
        Require(status is { Ahead: 1, Behind: 0 }, "Ahead of origin: " + status);
        // Someone else pushes to origin: behind shows after a fetch, and the header offers Sync.
        var other = Path.Combine(parent, "plain-other");
        await Git(parent, "clone", "-b", "feature", bare, other);
        await Git(other, "commit", "--allow-empty", "-m", "Theirs");
        await Git(other, "push", "origin", "feature");
        status = await host.GetBranchStatusAsync(true);
        Require(status is { Ahead: 1, Behind: 1 } && WorkbenchWindow.ShipActions(status).Remote == "sync", "Diverged: " + status);
        // The base branch is pushed only after the caller confirms it.
        await Git(work, "checkout", "main");
        await Git(work, "commit", "--allow-empty", "-m", "Main work");
        status = await host.GetBranchStatusAsync(false);
        Require(status is { OnBase: true, Branch: "main" }, "On base: " + status);
        var mainBefore = (await Git(bare, "rev-parse", "refs/heads/main")).Trim();
        Require((await host.ApplyBranchActionAsync(new("push", "main", status!.Head))).Outcome == "confirmBase" &&
            (await Git(bare, "rev-parse", "refs/heads/main")).Trim() == mainBefore, "Pushing the base must ask first.");
        Require((await host.ApplyBranchActionAsync(new("push", "main", status.Head, ConfirmBase: true))).Outcome == "done" &&
            (await Git(bare, "rev-parse", "refs/heads/main")).Trim() == status.Head, "A confirmed base push must land.");
        await Git(work, "checkout", "feature");
        Console.WriteLine("PASS upstream #771: branch status reports origin, publication, ahead/behind and base; Publish/Push are bound to the shown commit; the base asks first");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task Hub(string parent, string shim)
    {
        var work = await PullRequestChecks.MakeRepo(parent, "hub", github: true);
        var bare = Path.Combine(parent, "hub.git");
        IProjectServices host = new LocalProjectAdapter(new ProjectServices(work));
        await host.OpenProjectAsync(work);
        var head = (await Git(work, "rev-parse", "HEAD")).Trim();
        Require((await host.ApplyBranchActionAsync(new("push", "feature", head))).Outcome == "done", "The GitHub branch must publish.");
        var running = """[{"status":"IN_PROGRESS","conclusion":""},{"status":"COMPLETED","conclusion":"SUCCESS"}]""";
        await File.WriteAllTextAsync(Path.Combine(shim, "details.json"), Details("OPEN", true, running, "BLOCKED"));
        var status = await host.GetBranchStatusAsync(false);
        Require(status?.Details is { Number: 7, State: "OPEN", Draft: true, Checks: "pending", SameRepository: true } details && details.MergeMethods.SequenceEqual(["squash", "rebase"]),
            "Review details: " + status);
        Require(WorkbenchWindow.ShipActions(status!).PullRequest == "ready", "A draft offers Ready for review.");
        Require((await host.ApplyBranchActionAsync(new("ready", "feature", head))).Outcome == "done" &&
            File.ReadAllText(Path.Combine(shim, "log")).Contains("pr ready 7", StringComparison.Ordinal), "Ready must call gh pr ready.");
        await File.WriteAllTextAsync(Path.Combine(shim, "details.json"), Details("OPEN", false, """[{"state":"FAILURE"}]""", "CLEAN"));
        Require((await host.GetBranchStatusAsync(false))?.Details?.Checks == "failing", "A failing status context fails the checks.");
        // A fork's same-named branch is not this branch's pull request.
        await File.WriteAllTextAsync(Path.Combine(shim, "details.json"), Details("OPEN", false, "[]", "CLEAN", owner: "fork"));
        try { await host.ApplyBranchActionAsync(new("merge", "feature", head, Method: "squash")); throw new InvalidOperationException("A fork's PR was merged."); }
        catch (InvalidOperationException error) when (error.Message.Contains("another repository", StringComparison.Ordinal)) { }
        await File.WriteAllTextAsync(Path.Combine(shim, "details.json"), Details("OPEN", false, "[]", "CLEAN"));
        try { await host.ApplyBranchActionAsync(new("merge", "feature", head, Method: "merge")); throw new InvalidOperationException("A disallowed method was used."); }
        catch (InvalidOperationException error) when (error.Message.Contains("merge method", StringComparison.Ordinal)) { }
        await File.WriteAllTextAsync(Path.Combine(shim, "merged.json"), Details("MERGED", false, "[]", "UNKNOWN"));
        var merged = await host.ApplyBranchActionAsync(new("merge", "feature", head, Method: "squash", DeleteBranch: true));
        Require(merged.Outcome == "done" && File.ReadAllText(Path.Combine(shim, "log")).Contains($"pr merge 7 --squash --match-head-commit {head}", StringComparison.Ordinal),
            "Merge must name the method and the head commit: " + merged);
        var remoteBranch = await Git(bare, "branch", "--list", "feature");
        Require(remoteBranch.Trim().Length == 0 && (await Git(work, "rev-parse", "--abbrev-ref", "HEAD")).Trim() == "feature",
            "Delete after merge removes only origin's branch through git, keeping the local checkout.");
        await File.WriteAllTextAsync(Path.Combine(shim, "details.json"), Details("CLOSED", false, "[]", "UNKNOWN"));
        Require((await host.ApplyBranchActionAsync(new("reopen", "feature", head))).Outcome == "done" &&
            File.ReadAllText(Path.Combine(shim, "log")).Contains("pr reopen 7", StringComparison.Ordinal), "Reopen must call gh pr reopen.");
        Console.WriteLine("PASS upstream #776: review details (draft, checks, merge methods, same repository) and Ready, Merge with branch deletion through git, Reopen");

        // The remote adapter returns what the local one does.
        await using var server = RemoteServer.Create(work, IPAddress.Loopback, 0, "branch-test");
        await server.StartAsync();
        var address = server.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
        using var remote = new RemoteProjectAdapter(new Uri(address), "branch-test");
        await remote.OpenProjectAsync(work);
        var local = await host.GetBranchStatusAsync(false);
        var over = await remote.GetBranchStatusAsync(false);
        Require(over is not null && local is not null && over with { Details = null } == local with { Details = null } &&
            over.Details! with { MergeMethods = [] } == local.Details! with { MergeMethods = [] } && over.Details.MergeMethods.SequenceEqual(local.Details.MergeMethods),
            $"Remote branch status differs: {over} vs {local}.");
        Require((await remote.ApplyBranchActionAsync(new("reopen", "feature", head))).Outcome == "done", "Remote branch actions must reach the host.");
        await server.StopAsync();
        Console.WriteLine("PASS branch status and actions over WebSocket RPC");
    }
}