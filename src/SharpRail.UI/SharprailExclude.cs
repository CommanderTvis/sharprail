using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    /// <summary>How long the offer to exclude <c>.sharprail</c> stays; it is dismissible and its action is also in the Changes row menu.</summary>
    public TimeSpan SharprailPromptLifetime { get; set; } = TimeSpan.FromSeconds(30);

    // Workspaces this window has already asked about, so a refresh never raises the offer again.
    private readonly HashSet<string> sharprailOffered = [];

    private static bool IsSharprailPath(string path) => path == ".sharprail" || path.StartsWith(".sharprail/", StringComparison.Ordinal);

    private bool CanExcludeSharprail(GitChange change) =>
        change.IndexStatus == "?" && IsSharprailPath(change.Path) && state.Supports(HostProtocol.SharprailExclude);

    // The snapshot just applied already lists the directory as untracked, so no extra scan is needed to notice it;
    // the host is asked only then, and only once per workspace for a project that has not declined.
    private void ConsiderSharprailExclude(long request, GitSnapshot snapshot)
    {
        if (!snapshot.IsRepository || !snapshot.Changes.Any(CanExcludeSharprail)) return;
        if (state.Current.SharprailExcludeDeclined.Contains(projectRoot) || !sharprailOffered.Add(workspaceRoot)) return;
        _ = OfferSharprailExcludeAsync(request);
    }

    private async Task OfferSharprailExcludeAsync(long request)
    {
        var directory = workspaceRoot;
        var project = projectRoot;
        try
        {
            var status = await Task.Run(async () => await host.GetSharprailDirectoryAsync(lifetime.Token), lifetime.Token);
            if (request != projectRequest || directory != workspaceRoot || !status.CanExclude || state.Current.SharprailExcludeDeclined.Contains(project)) return;
            Toasts.Push(ToastVariant.Info, ".sharprail added to this project — add to .git/info/exclude?", duration: SharprailPromptLifetime,
                action: new("Add to .git/info/exclude", () => _ = ExcludeSharprailAsync(request)),
                secondary: new("Keep it in the repo", () => _ = ShareAsync(HostStateChange.DeclineSharprailExclude(project))));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Console.Error.WriteLine(error); }
    }

    private async Task ExcludeSharprailAsync(long request)
    {
        if (request != projectRequest || !WorkspaceMounted) { Toasts.Push(ToastVariant.Info, "The workspace changed — exclude .sharprail again from its Changes list"); return; }
        try
        {
            var status = await Task.Run(async () => await host.ExcludeSharprailDirectoryAsync(lifetime.Token), lifetime.Token);
            if (status.Written) Toasts.Push(ToastVariant.Success, ".sharprail is excluded in .git/info/exclude");
            else if (status.Detail.Length > 0) Toasts.Push(ToastVariant.Info, status.Detail);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { Toasts.Push(ToastVariant.Error, error.Message, "Couldn't update .git/info/exclude"); Console.Error.WriteLine(error); }
        if (request == projectRequest) await RefreshGitAsync(request);
    }
}