using Avalonia.Controls;

namespace SharpRail.Plugins.Api.UI;

/// <summary>The open request of the workspace's branch (W28); counts are -1 when unknown.</summary>
public sealed record RepoOpenReview(int Number, string Url, int Unpushed, int Behind);

/// <summary>
/// The newest request of the branch in any state (W28). <c>State</c> is <c>OPEN</c>, <c>MERGED</c> or <c>CLOSED</c>, <c>Checks</c> is
/// <c>passing</c>, <c>failing</c>, <c>pending</c> or empty, <c>ReviewDecision</c> and <c>MergeState</c> are the forge's values and
/// <c>MergeMethods</c> are <c>merge</c>, <c>squash</c> and <c>rebase</c>.
/// </summary>
public sealed record RepoReviewDetails(int Number, string Url, string State, bool Draft, string Checks, string ReviewDecision, string MergeState,
    IReadOnlyList<string> MergeMethods, bool SameRepository);

/// <summary>The branch's remote state as the host reported it (W28); <c>Provider</c> is the owning plugin's id, empty for a plain remote.</summary>
public sealed record RepoBranchStatus(string Branch, string Head, string Provider, bool Published, bool OnBase, int Ahead, int Behind,
    RepoOpenReview? Review, RepoReviewDetails? Details);

/// <summary>A mutation bound to the branch and commit the caller saw: <c>ready</c>, <c>merge</c>, <c>reopen</c> or <c>delete-remote</c>.</summary>
public sealed record RepoAction(string Kind, string Branch, string Head, string Method = "", bool DeleteBranch = false);

/// <summary>The outcome of a <see cref="RepoAction"/>: <c>done</c>, or null from the surface when it failed (the surface already told the user).</summary>
public sealed record RepoActionResult(string Outcome, string Message);

/// <summary>The title and description proposed for a new request.</summary>
public sealed record RepoPrDraft(string Title, string Body);

/// <summary>The request a user composed.</summary>
public sealed record RepoPrRequest(string Title, bool TitleEdited, string Body, bool Draft);

/// <summary>Action is created, updated, pushed, compare or authFailed; Problem is missing or unauthenticated when the tool could not be used.</summary>
public sealed record RepoPrResult(string Action, string Url, int Number, int DirtyFiles, string Problem);

/// <summary>
/// What the workbench window gives a repo hosting contribution (W28): the branch status the window already follows, and
/// the host calls and window services its pull request UI needs. Everything runs on the UI thread; calls to the host
/// leave it. Git stays in the host: a mutation here is bound to the commit its status named.
/// </summary>
public interface IRepoHostSurface
{
    /// <summary>The window's latest branch status, <see langword="null"/> until the first one arrives or without a repository.</summary>
    RepoBranchStatus? Status { get; }

    /// <summary>Whether a branch mutation runs, so buttons disable.</summary>
    bool IsBusy { get; }

    /// <summary>Raised when <see cref="Status"/> or <see cref="IsBusy"/> changed.</summary>
    event Action? Changed;

    /// <summary>Whether the host is another computer (setup guidance then cannot name its operating system).</summary>
    bool IsRemote { get; }

    /// <summary>The window, the owner of dialogs.</summary>
    Window Owner { get; }

    /// <summary>Applies a mutation; tells the user on success or failure and refreshes the status. Null when it failed or was refused while busy.</summary>
    Task<RepoActionResult?> ApplyAsync(RepoAction action, string? failureTitle = null);

    /// <summary>The proposed title and description for a new request.</summary>
    Task<RepoPrDraft> PreviewAsync(CancellationToken cancellationToken);

    /// <summary>Pushes the branch and opens or updates its request.</summary>
    Task<RepoPrResult> OpenPrAsync(RepoPrRequest request, CancellationToken cancellationToken);

    /// <summary>Asks for a branch status; <paramref name="fresh"/> fetches origin first.</summary>
    void RefreshStatus(bool fresh = true);

    /// <summary>Shows a toast; <paramref name="kind"/> is <c>info</c>, <c>success</c> or <c>error</c>.</summary>
    void Toast(string kind, string title, string message);

    /// <summary>Asks the user to confirm.</summary>
    Task<bool> ConfirmAsync(string title, string explanation, string confirmLabel, string automationName);

    /// <summary>Shows a notice.</summary>
    Task NoticeAsync(string title, string description);

    /// <summary>Copies text and tells the user to run it in a terminal.</summary>
    Task CopyCommandAsync(string command);

    /// <summary>Opens an https link outside the app.</summary>
    void OpenLink(string url);

    /// <summary>The remembered merge choice of this project, <c>method|delete</c> or <c>method|keep</c>; null for none.</summary>
    string? MergePreference { get; set; }
}

/// <summary>
/// A repo hosting provider's UI (W28): its item in the location strip, right after Remote, and its section in the Review
/// panel. Both exist only while the plugin is enabled; each control hides itself while it has nothing to show and follows
/// <see cref="IRepoHostSurface.Changed"/>, never doing I/O on the UI thread.
/// </summary>
/// <param name="LocationItem">Creates the strip item's content for a window; a new control each time. It sits under the <paramref name="Caption"/>.</param>
/// <param name="Caption">The small caption above the location item, such as <c>Pull request</c>.</param>
/// <param name="ReviewSection">Creates the Review panel section for a window; a new control each time.</param>
public sealed record RepoHostUIRegistration(string Caption, Func<IRepoHostSurface, Control> LocationItem, Func<IRepoHostSurface, Control> ReviewSection);