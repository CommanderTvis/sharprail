using Avalonia;
using Avalonia.Controls;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Review.UI;

/// <summary>
/// The Review tool window: a one-line summary of the workspace's changes, the repo host's pull request review section and a button
/// that shows the Changes list. The summary follows the workspace's file revision while the pane is attached.
/// </summary>
internal sealed class ReviewPanel : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly TextBlock summary = Ui.Text("", Ui.TextBrush);
    private string workspace;
    private IDisposable? watch;
    private int generation;

    internal ReviewPanel(IPluginUIContext context, string workspace)
    {
        this.context = context;
        this.workspace = workspace;
        Margin = new Thickness(16);
        Spacing = 12;
        Name = "ReviewPanel";
        summary.Name = "ReviewSummary";
        Children.Add(Ui.Text("Repository review", Ui.TextBrush, 16));
        Children.Add(summary);
        Children.Add(context.RepoHostReviewSection());
        Children.Add(Ui.Text("Select a file in Files · Changed to inspect its diff.", Ui.Hint, 12));
        Children.Add(Ui.Button("Show Changes", () => context.RevealTool(this.workspace, "changes"), "fileDiff"));
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => { watch?.Dispose(); watch = null; };
    }

    /// <summary>Shows another workspace's summary in this same pane.</summary>
    internal bool Retarget(string next)
    {
        workspace = next;
        summary.Text = "";
        if (watch is not null) Refresh();
        return true;
    }

    private void Attach()
    {
        watch?.Dispose();
        watch = context.WatchHost(host => host.WorkspaceRevisions.GetValueOrDefault(workspace), (_, _) => Refresh());
        _ = context.WatchWorkspaceAsync(workspace);
        Refresh();
    }

    private async void Refresh()
    {
        var current = ++generation;
        var target = workspace;
        try
        {
            var result = await context.RequestAsync(ReviewContract.Summary, new ReviewSummaryParams(target));
            if (current != generation) return;
            summary.Text = result.IsRepository ? $"{result.Changed} changed files on {result.Branch}" : "No repository selected";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (current == generation) summary.Text = "No repository selected";
            context.Log.Warn("Could not read the review summary: " + error.Message);
        }
    }
}