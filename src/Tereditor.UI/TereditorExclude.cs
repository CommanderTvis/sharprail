using Tereditor.Host.Abstractions;
using Tereditor.UI.State;

namespace Tereditor.UI;

public sealed partial class WorkbenchWindow
{
    /// <summary>How long the offer to exclude <c>.tereditor</c> stays; it is dismissible and its action is also in the Changes row menu.</summary>
    public TimeSpan TereditorPromptLifetime { get; set; } = TimeSpan.FromSeconds(30);

    // Workspaces this window has already asked about, so a refresh never raises the offer again.
    private readonly HashSet<string> tereditorOffered = [];

    private static bool IsTereditorPath(string path) => path == ".tereditor" || path.StartsWith(".tereditor/", StringComparison.Ordinal);

    private bool CanExcludeTereditor(GitChange change) =>
        change.IndexStatus == "?" && IsTereditorPath(change.Path) && state.Supports(HostProtocol.TereditorExclude);

    // The snapshot just applied already lists the directory as untracked, so no extra scan is needed to notice it;
    // the host is asked only then, and only once per workspace for a project that has not declined.
    private void ConsiderTereditorExclude(long request, GitSnapshot snapshot)
    {
        if (!snapshot.IsRepository || !snapshot.Changes.Any(CanExcludeTereditor)) return;
        if (state.Current.TereditorExcludeDeclined.Contains(projectRoot) || !tereditorOffered.Add(workspaceRoot)) return;
        _ = OfferTereditorExcludeAsync(request);
    }

    private async Task OfferTereditorExcludeAsync(long request)
    {
        var directory = workspaceRoot;
        var project = projectRoot;
        try
        {
            var status = await Task.Run(async () => await host.GetTereditorDirectoryAsync(lifetime.Token), lifetime.Token);
            if (request != projectRequest || directory != workspaceRoot || !status.CanExclude || state.Current.TereditorExcludeDeclined.Contains(project)) return;
            Toasts.Push(ToastVariant.Info, ".tereditor added to this project — add to .git/info/exclude?", duration: TereditorPromptLifetime,
                action: new("Add to .git/info/exclude", () => _ = ExcludeTereditorAsync(request)),
                secondary: new("Keep it in the repo", () => _ = ShareAsync(HostStateChange.DeclineTereditorExclude(project))));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Console.Error.WriteLine(error); }
    }

    private async Task ExcludeTereditorAsync(long request)
    {
        if (request != projectRequest || !WorkspaceMounted) { Toasts.Push(ToastVariant.Info, "The workspace changed — exclude .tereditor again from its Changes list"); return; }
        try
        {
            var status = await Task.Run(async () => await host.ExcludeTereditorDirectoryAsync(lifetime.Token), lifetime.Token);
            if (status.Written) Toasts.Push(ToastVariant.Success, ".tereditor is excluded in .git/info/exclude");
            else if (status.Detail.Length > 0) Toasts.Push(ToastVariant.Info, status.Detail);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { Toasts.Push(ToastVariant.Error, error.Message, "Couldn't update .git/info/exclude"); Console.Error.WriteLine(error); }
        if (request == projectRequest) await RefreshGitAsync(request);
    }
}