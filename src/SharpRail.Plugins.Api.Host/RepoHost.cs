namespace SharpRail.Plugins.Api.Host;

/// <summary>
/// A pull request (or merge request) of a branch in the provider-neutral model (H24). A provider maps its forge's values
/// onto these; the host never sees the forge's own strings.
/// </summary>
/// <param name="Number">The request's number on its forge.</param>
/// <param name="Title">The request's title.</param>
/// <param name="Url">A plain https link; a provider drops the request when it has none.</param>
/// <param name="State"><c>open</c>, <c>merged</c> or <c>closed</c>.</param>
/// <param name="Draft">Whether the request is a draft.</param>
/// <param name="Checks"><c>passing</c>, <c>failing</c>, <c>pending</c> or <c>none</c>.</param>
/// <param name="Review"><c>approved</c>, <c>changes-requested</c>, <c>required</c> or <c>none</c>.</param>
/// <param name="Mergeability"><c>clean</c>, <c>blocked</c>, <c>behind</c>, <c>conflicting</c> or <c>unknown</c>: the forge's merge state, never approvals.</param>
/// <param name="MergeMethods">The subset of <c>merge</c>, <c>squash</c> and <c>rebase</c> the repository allows.</param>
/// <param name="SameRepository">False when the head is a fork's branch; the provider refuses actions on it.</param>
/// <param name="Unpushed">Local commits not on the request's head, -1 when unknown. The host fills it after the lookup.</param>
/// <param name="Behind">Commits on the request's head not local, -1 when unknown. The host fills it after the lookup.</param>
public sealed record PullRequest(int Number, string Title, string Url, string State, bool Draft, string Checks, string Review,
    string Mergeability, IReadOnlyList<string> MergeMethods, bool SameRepository, int Unpushed = -1, int Behind = -1);

/// <summary>The branch a provider call concerns; the host has already checked it and pushed it when the call needs that.</summary>
/// <param name="Directory">The workspace's checkout, the working directory for the provider's tools.</param>
/// <param name="RemoteUrl"><c>origin</c>'s URL as git reports it.</param>
/// <param name="Branch">The workspace's branch.</param>
/// <param name="BaseName">The review base branch's name on the remote, empty when unknown.</param>
public sealed record RepoHostBranch(string Directory, string RemoteUrl, string Branch, string BaseName);

/// <summary>The text of a pull request a user composed.</summary>
/// <param name="Title">The title.</param>
/// <param name="TitleEdited">Whether the user changed the title; an update keeps the existing title otherwise.</param>
/// <param name="Body">The description.</param>
/// <param name="Draft">Whether a created request is a draft.</param>
public sealed record RepoHostRequest(string Title, bool TitleEdited, string Body, bool Draft);

/// <summary>
/// What a provider's create or update did: <c>Action</c> is <c>created</c>, <c>updated</c> or <c>failed</c> (the host then
/// shows the compare link), <c>Problem</c> is empty or
/// <c>missing</c> (the tool is not installed) or <c>unauthenticated</c>, which the host turns into the compare-link
/// outcome.
/// </summary>
public sealed record RepoHostOutcome(string Action, string Url, int Number, string Problem = "");

/// <summary>Whether the provider's tool is signed in, with a line to show.</summary>
public sealed record RepoHostSignIn(bool Connected, string Detail);

/// <summary>A tool run through <see cref="IPluginHostContext.RunToolAsync"/>.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Output">Standard output, decoded as UTF-8.</param>
/// <param name="Error">Standard error.</param>
public sealed record RepoHostToolResult(int ExitCode, string Output, string Error);

/// <summary>
/// What a plugin contributes for the remotes it recognizes (H24). One provider owns a workspace's branch when it is the
/// first active one, by plugin id, whose <see cref="Recognizes"/> accepts <c>origin</c>'s URL. Methods run on pool
/// threads with the host's budgets (queries 8 seconds, mutations 5 minutes); one that throws or times out reads as no
/// review and is logged against its plugin. They never run git: pushes, fetches and ahead/behind stay in the host.
/// </summary>
public abstract class RepoHostProvider
{
    /// <summary>Whether this provider owns a remote with this URL.</summary>
    public abstract bool Recognizes(string remoteUrl);

    /// <summary>The tool's sign-in state for the Settings page.</summary>
    public abstract ValueTask<RepoHostSignIn> SignInAsync(CancellationToken cancellationToken);

    /// <summary>The branch's open request, else its newest in any state, else <see langword="null"/>.</summary>
    public abstract ValueTask<PullRequest?> FindAsync(RepoHostBranch branch, CancellationToken cancellationToken);

    /// <summary>Creates the branch's request, or updates the open one's description (and title when edited).</summary>
    public abstract ValueTask<RepoHostOutcome> CreateOrUpdateAsync(RepoHostBranch branch, RepoHostRequest request, CancellationToken cancellationToken);

    /// <summary>Marks the request ready for review.</summary>
    public abstract ValueTask ReadyAsync(RepoHostBranch branch, PullRequest request, CancellationToken cancellationToken);

    /// <summary>Reopens a closed request.</summary>
    public abstract ValueTask ReopenAsync(RepoHostBranch branch, PullRequest request, CancellationToken cancellationToken);

    /// <summary>Merges an open request with a method from <see cref="PullRequest.MergeMethods"/>, bound to the commit the caller saw.</summary>
    public abstract ValueTask MergeAsync(RepoHostBranch branch, PullRequest request, string method, string head, CancellationToken cancellationToken);

    /// <summary>A link that opens the forge's compose page for the branch, for when the tool cannot be used.</summary>
    public abstract string CompareUrl(RepoHostBranch branch, RepoHostRequest request);
}

/// <summary>One finished branch status, handed to <see cref="IPluginHostContext.OnBranchStatus"/> observers.</summary>
/// <param name="WorkspaceId">The workspace's id, empty when the host cannot name it.</param>
/// <param name="Directory">The workspace's checkout.</param>
/// <param name="Provider">The owning provider's plugin id, empty for a plain remote.</param>
/// <param name="Branch">The branch.</param>
/// <param name="Published">Whether origin has the branch.</param>
/// <param name="Ahead">Commits ahead of origin's branch, -1 when unknown.</param>
/// <param name="Behind">Commits behind origin's branch, -1 when unknown.</param>
/// <param name="Head">The commit HEAD names.</param>
/// <param name="Review">The owner's request, <see langword="null"/> for none.</param>
public sealed record BranchStatusEvent(string WorkspaceId, string Directory, string Provider, string Branch, bool Published, int Ahead, int Behind,
    string Head, PullRequest? Review);