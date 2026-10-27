namespace SharpRail.Host.Abstractions;

public static class FileLimits
{
    // The editor keeps frame cost independent of size; open/save time and memory grow linearly.
    public const int EditableBytes = 64 * 1024 * 1024;
    // Markdown and image previews render the whole document at once.
    public const int PreviewBytes = 8 * 1024 * 1024;
    // WebSocket RPC carries a read as one message and a save as original plus new text.
    public const int ReadMessageBytes = EditableBytes + 16 * 1024 * 1024;
    public const int SaveMessageBytes = 2 * EditableBytes + 16 * 1024 * 1024;
}

/// <summary>One relative file or directory in a client upload; directories carry no bytes.</summary>
public record FileTransferEntry(string Path, bool IsDirectory, byte[] Data);
/// <summary>A bounded upload to a workspace folder, or a private attachment directory when Folder is null.</summary>
public record FileTransferRequest(string WorkspaceRoot, string? Folder, IReadOnlyList<FileTransferEntry> Entries);

public record FileSaveRequest(string WorkspaceRoot, string Path, string OriginalText, string Text);
public record ProjectFile(string Path, string Name, bool IsDirectory, bool GitIgnored = false);
public record SpecDocument(string Id, string Title, string Path, string Parent, string Type)
{
    /// <summary>The frontmatter <c>status</c>, empty when the spec has none.</summary>
    public string Status { get; init; } = "";
}
/// <summary>
/// What the host knows about a resource's bytes: SHA-256 in lowercase hex and byte length (both null when the resource is
/// absent), whether it is text, and its media type from magic numbers, then the filename. A byte-only resource has no
/// text; the client fetches its bytes.
/// </summary>
public record ContentMetadata(string? Sha256, long? ByteLength, bool IsText, string? MediaType)
{
    /// <summary>Types a browser would run script from; a client must draw or show them inert, never execute them.</summary>
    public bool IsActive => MediaType is "text/html" or "application/xhtml+xml" or "image/svg+xml";
}
public record FileDocument(string Path, string Text, byte[]? ImageData = null)
{
    public ContentMetadata? Info { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>Why the file is read-only, for an editor to show over it; null for no notice.</summary>
    public string? ReadOnlyNotice { get; init; }
}
/// <summary>Changed workspace-relative paths, Git metadata changes and a request to refresh after lost filesystem events.</summary>
public record WorkspaceFileChanges(IReadOnlyList<string> Paths, bool GitChanged = false, bool Rescan = false);
public record GitChange(string Path, string IndexStatus, string WorktreeStatus, string? OriginalPath, int Added, int Removed);
public record WorktreeInfo(string Path, string Branch, bool IsMain, bool IsLocked);
public record GitCommit(string Sha, string ShortSha, string Subject, string Author, string CommittedAt);
public record GitSnapshot(bool IsRepository, string Branch, IReadOnlyList<GitChange> Changes,
    IReadOnlyList<WorktreeInfo> Worktrees, IReadOnlyList<string> Branches)
{
    public IReadOnlyList<GitCommit> Commits { get; init; } = [];
}
/// <summary>
/// Whole-file contents on both sides of a diff scope; a missing side is empty. The hashes are SHA-256 of each side's
/// bytes in lowercase hex, null when the side is absent, and are what a change request must echo back.
/// </summary>
public record DiffSides(string Original, string Modified)
{
    public string? OriginalHash { get; init; }
    public string? ModifiedHash { get; init; }
    /// <summary>The commit the original side was read at; null for an absent side, the index or an unborn HEAD.</summary>
    public string? OriginalCommit { get; init; }
    /// <summary>Content metadata of each side; an absent side has no hash. A byte-only side has empty text.</summary>
    public ContentMetadata? OriginalInfo { get; init; }
    public ContentMetadata? ModifiedInfo { get; init; }
    /// <summary>
    /// What <see cref="IProjectServices.ReadContentBytesAsync"/> takes to fetch each side's bytes: null is the working
    /// tree (or an absent side), empty the index, otherwise a commit id that never moves.
    /// </summary>
    public string? OriginalRevision { get; init; }
    public string? ModifiedRevision { get; init; }
}
/// <summary>The raw bytes of one resource with their metadata. The host never decodes or interprets them.</summary>
public record ContentBytes(byte[] Data, ContentMetadata Info);
/// <summary>One-based inclusive lines of a side; a zero count is an insertion point before <paramref name="Start"/>.</summary>
public record LineSpan(int Start, int Count);
/// <summary>A whole file when <see cref="Original"/> and <see cref="Modified"/> are null, otherwise the spans of one hunk.</summary>
public record RevertTarget(LineSpan? Original = null, LineSpan? Modified = null)
{
    public bool IsFile => Original is null && Modified is null;
}
/// <summary>The SHA-256 of each side the client rendered, null for an absent side.</summary>
public record ChangeExpectation(string? OriginalHash, string? ModifiedHash);
public record ChangeIdentity(string? Hash, long? ByteLength, int? Mode);
/// <summary>Kind is revert or undo; Trashed is the claim path of a file moved to the system trash.</summary>
public record ChangeReceipt(string Id, string Path, string Kind, long At, ChangeIdentity Before, ChangeIdentity After, string? Trashed = null);
public enum ChangeFailure { StaleView, ScopeImmutable, RangeInvalid, ReceiptUnknown, UnsupportedChange }
/// <summary>A refused change write; clients branch on <see cref="Code"/> instead of matching text.</summary>
public sealed class ChangeException(ChangeFailure code, string message) : IOException(message)
{
    public ChangeFailure Code { get; } = code;
}
public record RemoteBranch(string Remote, string Name)
{
    public string Ref => Remote + "/" + Name;
}
public record BranchCatalog(IReadOnlyList<string> Local, IReadOnlyList<RemoteBranch> Remote, string DefaultBase)
{
    public string SuggestedPath { get; init; } = "";
    public string SuggestedBranch { get; init; } = "";
    /// <summary>The project checkout's branch, empty when HEAD is detached.</summary>
    public string Current { get; init; } = "";
}
public record EditorInfo(string Id, string Label);
/// <summary>Line totals of a workspace's changes against its review target.</summary>
public record DiffStats(int Added, int Removed);
/// <summary>
/// The branch's pull request (or merge request) in the provider-neutral model (Plugin API H24): the open one first, else the
/// newest in any state, else none. State is open, merged or closed; Checks is passing, failing, pending or none; Review is
/// approved, changes-requested, required or none; Mergeability is clean, blocked, behind, conflicting or unknown (the forge's
/// merge state, never approvals); MergeMethods are merge, squash and rebase as the repository allows them. SameRepository is
/// false for a request whose head is a fork's branch. Unpushed and Behind count commits against the request's head and are
/// -1 when unknown (only an open request has them).
/// </summary>
public record PullRequest(int Number, string Title, string Url, string State, bool Draft, string Checks, string Review, string Mergeability,
    IReadOnlyList<string> MergeMethods, bool SameRepository, int Unpushed, int Behind);
/// <summary>
/// The branch's remote state and next step (ThinkRail #771): whether origin exists and which repo hosting plugin owns it
/// (its plugin id, empty for a plain remote), whether the branch is published there, commits ahead and behind it (-1 when
/// unknown), commits ahead of the review base, whether the branch is its own base, the commit HEAD names and the
/// owner's pull request for the branch.
/// </summary>
public record BranchStatus(string Branch, bool HasOrigin, string Provider, bool Published, int Ahead, int Behind, int AheadOfBase,
    bool OnBase, string BaseName, string Head, PullRequest? Review);
/// <summary>
/// A branch mutation bound to what the caller saw: Kind is push, ready, merge, reopen or delete-remote; Head is the commit
/// the caller's status named (the host refuses when the branch moved); ConfirmBase allows pushing a branch that is its
/// own base; Method and DeleteBranch apply to merge.
/// </summary>
public record BranchAction(string Kind, string Branch, string Head, bool ConfirmBase = false, string Method = "", bool DeleteBranch = false);
/// <summary>Outcome is done, confirmBase or authFailed; Message says what happened or why not.</summary>
public record BranchActionResult(string Outcome, string Message);
public record PrDraft(string Title, string Body);
public record PrRequest(string Title, bool TitleEdited, string Body, bool Draft);
/// <summary>Action is created, updated, pushed, compare or authFailed; Problem is missing or unauthenticated when the provider's tool could not be used.</summary>
public record PrResult(string Action, string Url, int Number, int DirtyFiles, string Problem);
public record SearchHit(string Path, int Line, string Text);
/// <summary>Hits in path then line order; <paramref name="Truncated"/> when the host stopped at its limit.</summary>
public record SearchHits(IReadOnlyList<SearchHit> Hits, bool Truncated);
public record GitAction(string Kind, string Path = "", string Branch = "", string BaseBranch = "HEAD");
/// <summary>A change to one workspace path: create-file, create-folder, rename/copy (to <see cref="To"/>), trash or reveal.</summary>
public record FileAction(string Kind, string Path, string To = "")
{
    /// <summary>When supplied, refuses the action if this session has switched workspaces.</summary>
    public string? WorkspaceRoot { get; init; }
}

/// <summary>
/// Where a workspace's <c>.sharprail</c> directory stands against Git. <see cref="Written"/> is set only by the call that
/// appended the entry; <see cref="Detail"/> says why an exclusion changed nothing, empty otherwise.
/// </summary>
public sealed record SharprailDirectoryStatus(bool Exists, bool Tracked, bool Ignored, bool Written = false, string Detail = "")
{
    /// <summary>Whether the directory shows as untracked noise that a local exclusion would remove.</summary>
    public bool CanExclude => Exists && !Tracked && !Ignored;
}

public partial interface IProjectServices
{
    ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default);
    /// <summary>Watches the currently opened workspace, captured for this subscription. The first empty batch confirms that watching has started.</summary>
    IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync(CancellationToken cancellationToken = default);
    /// <summary>Stores a PNG prompt attachment in the host clipboard directory and returns its absolute path.</summary>
    ValueTask<string> SavePromptImageAsync(byte[] data, CancellationToken cancellationToken = default)
        => ValueTask.FromException<string>(new NotSupportedException("This host cannot store prompt images."));
    /// <summary>Imports client bytes without overwriting existing entries; returns absolute host paths of the upload roots.</summary>
    ValueTask<IReadOnlyList<string>> ImportFilesAsync(FileTransferRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromException<IReadOnlyList<string>>(new NotSupportedException("This host cannot import files."));
    ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default);
    ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default);
    ValueTask<SearchHits> SearchAsync(string query, CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all");
    ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default);
    /// <summary>Reads one commit by hexadecimal id, independently of the comparison catalog; null when it no longer exists.</summary>
    ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default);
    /// <summary>
    /// Lists the workspaces of an open or recent project of this host, independently of the project this session has
    /// open: its worktrees with the main one first, or the folder itself when it is not a repository.
    /// </summary>
    /// <summary>Creates a new empty project folder under an existing parent and returns its path. Existing targets are rejected.</summary>
    ValueTask<string> CreateProjectAsync(string parentPath, string name, CancellationToken cancellationToken = default);
    /// <summary>
    /// Clones <paramref name="url"/> into a new folder <paramref name="name"/> under <paramref name="parentPath"/> on
    /// this host, shallowly when <paramref name="depth"/> is given, and returns the clone's path; the caller opens it as
    /// a project. A failed clone leaves no folder behind and reports git's own reason.
    /// </summary>
    ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken cancellationToken = default);
    ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default);
    ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default);
    ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken cancellationToken = default);
    ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparisonBranch, RevertTarget target, ChangeExpectation expect, CancellationToken cancellationToken = default);
    ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default);
    ValueTask ApplyFileActionAsync(FileAction action, CancellationToken cancellationToken = default);
    ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default);
    /// <summary>The change totals of one of this project's workspaces, or null from a host that cannot tell.</summary>
    ValueTask<DiffStats?> GetDiffStatsAsync(string workspacePath, CancellationToken cancellationToken = default) => ValueTask.FromResult<DiffStats?>(null);
    /// <summary>
    /// Whether the workspace root holds a <c>.sharprail</c> directory, and whether Git tracks or already ignores it.
    /// A host that predates the operation reports it absent.
    /// </summary>
    ValueTask<SharprailDirectoryStatus> GetSharprailDirectoryAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new SharprailDirectoryStatus(false, false, false));
    /// <summary>
    /// Appends <c>/.sharprail/</c> to the repository's shared <c>info/exclude</c> (the common Git directory, so every
    /// workspace of the project is covered). Idempotent; changes nothing when the directory is tracked or already ignored.
    /// </summary>
    ValueTask<SharprailDirectoryStatus> ExcludeSharprailDirectoryAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromException<SharprailDirectoryStatus>(new NotSupportedException("This host cannot exclude the .sharprail directory."));
    ValueTask<PrDraft> PreviewPrAsync(CancellationToken cancellationToken = default);
    /// <summary>The branch's remote facts and review; null for a detached HEAD or a host that predates the operation.</summary>
    ValueTask<BranchStatus?> GetBranchStatusAsync(bool fresh, CancellationToken cancellationToken = default) => ValueTask.FromResult<BranchStatus?>(null);
    /// <summary>Pushes the branch or acts on its pull request after checking the branch still matches what the caller saw.</summary>
    ValueTask<BranchActionResult> ApplyBranchActionAsync(BranchAction action, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<BranchActionResult>(new NotSupportedException("This host cannot push or act on pull requests."));
    ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default);
    ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default);
    /// <summary>
    /// Reads a project's workspace registry after ensuring its Default workspace and re-syncing branches with their
    /// checkouts, and lists the worktrees that could still be attached. Changes reach every client through host state.
    /// </summary>
    ValueTask<WorkspaceCatalog> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default);
    /// <summary>Returns the workspace the action created, changed or dropped; null when there was none to drop.</summary>
    ValueTask<WorkspaceRecord?> ApplyWorkspaceActionAsync(WorkspaceAction action, CancellationToken cancellationToken = default);
}