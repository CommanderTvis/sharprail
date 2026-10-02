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
public record ProjectFile(string Path, string Name, bool IsDirectory);
/// <summary>Changed workspace-relative paths, Git metadata changes and a request to refresh after lost filesystem events.</summary>
public record WorkspaceFileChanges(IReadOnlyList<string> Paths, bool GitChanged = false, bool Rescan = false);
public record FileDocument(string Path, string Text, byte[]? ImageData = null);
public record GitChange(string Path, string IndexStatus, string WorktreeStatus, string? OriginalPath, int Added, int Removed);
public record WorktreeInfo(string Path, string Branch, bool IsMain, bool IsLocked);
public record GitCommit(string Sha, string ShortSha, string Subject, string Author, string CommittedAt);
public record GitSnapshot(bool IsRepository, string Branch, IReadOnlyList<GitChange> Changes,
    IReadOnlyList<WorktreeInfo> Worktrees, IReadOnlyList<string> Branches)
{
    public IReadOnlyList<GitCommit> Commits { get; init; } = [];
}
/// <summary>Whole-file contents on both sides of a diff scope; a missing side is empty.</summary>
public record DiffSides(string Original, string Modified);
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
public record SearchHit(string Path, int Line, string Text);
/// <summary>Hits in path then line order; <paramref name="Truncated"/> when the host stopped at its limit.</summary>
public record SearchHits(IReadOnlyList<SearchHit> Hits, bool Truncated);
public record GitAction(string Kind, string Path = "", string Branch = "", string BaseBranch = "HEAD");
/// <summary>A change to one workspace path: create-file, create-folder, rename (to <see cref="To"/>), trash or reveal.</summary>
public record FileAction(string Kind, string Path, string To = "");

public interface IProjectServices
{
    ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default);
    /// <summary>Watches the currently opened workspace, captured for this subscription. The first empty batch confirms that watching has started.</summary>
    IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync(CancellationToken cancellationToken = default);
    ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default);
    ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default);
    ValueTask<SearchHits> SearchAsync(string query, CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all");
    ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default);
    /// <summary>Reads one commit by hexadecimal id, independently of the comparison catalog; null when it no longer exists.</summary>
    ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default);
    /// <summary>
    /// Lists the workspaces of an open or recent project of this host, independently of the project this session has
    /// open: its worktrees with the main one first, or the folder itself when it is not a repository.
    /// </summary>
    ValueTask<IReadOnlyList<WorktreeInfo>> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default);
    /// <summary>
    /// Clones <paramref name="url"/> into a new folder <paramref name="name"/> under <paramref name="parentPath"/> on
    /// this host, shallowly when <paramref name="depth"/> is given, and returns the clone's path; the caller opens it as
    /// a project. A failed clone leaves no folder behind and reports git's own reason.
    /// </summary>
    ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken cancellationToken = default);
    ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default);
    ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default);
    ValueTask ApplyFileActionAsync(FileAction action, CancellationToken cancellationToken = default);
    ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default);
    ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default);
}