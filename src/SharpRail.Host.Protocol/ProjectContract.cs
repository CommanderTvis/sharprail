using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class SaveFileRequest
{
    [ProtoMember(1)] public string WorkspaceRoot { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
    [ProtoMember(3)] public string OriginalText { get; set; } = "";
    [ProtoMember(4)] public string Text { get; set; } = "";
}

[ProtoContract]
public sealed class SaveFileReply { }

public static class ProjectHeaders
{
    /// <summary>Trailer naming the <c>ChangeFailure</c> of a refused change write.</summary>
    public const string ChangeCode = "x-sharprail-change-code";

    /// <summary>Binary metadata carrying the UTF-8 workspace root a client last opened; the host resolves each call against it.</summary>
    public const string Root = "x-sharprail-root-bin";
}

[ProtoContract]
public sealed class ProjectRequest
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Scope { get; set; } = "";
    [ProtoMember(3)] public string Branch { get; set; } = "";
    [ProtoMember(4)] public string Action { get; set; } = "";
    [ProtoMember(5)] public string BaseBranch { get; set; } = "HEAD";
}

[ProtoContract]
public sealed class ProjectFileReply
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public bool IsDirectory { get; set; }
}

[ProtoContract]
public sealed class ProjectFilesReply
{
    [ProtoMember(1)] public List<ProjectFileReply> Files { get; set; } = [];
}

[ProtoContract]
public sealed class DocumentReply
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Text { get; set; } = "";
    [ProtoMember(3)] public byte[]? ImageData { get; set; }
    [ProtoMember(4)] public ContentMetadataDto? Info { get; set; }
}

[ProtoContract]
public sealed class ContentMetadataDto
{
    // A hash is never empty, so null (omitted on the wire) means the resource is absent.
    [ProtoMember(1)] public string? Sha256 { get; set; }
    [ProtoMember(2)] public long? ByteLength { get; set; }
    [ProtoMember(3)] public bool IsText { get; set; }
    [ProtoMember(4)] public string? MediaType { get; set; }
}

/// <summary>Revision null is the working tree, empty the index, otherwise a commit id; null is not distinguishable from empty on the wire, so <see cref="WorkingTree"/> says it.</summary>
[ProtoContract]
public sealed class ContentRequest
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public bool WorkingTree { get; set; }
    [ProtoMember(3)] public string Revision { get; set; } = "";
}

[ProtoContract]
public sealed class ContentReply
{
    [ProtoMember(1)] public byte[] Data { get; set; } = [];
    [ProtoMember(2)] public ContentMetadataDto Info { get; set; } = new();
}

[ProtoContract]
public sealed class DiffSidesReply
{
    [ProtoMember(1)] public string Original { get; set; } = "";
    [ProtoMember(2)] public string Modified { get; set; } = "";
    // A hash is never empty, so null (omitted on the wire) unambiguously means the side is absent.
    [ProtoMember(3)] public string? OriginalHash { get; set; }
    [ProtoMember(4)] public string? ModifiedHash { get; set; }
    [ProtoMember(5)] public string? OriginalCommit { get; set; }
    [ProtoMember(6)] public ContentMetadataDto? OriginalInfo { get; set; }
    [ProtoMember(7)] public ContentMetadataDto? ModifiedInfo { get; set; }
    [ProtoMember(8)] public string? OriginalRevision { get; set; }
    [ProtoMember(9)] public string? ModifiedRevision { get; set; }
}

[ProtoContract]
public sealed class RevertChangeRequest
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Scope { get; set; } = "";
    [ProtoMember(3)] public string Branch { get; set; } = "";
    /// <summary>False reverts the file's whole change and ignores the spans.</summary>
    [ProtoMember(4)] public bool IsRange { get; set; }
    [ProtoMember(5)] public int OriginalStart { get; set; }
    [ProtoMember(6)] public int OriginalCount { get; set; }
    [ProtoMember(7)] public int ModifiedStart { get; set; }
    [ProtoMember(8)] public int ModifiedCount { get; set; }
    [ProtoMember(9)] public string? OriginalHash { get; set; }
    [ProtoMember(10)] public string? ModifiedHash { get; set; }
}

[ProtoContract]
public sealed class UndoChangeRequest
{
    [ProtoMember(1)] public string ReceiptId { get; set; } = "";
    [ProtoMember(2)] public string? ModifiedHash { get; set; }
}

[ProtoContract]
public sealed class ChangeReceiptReply
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
    [ProtoMember(3)] public string Kind { get; set; } = "";
    [ProtoMember(4)] public long At { get; set; }
    [ProtoMember(5)] public string? BeforeHash { get; set; }
    [ProtoMember(6)] public long? BeforeLength { get; set; }
    [ProtoMember(7)] public int? BeforeMode { get; set; }
    [ProtoMember(8)] public string? AfterHash { get; set; }
    [ProtoMember(9)] public long? AfterLength { get; set; }
    [ProtoMember(10)] public int? AfterMode { get; set; }
    [ProtoMember(11)] public string? Trashed { get; set; }
}

[ProtoContract]
public sealed class ChangeReply
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string IndexStatus { get; set; } = "";
    [ProtoMember(3)] public string WorktreeStatus { get; set; } = "";
    [ProtoMember(4)] public string? OriginalPath { get; set; }
    [ProtoMember(5)] public int Added { get; set; }
    [ProtoMember(6)] public int Removed { get; set; }
}

[ProtoContract]
public sealed class WorktreeReply
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Branch { get; set; } = "";
    [ProtoMember(3)] public bool IsMain { get; set; }
    [ProtoMember(4)] public bool IsLocked { get; set; }
}

[ProtoContract]
public sealed class CommitReply
{
    [ProtoMember(1)] public string Sha { get; set; } = "";
    [ProtoMember(2)] public string ShortSha { get; set; } = "";
    [ProtoMember(3)] public string Subject { get; set; } = "";
    [ProtoMember(4)] public string Author { get; set; } = "";
    [ProtoMember(5)] public string CommittedAt { get; set; } = "";
}

[ProtoContract]
public sealed class GitReply
{
    [ProtoMember(1)] public bool IsRepository { get; set; }
    [ProtoMember(2)] public string Branch { get; set; } = "";
    [ProtoMember(3)] public List<ChangeReply> Changes { get; set; } = [];
    [ProtoMember(4)] public List<WorktreeReply> Worktrees { get; set; } = [];
    [ProtoMember(5)] public List<string> Branches { get; set; } = [];
    [ProtoMember(6)] public List<CommitReply> Commits { get; set; } = [];
}

[ProtoContract]
public sealed class FileChangeReply
{
    [ProtoMember(1)] public List<string> Paths { get; set; } = [];
    [ProtoMember(2)] public bool Rescan { get; set; }
}

[ProtoContract]
public sealed class SearchHitReply
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public int Line { get; set; }
    [ProtoMember(3)] public string Text { get; set; } = "";
}

[ProtoContract]
public sealed class SearchReply
{
    [ProtoMember(1)] public List<SearchHitReply> Hits { get; set; } = [];
    [ProtoMember(2)] public bool Truncated { get; set; }
}

[Service]
public partial interface IProjectRpc
{
    IAsyncEnumerable<FileChangeReply> WatchFilesAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SaveFileReply> SaveFileAsync(SaveFileRequest request, CallContext context = default);
    ValueTask<WorkspaceReply> OpenProjectAsync(ProjectRequest request, CallContext context = default);
    ValueTask<ProjectFilesReply> ListFilesAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DocumentReply> ReadFileAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SpecsReply> ListSpecsAsync(ProjectRequest request, CallContext context = default);
    /// <summary>Searches the workspace for <see cref="ProjectRequest.Path"/> as a plain substring.</summary>
    ValueTask<SearchReply> SearchAsync(ProjectRequest request, CallContext context = default);
    ValueTask<GitReply> GetGitAsync(ProjectRequest request, CallContext context = default);
    ValueTask<CommitsReply> ListCommitsAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DocumentReply> GetDiffAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DiffSidesReply> GetDiffSidesAsync(ProjectRequest request, CallContext context = default);
    ValueTask<ContentReply> ReadContentBytesAsync(ContentRequest request, CallContext context = default);
    ValueTask<ChangeReceiptReply> RevertChangeAsync(RevertChangeRequest request, CallContext context = default);
    ValueTask<ChangeReceiptReply> UndoChangeAsync(UndoChangeRequest request, CallContext context = default);
    ValueTask<GitReply> ApplyGitActionAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SaveFileReply> ApplyFileActionAsync(FileActionRequest request, CallContext context = default);
    ValueTask<BranchesReply> ListBranchesAsync(BranchesRequest request, CallContext context = default);
    ValueTask<OpenReviewReply> GetOpenReviewAsync(OpenReviewRequest request, CallContext context = default);
    ValueTask<PrDraftReply> PreviewPrAsync(ProjectRequest request, CallContext context = default);
    ValueTask<PrReply> OpenPrAsync(OpenPrRequest request, CallContext context = default);
    ValueTask<EditorsReply> ListEditorsAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SaveFileReply> OpenInEditorAsync(OpenInEditorRequest request, CallContext context = default);
    ValueTask<DiffStatsReply> GetDiffStatsAsync(DiffStatsRequest request, CallContext context = default);
    ValueTask<WorkspaceCatalogReply> ListWorkspacesAsync(WorkspaceCatalogRequest request, CallContext context = default);
    ValueTask<WorkspaceActionReply> ApplyWorkspaceActionAsync(WorkspaceActionRequest request, CallContext context = default);
}

[ProtoContract]
public sealed class DiffStatsRequest
{
    [ProtoMember(1)] public string WorkspacePath { get; set; } = "";
}

[ProtoContract]
public sealed class DiffStatsReply
{
    [ProtoMember(1)] public int Added { get; set; }
    [ProtoMember(2)] public int Removed { get; set; }
}

[ProtoContract]
public sealed class FileActionRequest
{
    [ProtoMember(1)] public string Kind { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
    [ProtoMember(3)] public string To { get; set; } = "";
}

[ProtoContract]
public sealed class BranchesRequest
{
    [ProtoMember(1)] public bool FetchDefault { get; set; }
}

[ProtoContract]
public sealed class RemoteBranchReply
{
    [ProtoMember(1)] public string Remote { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
}

[ProtoContract]
public sealed class BranchesReply
{
    [ProtoMember(1)] public List<string> Local { get; set; } = [];
    [ProtoMember(2)] public List<RemoteBranchReply> Remote { get; set; } = [];
    [ProtoMember(3)] public string DefaultBase { get; set; } = "";
    [ProtoMember(4)] public string SuggestedPath { get; set; } = "";
    [ProtoMember(5)] public string SuggestedBranch { get; set; } = "";
    [ProtoMember(6)] public string Current { get; set; } = "";
}

[ProtoContract]
public sealed class EditorReply
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Label { get; set; } = "";
}

[ProtoContract]
public sealed class EditorsReply
{
    [ProtoMember(1)] public List<EditorReply> Editors { get; set; } = [];
}

[ProtoContract]
public sealed class OpenInEditorRequest
{
    [ProtoMember(1)] public string EditorId { get; set; } = "";
    [ProtoMember(2)] public string WorktreePath { get; set; } = "";
}

[ProtoContract]
public sealed class SpecReply
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Title { get; set; } = "";
    [ProtoMember(3)] public string Path { get; set; } = "";
    [ProtoMember(4)] public string Parent { get; set; } = "";
    [ProtoMember(5)] public string Type { get; set; } = "";
    [ProtoMember(6)] public string Status { get; set; } = "";
}

[ProtoContract]
public sealed class SpecsReply
{
    [ProtoMember(1)] public List<SpecReply> Specs { get; set; } = [];
}

[ProtoContract]
public sealed class CommitsReply
{
    [ProtoMember(1)] public List<CommitReply> Commits { get; set; } = [];
}
[ProtoContract]
public sealed class OpenReviewRequest
{
    [ProtoMember(1)] public bool Fresh { get; set; }
}

[ProtoContract]
public sealed class OpenReviewReply
{
    [ProtoMember(1)] public bool Found { get; set; }
    [ProtoMember(2)] public int Number { get; set; }
    [ProtoMember(3)] public string Url { get; set; } = "";
    [ProtoMember(4)] public string Provider { get; set; } = "";
    [ProtoMember(5)] public int UnpushedCommits { get; set; }
    [ProtoMember(6)] public int BehindCommits { get; set; }
}

[ProtoContract]
public sealed class PrDraftReply
{
    [ProtoMember(1)] public string Title { get; set; } = "";
    [ProtoMember(2)] public string Body { get; set; } = "";
}

[ProtoContract]
public sealed class OpenPrRequest
{
    [ProtoMember(1)] public string Title { get; set; } = "";
    [ProtoMember(2)] public bool TitleEdited { get; set; }
    [ProtoMember(3)] public string Body { get; set; } = "";
    [ProtoMember(4)] public bool Draft { get; set; }
}

[ProtoContract]
public sealed class PrReply
{
    [ProtoMember(1)] public string Action { get; set; } = "";
    [ProtoMember(2)] public string Url { get; set; } = "";
    [ProtoMember(3)] public int Number { get; set; }
    [ProtoMember(4)] public int DirtyFiles { get; set; }
    [ProtoMember(5)] public string GhProblem { get; set; } = "";
}
