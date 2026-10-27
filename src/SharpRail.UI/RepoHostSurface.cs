using System.Diagnostics;
using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Input.Platform;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// What the window gives a repo hosting plugin's UI (W28): the branch status it already follows, the host calls its pull
/// request flow needs and the window's own toasts, dialogs and clipboard. The window itself keeps only the Remote segment.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private sealed class RepoSurface(WorkbenchWindow window) : IRepoHostSurface
    {
        public RepoBranchStatus? Status => window.branchStatus is { } status ? Map(status) : null;
        public bool IsBusy => window.shipBusy;
        public event Action? Changed;
        public bool IsRemote => window.remote;
        public Window Owner => window;
        public void Raise() => Changed?.Invoke();

        public async Task<RepoActionResult?> ApplyAsync(RepoAction action, string? failureTitle = null)
        {
            if (window.branchStatus is not { } status) return null;
            var result = await window.BranchActionAsync(status, new(action.Kind, action.Branch, action.Head, Method: action.Method, DeleteBranch: action.DeleteBranch), failureTitle);
            return result is null ? null : new(result.Outcome, result.Message);
        }

        public async Task<RepoPrDraft> PreviewAsync(CancellationToken cancellationToken)
        {
            var draft = await Task.Run(async () => await window.host.PreviewPrAsync(cancellationToken), cancellationToken);
            return new(draft.Title, draft.Body);
        }

        public async Task<RepoPrResult> OpenPrAsync(RepoPrRequest request, CancellationToken cancellationToken)
        {
            var result = await Task.Run(async () => await window.host.OpenPrAsync(new(request.Title, request.TitleEdited, request.Body, request.Draft), cancellationToken), cancellationToken);
            return new(result.Action, result.Url, result.Number, result.DirtyFiles, result.GhProblem);
        }

        public void RefreshStatus(bool fresh = true) => window.RequestBranchStatus(fresh, force: true);

        public void Toast(string kind, string title, string message) =>
            window.Toasts.Push(kind switch { "error" => ToastVariant.Error, "success" => ToastVariant.Success, _ => ToastVariant.Info }, message, title);

        public Task<bool> ConfirmAsync(string title, string explanation, string confirmLabel, string automationName) =>
            Dialogs.Confirm(window, title, explanation, confirmLabel, automationName);

        public Task NoticeAsync(string title, string description) => Dialogs.Notice(window, title, description);
        public Task CopyCommandAsync(string command) => window.CopyIntegrateAsync(command);
        public void OpenLink(string url) => window.OpenLink(url);

        public string? MergePreference
        {
            get => window.profile.Data.MergePreferences.GetValueOrDefault(window.projectRoot);
            set
            {
                if (value is null) window.profile.Data.MergePreferences.Remove(window.projectRoot);
                else window.profile.Data.MergePreferences[window.projectRoot] = value;
                window.SaveProfile();
            }
        }

        private static RepoBranchStatus Map(BranchStatus status) => new(status.Branch, status.Head, status.Provider, status.Published, status.OnBase, status.Ahead, status.Behind,
            status.Review is { } review ? new(review.Number, review.Url, review.UnpushedCommits, review.BehindCommits) : null,
            status.Details is { } details ? new(details.Number, details.Url, details.State, details.Draft, details.Checks, details.ReviewDecision, details.MergeState, details.MergeMethods, details.SameRepository) : null);
    }

    /// <summary>Asks for a fresh branch status, past the host's cache with a fetch; checks use it after changing the remote.</summary>
    public void RefreshBranchStatus() => RequestBranchStatus(true, force: true);

    private RepoSurface? repoSurface;
    private IRepoHostSurface RepoHostSurface => repoSurface ??= new(this);

    /// <summary>Opens an https link outside the app; checks replace it.</summary>
    public Action<string> OpenLink { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static bool ShellInert(string branch) => InertBranch().IsMatch(branch);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex InertBranch();

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
}