namespace SharpRail.Host.Abstractions;

public record ProjectFile(string Path, string Name, bool IsDirectory);
public record SpecDocument(string Id, string Title, string Path, string Parent, string Type);
public record FileDocument(string Path, string Text, byte[]? ImageData = null);
public record GitChange(string Path, string IndexStatus, string WorktreeStatus, string? OriginalPath, int Added, int Removed);
public record WorktreeInfo(string Path, string Branch, bool IsMain, bool IsLocked);
public record GitCommit(string Sha, string ShortSha, string Subject, string Author, string CommittedAt);
public record GitSnapshot(bool IsRepository, string Branch, IReadOnlyList<GitChange> Changes,
    IReadOnlyList<WorktreeInfo> Worktrees, IReadOnlyList<string> Branches)
{
    public IReadOnlyList<GitCommit> Commits { get; init; } = [];
}
public record GitAction(string Kind, string Path = "", string Branch = "", string BaseBranch = "HEAD");

public interface IProjectServices
{
    ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default);
    ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all");
    ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default);
    ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default);
    ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default);
}
