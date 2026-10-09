namespace SharpRail.Host.Abstractions;

public static class FileLimits
{
    // The editor keeps frame cost independent of size; open/save time and memory grow linearly.
    public const int EditableBytes = 64 * 1024 * 1024;
    // Markdown and image previews render the whole document at once.
    public const int PreviewBytes = 8 * 1024 * 1024;
    // gRPC carries a read as one message and a save as original plus new text.
    public const int ReadMessageBytes = EditableBytes + 16 * 1024 * 1024;
    public const int SaveMessageBytes = 2 * EditableBytes + 16 * 1024 * 1024;
}

public record FileSaveRequest(string WorkspaceRoot, string Path, string OriginalText, string Text);
public record FileChange(IReadOnlyList<string> Paths, bool Rescan = false);
public record ProjectFile(string Path, string Name, bool IsDirectory);
public record SpecDocument(string Id, string Title, string Path, string Parent, string Type);
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
}
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
}
public record EditorInfo(string Id, string Label);
/// <summary>Line totals of a workspace's changes against its review target.</summary>
public record DiffStats(int Added, int Removed);
/// <summary>The open pull request of the workspace branch; counts are -1 when unknown.</summary>
public record OpenReview(int Number, string Url, string Provider, int UnpushedCommits, int BehindCommits);
public record PrDraft(string Title, string Body);
public record PrRequest(string Title, bool TitleEdited, string Body, bool Draft);
/// <summary>Action is created, updated, pushed, compare or authFailed; GhProblem is missing or unauthenticated when gh could not be used.</summary>
public record PrResult(string Action, string Url, int Number, int DirtyFiles, string GhProblem);
public record GitAction(string Kind, string Path = "", string Branch = "", string BaseBranch = "HEAD");

public interface IProjectServices
{
    IAsyncEnumerable<FileChange> WatchFilesAsync(CancellationToken cancellationToken = default);
    ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default);
    ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default);
    ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all");
    ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default);
    ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default);
    ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default);
    ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken cancellationToken = default);
    ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparisonBranch, RevertTarget target, ChangeExpectation expect, CancellationToken cancellationToken = default);
    ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default);
    ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default);
    /// <summary>The change totals of one of this project's workspaces, or null from a host that cannot tell.</summary>
    ValueTask<DiffStats?> GetDiffStatsAsync(string workspacePath, CancellationToken cancellationToken = default) => ValueTask.FromResult<DiffStats?>(null);
    ValueTask<OpenReview?> GetOpenReviewAsync(bool fresh, CancellationToken cancellationToken = default);
    ValueTask<PrDraft> PreviewPrAsync(CancellationToken cancellationToken = default);
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