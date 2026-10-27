using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.GitHub.UI;

/// <summary>The pull request flows both the strip item and the Review section start: compose and open, merge, delete the remote branch.</summary>
internal static class GitHubPrFlow
{
    /// <summary>The last composed request per surface, so Try again resubmits the edited text, never a regenerated draft.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRepoHostSurface, State> States = new();

    private sealed class State
    {
        public bool Busy;
        public RepoPrRequest? LastSubmit;
        public RepoOpenReview? Optimistic;
        public Action? Changed;
    }

    internal static bool IsBusy(IRepoHostSurface surface) => States.GetOrCreateValue(surface).Busy;

    internal static RepoPrRequest? LastSubmit(IRepoHostSurface surface) => States.GetOrCreateValue(surface).LastSubmit;

    /// <summary>The review the section shows: the answer of a submit not yet confirmed by a status, else the status' own.</summary>
    internal static RepoOpenReview? ReviewOf(IRepoHostSurface surface)
    {
        var state = States.GetOrCreateValue(surface);
        return state.Optimistic ?? surface.Status?.Review;
    }

    /// <summary>A status arrived: the answer of a submit made before it no longer stands.</summary>
    internal static void StatusArrived(IRepoHostSurface surface)
    {
        var state = States.GetOrCreateValue(surface);
        if (!state.Busy) state.Optimistic = null;
    }

    internal static void OnChanged(IRepoHostSurface surface, Action changed) => States.GetOrCreateValue(surface).Changed = changed;

    private static void SetBusy(IRepoHostSurface surface, bool busy)
    {
        var state = States.GetOrCreateValue(surface);
        state.Busy = busy;
        state.Changed?.Invoke();
    }

    private enum Outcome { Done, Failed, Setup }

    internal static async Task OpenAsync(IRepoHostSurface surface, bool draft)
    {
        var state = States.GetOrCreateValue(surface);
        if (state.Busy || surface.Status is not { } start) return;
        var key = start.Branch;
        // An open pull request is updated through the same dialog: its description has no other source here than
        // the regenerated commit list, which must not replace a hand-written one unseen.
        var updating = ReviewOf(surface) is not null;
        var seed = state.LastSubmit is { } last && last.Draft == draft ? last : null;
        if (seed is null)
        {
            SetBusy(surface, true);
            try
            {
                var preview = await surface.PreviewAsync(CancellationToken.None);
                seed = new(preview.Title, false, preview.Body, draft);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { surface.Toast("error", "Couldn't prepare the PR", error.Message); return; }
            finally { SetBusy(surface, false); }
            if (key != surface.Status?.Branch) return;
        }
        GitHubSetup? setup = null;
        var submitted = await GitHubDialogs.ComposeAsync(surface.Owner, new(seed.Title, seed.Body), seed.TitleEdited, draft, updating, async request =>
        {
            (var outcome, setup) = await SubmitAsync(surface, request, key);
            return outcome != Outcome.Failed;
        });
        if (!submitted) { if (key == surface.Status?.Branch) state.LastSubmit = null; return; }
        while (setup is not null && key == surface.Status?.Branch && state.LastSubmit is { } retry)
        {
            var choice = await GitHubDialogs.SetupAsync(surface.Owner, setup, surface.IsRemote ? null : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux");
            if (choice == GitHubSetupChoice.Compare) { surface.OpenLink(setup.CompareUrl); state.LastSubmit = null; return; }
            if (choice != GitHubSetupChoice.Retry) return;
            (_, setup) = await SubmitAsync(surface, retry, key);
        }
    }

    private static async Task<(Outcome Outcome, GitHubSetup? Setup)> SubmitAsync(IRepoHostSurface surface, RepoPrRequest request, string key)
    {
        var state = States.GetOrCreateValue(surface);
        state.LastSubmit = request;
        SetBusy(surface, true);
        try
        {
            var result = await surface.OpenPrAsync(request, CancellationToken.None);
            if (key != surface.Status?.Branch) return (Outcome.Done, null);
            // Nothing is unpushed right after the push; how far origin is ahead is only known to a fetch.
            if (result.Number > 0) { state.Optimistic = new(result.Number, result.Url, 0, -1); }
            surface.RefreshStatus();
            if (result.DirtyFiles > 0)
                surface.Toast("info", "Uncommitted changes", $"{result.DirtyFiles} uncommitted {(result.DirtyFiles == 1 ? "file" : "files")} stayed local.");
            if (result.Action == "authFailed") return (Outcome.Setup, new("pushAuth"));
            if (result.Action == "compare" && result.Problem.Length > 0) return (Outcome.Setup, new(result.Problem, result.Url));
            state.LastSubmit = null;
            if (result.Action == "compare" && result.Url.Length > 0) surface.OpenLink(result.Url);
            var pr = result.Number > 0 ? "PR #" + result.Number : null;
            var (title, message) = result.Action switch
            {
                "created" => ("PR opened", (pr ?? "The PR") + " is open."),
                "updated" => ("PR updated", $"Pushed new commits to {pr ?? "the open PR"} and refreshed its description."),
                "compare" => ("Branch pushed", "GitHub's compare page opened with your description prefilled — finish creating the PR there."),
                _ => ("Branch pushed", "The branch is on origin.")
            };
            surface.Toast("success", title, message);
            return (Outcome.Done, null);
        }
        catch (OperationCanceledException) { return (Outcome.Failed, null); }
        catch (Exception error)
        {
            surface.Toast("error", "Open PR failed", error.Message);
            return (Outcome.Failed, null);
        }
        finally { SetBusy(surface, false); }
    }

    internal static async Task MergeAsync(IRepoHostSurface surface, RepoBranchStatus status)
    {
        if (status.Details is not { } details) return;
        var remembered = surface.MergePreference?.Split('|');
        var choice = await GitHubDialogs.MergeAsync(surface.Owner, details.Number, details.MergeMethods, remembered?[0] ?? details.MergeMethods[0], remembered is [_, "delete"]);
        if (choice is not { } chosen) return;
        surface.MergePreference = chosen.Method + "|" + (chosen.DeleteBranch ? "delete" : "keep");
        await surface.ApplyAsync(new("merge", status.Branch, status.Head, chosen.Method, chosen.DeleteBranch), "Merge failed");
    }

    internal static async Task DeleteRemoteBranchAsync(IRepoHostSurface surface, RepoBranchStatus status)
    {
        if (!await surface.ConfirmAsync($"Delete origin/{status.Branch}?",
            "The pull request merged. This deletes the branch on origin only; your local branch and workspace stay.", "Delete remote branch", "PullRequestDeleteConfirm")) return;
        await surface.ApplyAsync(new("delete-remote", status.Branch, status.Head), "Delete failed");
    }
}