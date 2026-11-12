using System.ComponentModel;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

using ProviderPullRequest = SharpRail.Plugins.Api.Host.PullRequest;

namespace SharpRail.Host.Core;

/// <summary>
/// The branch's remote state and its pull request's review facts and actions (ThinkRail #771 and #776). The host owns
/// the facts; every mutation is bound to the branch and commit the caller saw, and never force-pushes.
/// </summary>
public sealed partial class ProjectServices
{
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
        var route = hasOrigin ? RouteFor(origin) : null;
        var provider = !hasOrigin ? "" : route?.PluginId ?? "";
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
        var request = route is null ? null : await LookupAsync(currentRoot, branch, origin, route, fresh, cancellationToken);
        branchStatusObserved?.Invoke(new(currentRoot, currentRoot, route?.PluginId ?? "", branch, published, ahead, behind, head, request));
        return new(branch, hasOrigin, provider, published, ahead, behind, aheadOfBase, baseName == branch, baseName, head, ToWire(request));
    }

    private static async Task<bool> RefExistsAsync(string currentRoot, string reference, CancellationToken ct)
    {
        try { await GitRepository.RunAsync(currentRoot, ct, "rev-parse", "--verify", "-q", "--end-of-options", reference); return true; }
        catch (IOException) { return false; }
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
                    {
                        if (branch == baseName) throw new InvalidOperationException("The base branch has no pull request of its own.");
                        var target = new RepoHostBranch(currentRoot, origin, branch, baseName);
                        var route = RouteFor(origin);
                        var details = route is null ? null : await FindLiveAsync(route, target, cancellationToken);
                        if (route is null || details is null) throw new InvalidOperationException("GitHub reports no pull request for this branch.");
                        if (!details.SameRepository) throw new InvalidOperationException("The pull request for this branch name comes from another repository.");
                        var number = details.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        budget.CancelAfter(ProviderMutation);
                        switch (action.Kind)
                        {
                            case "ready":
                                if (details is not { State: "open", Draft: true }) throw new InvalidOperationException("The pull request is not an open draft.");
                                await route.Provider.ReadyAsync(target, details, budget.Token);
                                return new("done", $"PR #{number} is ready for review.");
                            case "reopen":
                                if (details.State != "closed") throw new InvalidOperationException("Only a closed pull request can be reopened.");
                                await route.Provider.ReopenAsync(target, details, budget.Token);
                                return new("done", $"PR #{number} is open again.");
                            case "merge":
                                if (details.State != "open") throw new InvalidOperationException("The pull request is not open.");
                                if (!details.MergeMethods.Contains(action.Method)) throw new InvalidOperationException("The repository does not allow that merge method.");
                                await route.Provider.MergeAsync(target, details, action.Method, head, budget.Token);
                                DropLookups(currentRoot, branch);
                                if (!action.DeleteBranch) return new("done", $"PR #{number} merged.");
                                // A queued merge keeps its branch: deletion waits until GitHub reports the merge.
                                if (await FindLiveAsync(route, target, cancellationToken) is not { State: "merged" })
                                    return new("done", $"PR #{number} is queued to merge; its branch stays until it lands.");
                                await DeleteRemoteBranchAsync(currentRoot, branch, cancellationToken);
                                return new("done", $"PR #{number} merged and origin/{branch} deleted.");
                            default:
                                if (details.State != "merged") throw new InvalidOperationException("The remote branch is deleted only after its pull request merged.");
                                await DeleteRemoteBranchAsync(currentRoot, branch, cancellationToken);
                                return new("done", $"Deleted origin/{branch}.");
                        }
                    }
                default: throw new ArgumentException("Unknown branch action " + action.Kind + ".");
            }
        }
        finally { DropLookups(currentRoot, branch); }
    }

    private static async Task<ProviderPullRequest?> FindLiveAsync(RepoHostRoute route, RepoHostBranch target, CancellationToken cancellationToken)
    {
        try { return await FindWithinBudgetAsync(route, target, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
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