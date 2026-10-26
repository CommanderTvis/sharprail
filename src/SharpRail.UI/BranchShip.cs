using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// The header's Remote and Pull request segments (ThinkRail #771 and #776): always present for a mounted workspace's
/// branch, each turning the host's branch status into the branch's next step. The host owns the facts and every
/// mutation; this only derives the actions and runs them, binding each to the branch and commit it showed.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private BranchStatus? branchStatus;
    private string branchStatusKey = "";
    private long branchStatusLookup;
    private long branchStatusAt;
    private bool shipBusy;
    private DispatcherTimer? checksPoll;

    /// <summary>The branch status the header shows; checks read it.</summary>
    public BranchStatus? BranchStatus => branchStatus;

    /// <summary>The next step each segment derives from a status, as action names; one pure rule for the header and checks.</summary>
    internal static (string Remote, string PullRequest) ShipActions(BranchStatus status)
    {
        var remote = !status.HasOrigin ? "" : !status.Published ? "publish" : status.Behind > 0 ? "sync" : status.Ahead > 0 ? "push" : "";
        var pr = "";
        if (status.Provider == "github" && !status.OnBase)
        {
            var details = status.Details;
            if (details is null || details.State is not ("OPEN" or "MERGED" or "CLOSED")) pr = status.Review is null ? "open" : "";
            else if (details.State == "MERGED") pr = status.Published ? "delete-remote" : "";
            else if (details.State == "CLOSED") pr = "reopen";
            // While the branch and origin differ, the next step stays with Remote's Push or Sync.
            else if (remote.Length > 0) pr = "";
            else if (details.Draft) pr = "ready";
            else if (details.Checks != "failing" && details.MergeState is "CLEAN" or "UNSTABLE" or "HAS_HOOKS" && details.MergeMethods.Count > 0) pr = "merge";
        }
        return (remote, pr);
    }

    private void RequestBranchStatus(bool fresh, bool force = false)
    {
        var key = WorkspaceMounted && !atHome && git.IsRepository && git.Branch.Length > 0 ? workspaceRoot + "\0" + git.Branch : "";
        if (key != branchStatusKey) { branchStatusKey = key; branchStatus = null; branchStatusAt = 0; UpdateShipSegments(); force = true; }
        if (key.Length == 0) return;
        // A watcher refresh asks at most every 15 seconds: each read may call gh.
        if (!force && !fresh && Environment.TickCount64 - branchStatusAt < 15_000) return;
        branchStatusAt = Environment.TickCount64;
        _ = LookupBranchStatusAsync(key, fresh);
    }

    private async Task LookupBranchStatusAsync(string key, bool fresh)
    {
        var lookup = ++branchStatusLookup;
        BranchStatus? status;
        try { status = await Task.Run(async () => await host.GetBranchStatusAsync(fresh, lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { Console.Error.WriteLine(error); status = null; }
        if (lookup != branchStatusLookup || key != branchStatusKey) return;
        branchStatus = status;
        UpdateShipSegments();
    }

    private void UpdateShipSegments()
    {
        var remoteSegment = this.FindControl<Border>("RemoteSegment")!;
        var prSegment = this.FindControl<Border>("PullRequestSegment")!;
        var remoteRow = this.FindControl<StackPanel>("RemoteRow")!;
        var prRow = this.FindControl<StackPanel>("PullRequestRow")!;
        remoteRow.Children.Clear(); prRow.Children.Clear();
        var status = branchStatus;
        remoteSegment.IsVisible = prSegment.IsVisible = status is not null;
        UpdateChecksPoll(status);
        if (status is null) return;
        var (remoteAction, prAction) = ShipActions(status);
        // Remote: its state, then its one action.
        if (!status.HasOrigin) remoteRow.Children.Add(ShipText("RemoteState", "No remote", Ui.Hint));
        else if (!status.Published) remoteRow.Children.Add(ShipText("RemoteState", "Not published", Ui.Muted));
        else if (status.Ahead <= 0 && status.Behind <= 0) remoteRow.Children.Add(ShipText("RemoteState", "In sync", Ui.Muted));
        else
        {
            if (status.Ahead > 0) remoteRow.Children.Add(ShipText("RemoteAhead", "↑" + status.Ahead, Ui.TextBrush));
            if (status.Behind > 0) remoteRow.Children.Add(ShipText("RemoteBehind", "↓" + status.Behind, Ui.Warning));
        }
        switch (remoteAction)
        {
            case "publish": remoteRow.Children.Add(ShipButton("RemotePublish", "Publish", () => _ = PushBranchAsync(status))); break;
            case "push": remoteRow.Children.Add(ShipButton("RemotePush", $"Push {status.Ahead}", () => _ = PushBranchAsync(status))); break;
            case "sync": remoteRow.Children.Add(ShipButton("RemoteSync", "Sync", () => _ = SyncBranchAsync(status))); break;
        }
        // Pull request: the review chip or its absence, then the next step.
        var details = status.Details;
        if (status.Provider != "github") prRow.Children.Add(ShipText("PullRequestState", status.Provider == "gitlab" ? "Open on GitLab" : "—", Ui.Hint));
        else if (details is null && status.Review is null) prRow.Children.Add(ShipText("PullRequestState", status.OnBase ? "—" : "No PR", Ui.Hint));
        else
        {
            var number = details?.Number ?? status.Review!.Number;
            var url = details?.Url ?? status.Review!.Url;
            var label = details switch
            {
                { State: "MERGED" } => "Merged #",
                { State: "CLOSED" } => "Closed #",
                { Draft: true } => "Draft #",
                _ => "PR #"
            } + number;
            var chip = ShipButton("PullRequestChip", label, () => OpenLink(url));
            ToolTip.SetTip(chip, url + (details is { ReviewDecision.Length: > 0 } ? " · " + details.ReviewDecision.Replace('_', ' ').ToLowerInvariant() : ""));
            prRow.Children.Add(chip);
            if (details is { State: "OPEN", Checks.Length: > 0 })
            {
                var (glyph, brush, tip) = details.Checks switch
                {
                    "failing" => ("✗", Ui.Danger, "Checks failing"),
                    "pending" => ("●", Ui.Warning, "Checks running"),
                    _ => ("✓", Ui.Success, "Checks passing")
                };
                var checks = ShipText("PullRequestChecks", glyph, brush);
                ToolTip.SetTip(checks, tip); AutomationProperties.SetName(checks, tip);
                prRow.Children.Add(checks);
            }
        }
        switch (prAction)
        {
            case "open":
                prRow.Children.Add(ShipButton("PullRequestOpen", "Open PR", () => { EnsureOpenReview(); _ = OpenPrFlowAsync(false); }));
                prRow.Children.Add(ShipButton("PullRequestOpenDraft", "Draft", () => { EnsureOpenReview(); _ = OpenPrFlowAsync(true); }));
                break;
            case "ready": prRow.Children.Add(ShipButton("PullRequestReady", "Ready for review", () => _ = BranchActionAsync(status, new("ready", status.Branch, status.Head)))); break;
            case "merge": prRow.Children.Add(ShipButton("PullRequestMerge", "Merge PR", () => _ = MergePrAsync(status))); break;
            case "reopen": prRow.Children.Add(ShipButton("PullRequestReopen", "Reopen", () => _ = BranchActionAsync(status, new("reopen", status.Branch, status.Head)))); break;
            case "delete-remote": prRow.Children.Add(ShipButton("PullRequestDeleteBranch", "Delete remote branch", () => _ = DeleteRemoteBranchAsync(status))); break;
        }
    }

    private static TextBlock ShipText(string name, string text, IBrush brush)
    {
        var block = Ui.Text(text, brush, 12);
        block.Name = name; block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }

    private Button ShipButton(string name, string label, Action action)
    {
        var button = new Button
        {
            Name = name,
            Content = Ui.Text(label, Ui.TextBrush, 12),
            Height = 20,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !shipBusy
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    // While checks run and the window is visible, the review is re-read every 20 seconds without a fetch.
    private void UpdateChecksPoll(BranchStatus? status)
    {
        var running = status?.Details is { State: "OPEN", Checks: "pending" };
        if (!running) { checksPoll?.Stop(); return; }
        checksPoll ??= CreateChecksPoll();
        if (!checksPoll.IsEnabled) checksPoll.Start();
    }

    private DispatcherTimer CreateChecksPoll()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => { if (IsVisible && WindowState != WindowState.Minimized) RequestBranchStatus(false, force: true); };
        return timer;
    }

    private async Task<BranchActionResult?> BranchActionAsync(BranchStatus status, BranchAction action, string? failure = null)
    {
        if (shipBusy) return null;
        var key = branchStatusKey;
        shipBusy = true; UpdateShipSegments();
        try
        {
            var result = await Task.Run(async () => await host.ApplyBranchActionAsync(action, lifetime.Token), lifetime.Token);
            if (result.Outcome == "authFailed") Toasts.Push(ToastVariant.Error, "git push could not authenticate; check your SSH keys or credentials.", "Push failed");
            else if (result.Outcome == "done") Toasts.Push(ToastVariant.Success, result.Message, "Done");
            return result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            Toasts.Push(ToastVariant.Error, error.Message, failure ?? "Couldn't update the branch");
            return null;
        }
        finally
        {
            shipBusy = false;
            if (key == branchStatusKey) { UpdateShipSegments(); RequestBranchStatus(true, force: true); }
            if (reviewKey.Length > 0) _ = LookupOpenReviewAsync(true);
        }
    }

    private async Task PushBranchAsync(BranchStatus status)
    {
        var result = await BranchActionAsync(status, new("push", status.Branch, status.Head), "Push failed");
        if (result?.Outcome != "confirmBase") return;
        // A branch that is its own base is shared: pushing it is asked first and names the branch.
        if (!await Dialogs.Confirm(this, $"Push {status.Branch}?", result.Message + " Push it to origin anyway?", "Push", "RemotePushConfirm")) return;
        await BranchActionAsync(status, new("push", status.Branch, status.Head, ConfirmBase: true), "Push failed");
    }

    /// <summary>Sync shows the shell-inert integration command to copy; it never force-pushes.</summary>
    private async Task SyncBranchAsync(BranchStatus status)
    {
        if (!ShellInert(status.Branch))
        {
            await Dialogs.Notice(this, "Integrate origin", $"Integrate origin/{status.Branch} in a terminal before pushing. Its name has shell-special characters, so no command is offered.");
            return;
        }
        var command = "git pull --rebase origin " + status.Branch;
        if (await Dialogs.Confirm(this, "Integrate origin", $"origin/{status.Branch} has {status.Behind} commit{(status.Behind == 1 ? "" : "s")} you don't have. " +
            $"Run this in a terminal, then push:\n{command}", "Copy command", "RemoteSyncCopy"))
            await CopyIntegrateAsync(command);
    }

    private async Task MergePrAsync(BranchStatus status)
    {
        if (status.Details is not { } details) return;
        var remembered = profile.Data.MergePreferences.GetValueOrDefault(projectRoot)?.Split('|');
        var choice = await PrDialogs.MergeAsync(this, details.Number, details.MergeMethods, remembered?[0] ?? details.MergeMethods[0], remembered is [_, "delete"]);
        if (choice is not { } chosen) return;
        profile.Data.MergePreferences[projectRoot] = chosen.Method + "|" + (chosen.DeleteBranch ? "delete" : "keep");
        SaveProfile();
        await BranchActionAsync(status, new("merge", status.Branch, status.Head, Method: chosen.Method, DeleteBranch: chosen.DeleteBranch), "Merge failed");
    }

    private async Task DeleteRemoteBranchAsync(BranchStatus status)
    {
        if (!await Dialogs.Confirm(this, $"Delete origin/{status.Branch}?",
            "The pull request merged. This deletes the branch on origin only; your local branch and workspace stay.", "Delete remote branch", "PullRequestDeleteConfirm")) return;
        await BranchActionAsync(status, new("delete-remote", status.Branch, status.Head), "Delete failed");
    }
}