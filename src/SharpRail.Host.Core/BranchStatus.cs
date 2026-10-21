using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// The branch's remote state and its pull request's review facts and actions (ThinkRail #771 and #776). The host owns
/// the facts; every mutation is bound to the branch and commit the caller saw, and never force-pushes.
/// </summary>
public sealed partial class ProjectServices
{
    private static readonly TimeSpan MergeMethodsTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<string> Methods, long At)> MergeMethodCache = new();

    public async ValueTask<BranchStatus?> GetBranchStatusAsync(bool fresh, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var branch = await CurrentBranchAsync(currentRoot, cancellationToken);
        if (branch is null || !GitRefs.IsSafe(branch)) return null;
        // An unborn branch has no commit to publish or review yet.
        if (!await RefExistsAsync(currentRoot, "HEAD", cancellationToken)) return null;
        var head = (await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "HEAD")).Trim();
        var origin = (await RemotesAsync(currentRoot, cancellationToken)).Contains("origin") ? await OriginUrlAsync(currentRoot, cancellationToken) : "";
        var hasOrigin = origin.Length > 0;
        var provider = !hasOrigin ? "" : IsGitHub(origin) ? "github" : IsGitLab(origin) ? "gitlab" : "other";
        if (hasOrigin && fresh)
        {
            // Behind is only trustworthy against a ref that was just fetched; an unpublished branch has nothing to fetch.
            try
            {
                await ChildProcess.RunAsync("git", currentRoot, ["fetch", "--quiet", "--no-tags", "--end-of-options", "origin", "+refs/heads/" + branch + ":refs/remotes/origin/" + branch],
                    TimeSpan.FromSeconds(20), cancellationToken, GitPromptFree);
            }
            catch (Exception error) when (error is TimeoutException or IOException or Win32Exception) { }
        }
        var published = hasOrigin && await RefExistsAsync(currentRoot, "refs/remotes/origin/" + branch, cancellationToken);
        var ahead = published ? await CountAsync(currentRoot, $"origin/{branch}..HEAD") : -1;
        var behind = published ? await CountAsync(currentRoot, $"HEAD..origin/{branch}") : -1;
        var baseRef = "";
        try { baseRef = (await ListBranchesAsync(false, cancellationToken)).DefaultBase; }
        catch (IOException) { }
        var baseName = baseRef.StartsWith("origin/", StringComparison.Ordinal) ? baseRef[7..] : baseRef;
        var aheadOfBase = baseRef.Length > 0 && baseName != branch ? await CountAsync(currentRoot, $"{baseRef}..HEAD") : 0;
        var review = provider == "github" ? await GetOpenReviewAsync(fresh, cancellationToken) : null;
        var details = provider == "github" ? await ReviewDetailsAsync(currentRoot, branch, origin, cancellationToken) : null;
        return new(branch, hasOrigin, provider, published, ahead, behind, aheadOfBase, baseName == branch, baseName, head, review, details);
    }

    private static bool IsGitLab(string origin) => origin.StartsWith("git@gitlab.com:", StringComparison.Ordinal) ||
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> RefExistsAsync(string currentRoot, string reference, CancellationToken ct)
    {
        try { await GitRepository.RunAsync(currentRoot, ct, "rev-parse", "--verify", "-q", "--end-of-options", reference); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>The newest pull request heading this branch in any state, with its checks, review and merge facts; null when gh cannot say.</summary>
    private static async Task<ReviewDetails?> ReviewDetailsAsync(string currentRoot, string branch, string origin, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("SHARPRAIL_GH_OFFLINE") == "1") return null;
        try
        {
            var result = await ChildProcess.RunAsync("gh", currentRoot, ["pr", "list", "--head", branch, "--state", "all", "--limit", "1", "--json",
                "number,url,state,isDraft,statusCheckRollup,reviewDecision,mergeStateStatus,headRepositoryOwner,headRepository"], GhQuery, ct, GhEnvironment);
            if (result.ExitCode != 0) return null;
            using var json = JsonDocument.Parse(result.Output);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0) return null;
            var pr = json.RootElement[0];
            var url = Text(pr, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            var owner = pr.TryGetProperty("headRepositoryOwner", out var ownerElement) ? Text(ownerElement, "login") : "";
            var name = pr.TryGetProperty("headRepository", out var repositoryElement) ? Text(repositoryElement, "name") : "";
            // gh matches --head by branch name only, so a fork's same-named branch must not pass as this one.
            var same = string.Equals(owner + "/" + name, GitHubRepository(origin), StringComparison.OrdinalIgnoreCase);
            return new(pr.GetProperty("number").GetInt32(), url, Text(pr, "state"), pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                ChecksState(pr), Text(pr, "reviewDecision"), Text(pr, "mergeStateStatus"), await MergeMethodsAsync(currentRoot, origin, ct), same);
        }
        catch (Exception error) when (error is TimeoutException or IOException or Win32Exception or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>failing when any check failed, else pending while any runs, else passing when there are checks.</summary>
    internal static string ChecksState(JsonElement pr)
    {
        if (!pr.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind != JsonValueKind.Array || rollup.GetArrayLength() == 0) return "";
        var pending = false;
        foreach (var check in rollup.EnumerateArray())
        {
            var conclusion = Text(check, "conclusion");
            var status = Text(check, "status");
            var state = Text(check, "state");
            if (conclusion is "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" || state is "FAILURE" or "ERROR") return "failing";
            if (status.Length > 0 && status != "COMPLETED" || state is "PENDING" or "EXPECTED") pending = true;
        }
        return pending ? "pending" : "passing";
    }

    private static async Task<IReadOnlyList<string>> MergeMethodsAsync(string currentRoot, string origin, CancellationToken ct)
    {
        if (MergeMethodCache.TryGetValue(origin, out var cached) && Environment.TickCount64 - cached.At < MergeMethodsTtl.TotalMilliseconds) return cached.Methods;
        var result = await ChildProcess.RunAsync("gh", currentRoot, ["repo", "view", "--json", "mergeCommitAllowed,squashMergeAllowed,rebaseMergeAllowed"], GhQuery, ct, GhEnvironment);
        if (result.ExitCode != 0) return [];
        using var json = JsonDocument.Parse(result.Output);
        bool Allowed(string name) => json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        IReadOnlyList<string> methods = [.. new[] { ("merge", "mergeCommitAllowed"), ("squash", "squashMergeAllowed"), ("rebase", "rebaseMergeAllowed") }
            .Where(pair => Allowed(pair.Item2)).Select(pair => pair.Item1)];
        MergeMethodCache[origin] = (methods, Environment.TickCount64);
        return methods;
    }

    public async ValueTask<BranchActionResult> ApplyBranchActionAsync(BranchAction action, CancellationToken cancellationToken = default)
    {
        var currentRoot = root;
        var branch = await CurrentBranchAsync(currentRoot, cancellationToken);
        if (branch is null || branch != action.Branch || !GitRefs.IsSafe(branch) || branch.StartsWith('-'))
            throw new InvalidOperationException("The workspace is no longer on the branch this action was shown for.");
        var head = (await GitRepository.RunAsync(currentRoot, cancellationToken, "rev-parse", "HEAD")).Trim();
        if (head != action.Head) throw new InvalidOperationException("The branch moved since this action was shown; look again before acting.");
        if (!(await RemotesAsync(currentRoot, cancellationToken)).Contains("origin")) throw new InvalidOperationException("This branch has no origin remote.");
        var origin = await OriginUrlAsync(currentRoot, cancellationToken);
        string baseName = "";
        try { var baseRef = (await ListBranchesAsync(false, cancellationToken)).DefaultBase; baseName = baseRef.StartsWith("origin/", StringComparison.Ordinal) ? baseRef[7..] : baseRef; }
        catch (IOException) { }
        try
        {
            switch (action.Kind)
            {
                case "push":
                    if (branch == baseName && !action.ConfirmBase) return new("confirmBase", $"This pushes {branch}, the branch other workspaces are based on.");
                    if (await PushAsync(currentRoot, branch, cancellationToken) is not null) return new("authFailed", "git push could not authenticate.");
                    return new("done", $"Pushed {branch} to origin.");
                case "ready" or "merge" or "reopen" or "delete-remote":
                    if (branch == baseName) throw new InvalidOperationException("The base branch has no pull request of its own.");
                    var details = await ReviewDetailsAsync(currentRoot, branch, origin, cancellationToken)
                        ?? throw new InvalidOperationException("GitHub reports no pull request for this branch.");
                    if (!details.SameRepository) throw new InvalidOperationException("The pull request for this branch name comes from another repository.");
                    var number = details.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    switch (action.Kind)
                    {
                        case "ready":
                            if (details is not { State: "OPEN", Draft: true }) throw new InvalidOperationException("The pull request is not an open draft.");
                            await GhMutationAsync(currentRoot, ["pr", "ready", number], cancellationToken);
                            return new("done", $"PR #{number} is ready for review.");
                        case "reopen":
                            if (details.State != "CLOSED") throw new InvalidOperationException("Only a closed pull request can be reopened.");
                            await GhMutationAsync(currentRoot, ["pr", "reopen", number], cancellationToken);
                            return new("done", $"PR #{number} is open again.");
                        case "merge":
                            if (details.State != "OPEN") throw new InvalidOperationException("The pull request is not open.");
                            if (!details.MergeMethods.Contains(action.Method)) throw new InvalidOperationException("The repository does not allow that merge method.");
                            await GhMutationAsync(currentRoot, ["pr", "merge", number, "--" + action.Method, "--match-head-commit", head], cancellationToken);
                            DropLookups(currentRoot, branch);
                            if (!action.DeleteBranch) return new("done", $"PR #{number} merged.");
                            // A queued merge keeps its branch: deletion waits until GitHub reports the merge.
                            if (await ReviewDetailsAsync(currentRoot, branch, origin, cancellationToken) is not { State: "MERGED" })
                                return new("done", $"PR #{number} is queued to merge; its branch stays until it lands.");
                            await DeleteRemoteBranchAsync(currentRoot, branch, cancellationToken);
                            return new("done", $"PR #{number} merged and origin/{branch} deleted.");
                        default:
                            if (details.State != "MERGED") throw new InvalidOperationException("The remote branch is deleted only after its pull request merged.");
                            await DeleteRemoteBranchAsync(currentRoot, branch, cancellationToken);
                            return new("done", $"Deleted origin/{branch}.");
                    }
                default: throw new ArgumentException("Unknown branch action " + action.Kind + ".");
            }
        }
        finally { DropLookups(currentRoot, branch); }
    }

    /// <summary>Deletes origin's branch through git, leased to the tip last fetched, so a newer push there is never lost.</summary>
    private static async Task DeleteRemoteBranchAsync(string currentRoot, string branch, CancellationToken ct)
    {
        var tip = (await GitRepository.RunAsync(currentRoot, ct, "rev-parse", "--verify", "-q", "--end-of-options", "refs/remotes/origin/" + branch)).Trim();
        var result = await ChildProcess.RunAsync("git", currentRoot, ["push", "origin", $"--force-with-lease=refs/heads/{branch}:{tip}", "--delete", "refs/heads/" + branch],
            PushBudget, ct, GitPromptFree);
        if (result.ExitCode != 0) throw new IOException("Deleting the remote branch failed: " + result.Error.Trim());
    }
}