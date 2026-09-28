namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? gitRefresh;
    private bool gitLoading;
    private string? gitError;

    private async Task RefreshGitAsync(long request)
    {
        if (request != projectRequest || !WorkspaceMounted || lifetime.IsCancellationRequested) return;
        gitRefresh?.Cancel();
        gitRefresh?.Dispose();
        gitRefresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = gitRefresh.Token;
        var selectedComparison = comparison;
        try
        {
            var snapshot = await Task.Run(async () => await host.GetGitAsync(selectedComparison, token), token);
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison) return;
            git = snapshot;
            branchLabel.Text = snapshot.IsRepository ? snapshot.Branch : "";
            branchIcon.IsVisible = snapshot.IsRepository;
            gitLoading = false; gitError = null;
            RefreshGitPanels();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (token.IsCancellationRequested || request != projectRequest || selectedComparison != comparison) return;
            gitLoading = false; gitError = "Git could not be loaded: " + error.Message;
            RefreshGitPanels();
            Console.Error.WriteLine(error);
        }
    }

    private void RefreshGitPanels()
    {
        toolContent.Remove("projects");
        toolContent.Remove("changes");
        toolContent.Remove("review");
        surface.RefreshContents("projects", "changes", "review");
    }
}
