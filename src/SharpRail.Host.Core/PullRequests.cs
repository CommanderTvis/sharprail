using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

using ProviderPullRequest = SharpRail.Plugins.Api.Host.PullRequest;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    private static readonly TimeSpan LookupTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GhQuery = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProviderMutation = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PushBudget = TimeSpan.FromSeconds(55);

    private sealed class Lookup(Task<(ProviderPullRequest? Review, bool Ok)> task)
    {
        internal Task<(ProviderPullRequest? Review, bool Ok)> Task { get; } = task;
        internal long CompletedAt;
    }

    private static readonly ConcurrentDictionary<string, Lookup> Lookups = new();

    /// <summary>The provider that owns a remote (H24): the seam's choice, else none (a plain remote).</summary>
    private RepoHostRoute? RouteFor(string origin) => origin.Length == 0 ? null : repoHosts?.Invoke(origin);

    /// <summary>The branch's request from the owning provider, cached and shared between concurrent callers; failures are never cached.</summary>
    private async Task<ProviderPullRequest?> LookupAsync(string currentRoot, string branch, string origin, RepoHostRoute route, bool fresh, CancellationToken cancellationToken)
    {
        var key = currentRoot + "\0" + branch;
        Lookup entry;
        lock (Lookups)
        {
            var now = Environment.TickCount64;
            if (Lookups.TryGetValue(key, out var existing) &&
                (!existing.Task.IsCompleted || !fresh && existing.CompletedAt != 0 && now - existing.CompletedAt < LookupTtl.TotalMilliseconds))
                entry = existing;
            else
            {
                entry = new Lookup(Task.Run(() => LookupFromProviderAsync(currentRoot, branch, origin, route, fresh)));
                Lookups[key] = entry;
                var created = entry;
                _ = created.Task.ContinueWith(task =>
                {
                    lock (Lookups)
                    {
                        if (task.IsCompletedSuccessfully && task.Result.Ok) created.CompletedAt = Environment.TickCount64;
                        else Lookups.TryRemove(new KeyValuePair<string, Lookup>(key, created));
                    }
                }, TaskScheduler.Default);
            }
        }
        var (review, _) = await entry.Task.WaitAsync(cancellationToken);
        return review;
    }

    private static void DropLookups(string currentRoot, string branch) => Lookups.TryRemove(currentRoot + "\0" + branch, out _);

    private static async Task<(ProviderPullRequest? Review, bool Ok)> LookupFromProviderAsync(string currentRoot, string branch, string origin, RepoHostRoute route, bool fresh)
    {
        try
        {
            var found = await FindWithinBudgetAsync(route, new(currentRoot, origin, branch, ""), CancellationToken.None);
            if (found is null) return (null, true);
            if (found.State != "open") return (found, true);
            var unpushed = await CountAsync(currentRoot, $"origin/{branch}..HEAD");
            // Behind is only trustworthy against a ref that was just fetched.
            var behind = -1;
            if (fresh)
            {
                try
                {
                    var fetch = await ChildProcess.RunAsync("git", currentRoot, ["fetch", "--quiet", "--no-tags", "--end-of-options", "origin", branch],
                        TimeSpan.FromSeconds(20), CancellationToken.None, GitPromptFree);
                    if (fetch.ExitCode == 0) behind = await CountAsync(currentRoot, $"HEAD..origin/{branch}");
                }
                catch (Exception error) when (error is TimeoutException or IOException or Win32Exception) { }
            }
            return (found with { Unpushed = unpushed, Behind = behind }, true);
        }
        catch (Exception)
        {
            // A provider that throws or times out reads as no review.
            return (null, false);
        }
    }

    private static async Task<ProviderPullRequest?> FindWithinBudgetAsync(RepoHostRoute route, RepoHostBranch target, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(GhQuery);
        return await route.Provider.FindAsync(target, budget.Token);
    }

    private static Abstractions.PullRequest? ToWire(ProviderPullRequest? request) => request is null ? null : new(request.Number, request.Title, request.Url, request.State, request.Draft,
        request.Checks, request.Review, request.Mergeability, request.MergeMethods, request.SameRepository, request.Unpushed, request.Behind);

    private static async Task<int> CountAsync(string currentRoot, string range)
    {
        try { return int.Parse((await GitRepository.RunAsync(currentRoot, CancellationToken.None, "rev-list", "--count", range)).Trim()); }
        catch (IOException) { return -1; }
    }

    private static readonly Dictionary<string, string?> GitPromptFree = new() { ["GIT_TERMINAL_PROMPT"] = "0" };

    public async ValueTask<PrDraft> PreviewPrAsync(CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var branch = await CurrentBranchAsync(currentRoot, cancellationToken) ?? "";
        var body = "";
        try
        {
            var baseRef = (await ListBranchesAsync(false, cancellationToken)).DefaultBase;
            var subjects = (await GitRepository.RunAsync(currentRoot, cancellationToken, "log", "--format=- %s", "--max-count=20", "--end-of-options", $"{baseRef}..HEAD")).Trim();
            body = subjects;
        }
        catch (IOException) { }
        return new(branch, body);
    }

    public async ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var branch = await CurrentBranchAsync(currentRoot, cancellationToken) ?? throw new InvalidOperationException("A detached HEAD has no branch to open a pull request for.");
        if (branch.StartsWith('-')) throw new ArgumentException("Refusing a branch name shaped like an option.");
        if (!GitRefs.IsSafe(branch)) throw new ArgumentException("The current branch name is not a valid ref.");
        if (!(await RemotesAsync(currentRoot, cancellationToken)).Contains("origin"))
            throw new InvalidOperationException("Pull requests are opened against the origin remote.");
        var baseRef = (await ListBranchesAsync(false, cancellationToken)).DefaultBase;
        if (!baseRef.StartsWith("origin/", StringComparison.Ordinal))
            throw new InvalidOperationException("The comparison base is not on origin; refusing to push for a pull request.");
        var baseName = baseRef[7..];
        if (branch == baseName) throw new InvalidOperationException("Refusing to push the default branch; create a branch first.");
        var dirty = (await GitRepository.RunAsync(currentRoot, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Count(entry => entry.Length > 3 && entry[2] == ' ');
        if (await CurrentBranchAsync(currentRoot, cancellationToken) != branch)
            throw new InvalidOperationException("The workspace switched branches while the pull request was being prepared.");

        var pushed = await PushAsync(currentRoot, branch, cancellationToken);
        DropLookups(currentRoot, branch);
        if (pushed is not null) return new("authFailed", "", 0, dirty, "");
        var origin = await OriginUrlAsync(currentRoot, cancellationToken);
        if (RouteFor(origin) is not { } route) return new("pushed", "", 0, dirty, "");
        var target = new RepoHostBranch(currentRoot, origin, branch, baseName);
        var composed = new RepoHostRequest(request.Title, request.TitleEdited, request.Body, request.Draft);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ProviderMutation);
            var outcome = await route.Provider.CreateOrUpdateAsync(target, composed, budget.Token);
            if (outcome.Action is "created" or "updated") return new(outcome.Action, outcome.Url, outcome.Number, dirty, "");
            return new("compare", route.Provider.CompareUrl(target, composed), 0, dirty, outcome.Problem);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new("compare", route.Provider.CompareUrl(target, composed), 0, dirty, "");
        }
        finally { DropLookups(currentRoot, branch); }
    }

    /// <summary>Pushes the branch with prompts disabled; returns null on success and the stderr when authentication failed.</summary>
    private static async Task<string?> PushAsync(string currentRoot, string branch, CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_MESSAGES"] = "C" };
        // LC_ALL would override LC_MESSAGES, so its character-set meaning moves to LC_CTYPE.
        if (Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all)
        {
            env["LC_ALL"] = null;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_CTYPE"))) env["LC_CTYPE"] = all;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH")) &&
            string.IsNullOrWhiteSpace(await ConfigAsync(currentRoot, "core.sshCommand", ct)))
            env["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes";
        ChildProcess.Result result;
        try { result = await ChildProcess.RunAsync("git", currentRoot, ["push", "--set-upstream", "origin", "--end-of-options", branch], PushBudget, ct, env); }
        catch (TimeoutException) { throw new IOException("git push timed out; check the network and that your SSH keys are loaded."); }
        if (result.ExitCode == 0) return null;
        var text = result.Error;
        if (new[] { "publickey", "Username", "Authentication failed", "terminal prompts disabled", "Host key verification failed" }.Any(text.Contains))
            return text;
        throw new IOException("git push failed: " + text.Trim());
    }

    private static async Task<string> ConfigAsync(string currentRoot, string key, CancellationToken ct)
    {
        try { return await GitRepository.RunAsync(currentRoot, ct, "config", "--get", key); }
        catch (IOException) { return ""; }
    }

    private static async Task<string?> CurrentBranchAsync(string currentRoot, CancellationToken ct)
    {
        try
        {
            var branch = (await GitRepository.RunAsync(currentRoot, ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim();
            return branch.Length == 0 ? null : branch;
        }
        catch (IOException) { return null; }
    }

    private static async Task<string> OriginUrlAsync(string currentRoot, CancellationToken ct)
    {
        try { return (await GitRepository.RunAsync(currentRoot, ct, "remote", "get-url", "origin")).Trim(); }
        catch (IOException) { return ""; }
    }
}