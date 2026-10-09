using System.Diagnostics;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    // One pull request state per workspace and branch; a lookup that a newer one or a submit superseded is dropped.
    private string reviewKey = "";
    private OpenReview? openReview;
    private long reviewLookup;
    private bool prBusy;
    private PrRequest? lastPrSubmit;

    /// <summary>Opens an https link outside the app; checks replace it.</summary>
    public Action<string> OpenLink { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>The open pull request the Review panel shows for the mounted workspace's branch.</summary>
    public OpenReview? OpenReview => openReview;

    /// <summary>Asks the host again, past its cache and with a fetch, so counts and a closed pull request are current.</summary>
    public void RefreshOpenReview()
    {
        if (reviewKey.Length > 0) _ = LookupOpenReviewAsync(true);
    }

    private void RefreshReviewPanel()
    {
        toolContent.Remove("review");
        surface.RefreshContents("review");
    }

    // Asked for when the Review panel is first drawn for a branch, so startup never waits on gh.
    private void EnsureOpenReview()
    {
        var key = git.IsRepository && git.Branch.Length > 0 && WorkspaceMounted ? workspaceRoot + "\0" + git.Branch : "";
        if (key == reviewKey) return;
        reviewKey = key; openReview = null; lastPrSubmit = null; reviewLookup++;
        if (key.Length > 0) _ = LookupOpenReviewAsync(false);
    }

    private async Task LookupOpenReviewAsync(bool fresh)
    {
        var key = reviewKey;
        var lookup = ++reviewLookup;
        OpenReview? review;
        try { review = await Task.Run(async () => await host.GetOpenReviewAsync(fresh, lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { Console.Error.WriteLine(error); review = null; }
        if (lookup != reviewLookup || key != reviewKey || review == openReview) return;
        openReview = review;
        RefreshReviewPanel();
    }

    private static bool ShellInert(string branch) => InertBranch().IsMatch(branch);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex InertBranch();

    private Control? PullRequestSection()
    {
        EnsureOpenReview();
        if (reviewKey.Length == 0) return null;
        var section = new StackPanel { Name = "ReviewPullRequest", Spacing = 8 };
        var review = openReview;
        var diverged = review is { BehindCommits: > 0 };
        var unpushed = review is { UnpushedCommits: > 0 } ? review.UnpushedCommits : 0;
        var integrate = ShellInert(git.Branch) ? "git pull --rebase origin " + git.Branch : null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (review is not null)
        {
            var chip = new Button
            {
                Name = "ReviewPrChip",
                Content = Ui.Text("PR #" + review.Number, Ui.Accent, 12),
                Padding = new Thickness(8, 2),
                Background = Ui.PrimarySubtle,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(chip, review.Url);
            AutomationProperties.SetName(chip, "Open pull request #" + review.Number);
            chip.Click += (_, _) => OpenLink(review.Url);
            row.Children.Add(chip);
        }
        var label = prBusy ? "Pushing…" : review is null ? "Open PR" : diverged ? "Branch diverged" : unpushed > 0 ? $"Push updates ({unpushed})" : "Push updates";
        var primary = Ui.Button(label, () =>
        {
            if (diverged) _ = CopyIntegrateAsync(integrate!);
            else _ = OpenPrFlowAsync(false);
        }, "gitBranch");
        primary.Name = "ReviewOpenPr";
        primary.IsEnabled = !prBusy && (!diverged || integrate is not null);
        ToolTip.SetTip(primary, review is null ? "Push the branch and open a pull request"
            : !diverged ? "Push new commits to the open pull request"
            : integrate is not null ? "The branch and origin diverged — integrate the remote changes first. Copy: " + integrate
            : $"The branch and origin diverged — integrate origin/{git.Branch} in a terminal first.");
        if (unpushed > 0 || diverged)
        {
            primary.Background = Ui.PrimaryFill; primary.BorderThickness = new Thickness(0);
            foreach (var child in ((StackPanel)primary.Content!).Children)
                if (child is TextBlock text) text.Foreground = Ui.OnPrimary; else ((Border)child).Background = Ui.OnPrimary;
        }
        row.Children.Add(primary);
        if (review is null)
        {
            var draft = Ui.Button("Open draft PR", () => _ = OpenPrFlowAsync(true));
            draft.Name = "ReviewOpenDraftPr"; draft.IsEnabled = !prBusy;
            row.Children.Add(draft);
        }
        section.Children.Add(row);
        if (diverged)
        {
            var notice = Ui.Text($"PR #{review!.Number}'s branch and origin diverged — origin has commits you don't have. Integrate them before pushing " +
                "(a plain push can't land; force-pushing would drop them). " + (integrate is not null ? "Run in a terminal:"
                    : $"Integrate origin/{git.Branch} in a terminal — its name has shell-special characters, so no command is offered."), Ui.Warning, 12);
            notice.Name = "ReviewPrNotice"; notice.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(notice);
            if (integrate is not null)
            {
                var command = Ui.Text(integrate, Ui.TextBrush, 12);
                command.Name = "ReviewIntegrateCommand"; command.FontFamily = Ui.CodeFont;
                section.Children.Add(command);
            }
        }
        else if (unpushed > 0)
        {
            var notice = Ui.Text((unpushed == 1 ? "1 new commit isn't" : $"{unpushed} new commits aren't") + $" in PR #{review!.Number} yet.", Ui.Muted, 12);
            notice.Name = "ReviewPrNotice"; notice.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(notice);
        }
        return section;
    }

    private async Task CopyIntegrateAsync(string command)
    {
        try
        {
            if (Clipboard is null) throw new InvalidOperationException("No clipboard.");
            await Clipboard.SetTextAsync(command);
            Toasts.Push(ToastVariant.Success, "Run it in a terminal to integrate the remote changes, then push.", "Command copied");
        }
        catch (Exception) { Toasts.Push(ToastVariant.Error, "Couldn't write to the clipboard.", "Copy failed"); }
    }

    private void SetPrBusy(bool busy)
    {
        prBusy = busy;
        RefreshReviewPanel();
    }

    private enum PrOutcome { Done, Failed, Setup }

    private async Task OpenPrFlowAsync(bool draft)
    {
        if (prBusy) return;
        var key = reviewKey;
        // An open pull request is updated through the same dialog: its description has no other source here than
        // the regenerated commit list, which must not replace a hand-written one unseen.
        var updating = openReview is not null;
        var seed = lastPrSubmit is { } last && last.Draft == draft ? last : null;
        if (seed is null)
        {
            SetPrBusy(true);
            try
            {
                var preview = await Task.Run(async () => await host.PreviewPrAsync(lifetime.Token), lifetime.Token);
                seed = new(preview.Title, false, preview.Body, draft);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { Toasts.Push(ToastVariant.Error, error.Message, "Couldn't prepare the PR"); return; }
            finally { SetPrBusy(false); }
            if (key != reviewKey) return;
        }
        PrSetup? setup = null;
        var submitted = await PrDialogs.ComposeAsync(this, new(seed.Title, seed.Body), seed.TitleEdited, draft, updating, async request =>
        {
            (var outcome, setup) = await SubmitPrAsync(request, key);
            return outcome != PrOutcome.Failed;
        });
        if (!submitted) { if (key == reviewKey) lastPrSubmit = null; return; }
        // Try again resubmits the last edited title and description, never a regenerated draft.
        while (setup is not null && key == reviewKey && lastPrSubmit is { } retry)
        {
            var choice = await PrDialogs.SetupAsync(this, setup, remote ? null : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux");
            if (choice == PrSetupChoice.Compare) { OpenLink(setup.CompareUrl); lastPrSubmit = null; return; }
            if (choice != PrSetupChoice.Retry) return;
            (_, setup) = await SubmitPrAsync(retry, key);
        }
    }

    private async Task<(PrOutcome Outcome, PrSetup? Setup)> SubmitPrAsync(PrRequest request, string key)
    {
        lastPrSubmit = request;
        SetPrBusy(true);
        try
        {
            var result = await Task.Run(async () => await host.OpenPrAsync(request, lifetime.Token), lifetime.Token);
            if (key != reviewKey) return (PrOutcome.Done, null);
            // The answer of this submit outranks a lookup already running; the fresh read then settles the counts.
            // Nothing is unpushed right after the push; how far origin is ahead is only known to a fetch.
            if (result.Number > 0) { reviewLookup++; openReview = new(result.Number, result.Url, "github", 0, -1); }
            _ = LookupOpenReviewAsync(true);
            if (result.DirtyFiles > 0)
                Toasts.Push(ToastVariant.Info, $"{result.DirtyFiles} uncommitted {(result.DirtyFiles == 1 ? "file" : "files")} stayed local.", "Uncommitted changes");
            if (result.Action == "authFailed") return (PrOutcome.Setup, new("pushAuth"));
            if (result.Action == "compare" && result.GhProblem.Length > 0) return (PrOutcome.Setup, new(result.GhProblem, result.Url));
            lastPrSubmit = null;
            if (result.Action == "compare" && result.Url.Length > 0) OpenLink(result.Url);
            var pr = result.Number > 0 ? "PR #" + result.Number : null;
            var (title, message) = result.Action switch
            {
                "created" => ("PR opened", (pr ?? "The PR") + " is open."),
                "updated" => ("PR updated", $"Pushed new commits to {pr ?? "the open PR"} and refreshed its description."),
                "compare" => ("Branch pushed", "GitHub's compare page opened with your description prefilled — finish creating the PR there."),
                _ => ("Branch pushed", "The branch is on origin. The remote isn't GitHub — open the review on your forge.")
            };
            Toasts.Push(ToastVariant.Success, message, title);
            return (PrOutcome.Done, null);
        }
        catch (OperationCanceledException) { return (PrOutcome.Failed, null); }
        catch (Exception error)
        {
            Toasts.Push(ToastVariant.Error, error.Message, "Open PR failed");
            return (PrOutcome.Failed, null);
        }
        finally { SetPrBusy(false); }
    }
}