using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.GitHub.UI;

/// <summary>The Review panel's pull request section (W28): the open request's chip, Open PR or Push updates, and the diverged or unpushed notice.</summary>
internal sealed class GitHubReviewSection : StackPanel
{
    private readonly IRepoHostSurface surface;

    public GitHubReviewSection(IRepoHostSurface surface)
    {
        this.surface = surface;
        Name = "ReviewPullRequest";
        Spacing = 8;
        AttachedToVisualTree += (_, _) => { surface.Changed += StatusChanged; GitHubPrFlow.OnChanged(surface, Render); Render(); };
        DetachedFromVisualTree += (_, _) => { surface.Changed -= StatusChanged; GitHubPrFlow.OnChanged(surface, () => { }); };
    }

    private void StatusChanged()
    {
        GitHubPrFlow.StatusArrived(surface);
        Render();
    }

    private void Render()
    {
        Children.Clear();
        var status = surface.Status;
        IsVisible = status is { Provider: GitHubUI.Provider };
        if (status is null || !IsVisible) return;
        var busy = GitHubPrFlow.IsBusy(surface);
        var review = GitHubPrFlow.ReviewOf(surface);
        var diverged = review is { Behind: > 0 };
        var unpushed = review is { Unpushed: > 0 } ? review.Unpushed : 0;
        var integrate = Inert(status.Branch) ? "git pull --rebase origin " + status.Branch : null;
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
            chip.Click += (_, _) => surface.OpenLink(review.Url);
            row.Children.Add(chip);
        }
        var label = busy ? "Pushing…" : review is null ? "Open PR" : diverged ? "Branch diverged" : unpushed > 0 ? $"Push updates ({unpushed})" : "Push updates";
        var primary = Ui.Button(label, () =>
        {
            if (diverged) _ = surface.CopyCommandAsync(integrate!);
            else _ = GitHubPrFlow.OpenAsync(surface, false);
        }, "gitBranch");
        primary.Name = "ReviewOpenPr";
        primary.IsEnabled = !busy && (!diverged || integrate is not null);
        ToolTip.SetTip(primary, review is null ? "Push the branch and open a pull request"
            : !diverged ? "Push new commits to the open pull request"
            : integrate is not null ? "The branch and origin diverged — integrate the remote changes first. Copy: " + integrate
            : $"The branch and origin diverged — integrate origin/{status.Branch} in a terminal first.");
        if (unpushed > 0 || diverged)
        {
            primary.Background = Ui.PrimaryFill; primary.BorderThickness = new Thickness(0);
            foreach (var child in ((StackPanel)primary.Content!).Children)
                if (child is TextBlock text) text.Foreground = Ui.OnPrimary; else ((Border)child).Background = Ui.OnPrimary;
        }
        row.Children.Add(primary);
        if (review is null)
        {
            var draft = Ui.Button("Open draft PR", () => _ = GitHubPrFlow.OpenAsync(surface, true));
            draft.Name = "ReviewOpenDraftPr"; draft.IsEnabled = !busy;
            row.Children.Add(draft);
        }
        Children.Add(row);
        if (diverged)
        {
            var notice = Ui.Text($"PR #{review!.Number}'s branch and origin diverged — origin has commits you don't have. Integrate them before pushing " +
                "(a plain push can't land; force-pushing would drop them). " + (integrate is not null ? "Run in a terminal:"
                    : $"Integrate origin/{status.Branch} in a terminal — its name has shell-special characters, so no command is offered."), Ui.Warning, 12);
            notice.Name = "ReviewPrNotice"; notice.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            Children.Add(notice);
            if (integrate is not null)
            {
                var command = Ui.Text(integrate, Ui.TextBrush, 12);
                command.Name = "ReviewIntegrateCommand"; command.FontFamily = Ui.CodeFont;
                Children.Add(command);
            }
        }
        else if (unpushed > 0)
        {
            var notice = Ui.Text((unpushed == 1 ? "1 new commit isn't" : $"{unpushed} new commits aren't") + $" in PR #{review!.Number} yet.", Ui.Muted, 12);
            notice.Name = "ReviewPrNotice"; notice.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            Children.Add(notice);
        }
    }

    private static bool Inert(string branch) => System.Text.RegularExpressions.Regex.IsMatch(branch, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$");
}