using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.GitHub.UI;

/// <summary>
/// The strip's Pull request item (W28): the review chip or its absence in the same icon slot as the other location
/// fields, then the branch's one next step. Hidden unless GitHub owns the remote.
/// </summary>
internal sealed class GitHubLocationItem : StackPanel
{
    private readonly IRepoHostSurface surface;
    private readonly DispatcherTimer checksPoll = new() { Interval = TimeSpan.FromSeconds(20) };

    public GitHubLocationItem(IRepoHostSurface surface)
    {
        this.surface = surface;
        Name = "PullRequestRow";
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        Margin = new Thickness(8, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;
        // While checks run the review is re-read every 20 seconds without a fetch.
        checksPoll.Tick += (_, _) => { if (!surface.IsBusy) surface.RefreshStatus(false); };
        AttachedToVisualTree += (_, _) => { surface.Changed += Render; Render(); };
        DetachedFromVisualTree += (_, _) => { surface.Changed -= Render; checksPoll.Stop(); };
    }

    /// <summary>The Pull request segment's next step from a status, as an action name; one pure rule for the strip and checks.</summary>
    internal static string NextStep(RepoBranchStatus status)
    {
        if (status.Provider != GitHubUI.Provider || status.OnBase) return "";
        var remote = !status.Published ? "publish" : status.Behind > 0 ? "sync" : status.Ahead > 0 ? "push" : "";
        var request = status.Review;
        if (request is null) return "open";
        if (request.State is not ("open" or "merged" or "closed")) return "";
        if (request.State == "merged") return status.Published ? "delete-remote" : "";
        if (request.State == "closed") return "reopen";
        // While the branch and origin differ, the next step stays with Remote's Push or Sync.
        if (remote.Length > 0) return "";
        if (request.Draft) return "ready";
        return request.Checks != "failing" && request.Mergeability == "clean" && request.MergeMethods.Count > 0 ? "merge" : "";
    }

    private void Render()
    {
        Children.Clear();
        var status = surface.Status;
        IsVisible = status is { Provider: GitHubUI.Provider };
        var running = status?.Review is { State: "open", Checks: "pending" };
        if (!running) checksPoll.Stop(); else if (!checksPoll.IsEnabled) checksPoll.Start();
        if (status is null || !IsVisible) return;
        var request = status.Review;
        Children.Add(Icon(request is null ? Ui.Hint : Ui.Muted));
        if (request is null) Children.Add(Text("PullRequestState", status.OnBase ? "—" : "No PR", Ui.Hint));
        else
        {
            var label = request switch
            {
                { State: "merged" } => "Merged #",
                { State: "closed" } => "Closed #",
                { Draft: true } => "Draft #",
                _ => "PR #"
            } + request.Number;
            var chip = ShipButton("PullRequestChip", label, () => surface.OpenLink(request.Url));
            ToolTip.SetTip(chip, request.Url + (request.Review is "approved" or "changes-requested" or "required" ? " · " + (request.Review == "required" ? "review required" : request.Review.Replace('-', ' ')) : ""));
            Children.Add(chip);
            if (request is { State: "open", Checks: "passing" or "failing" or "pending" })
            {
                var (glyph, brush, tip) = request.Checks switch
                {
                    "failing" => ("✗", Ui.Danger, "Checks failing"),
                    "pending" => ("●", Ui.Warning, "Checks running"),
                    _ => ("✓", Ui.Success, "Checks passing")
                };
                var checks = Text("PullRequestChecks", glyph, brush);
                ToolTip.SetTip(checks, tip); AutomationProperties.SetName(checks, tip);
                Children.Add(checks);
            }
        }
        switch (NextStep(status))
        {
            case "open":
                Children.Add(ShipButton("PullRequestOpen", "Open PR", () => _ = GitHubPrFlow.OpenAsync(surface, false)));
                Children.Add(ShipButton("PullRequestOpenDraft", "Draft", () => _ = GitHubPrFlow.OpenAsync(surface, true)));
                break;
            case "ready": Children.Add(ShipButton("PullRequestReady", "Ready for review", () => _ = surface.ApplyAsync(new("ready", status.Branch, status.Head)))); break;
            case "merge": Children.Add(ShipButton("PullRequestMerge", "Merge PR", () => _ = GitHubPrFlow.MergeAsync(surface, status))); break;
            case "reopen": Children.Add(ShipButton("PullRequestReopen", "Reopen", () => _ = surface.ApplyAsync(new("reopen", status.Branch, status.Head)))); break;
            case "delete-remote": Children.Add(ShipButton("PullRequestDeleteBranch", "Delete remote branch", () => _ = GitHubPrFlow.DeleteRemoteBranchAsync(surface, status))); break;
        }
    }

    // The field's leading icon, in the same column as the other location fields' icons.
    private static ContentControl Icon(IBrush brush) => new()
    {
        Name = "PullRequestIcon",
        Width = LayoutRoles.LocationIconSlot,
        Margin = new Thickness(0, 0, 4, 0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Content = Ui.Icon("fileDiff", brush, 14)
    };

    private static TextBlock Text(string name, string text, IBrush brush)
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
            IsEnabled = !surface.IsBusy
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
}