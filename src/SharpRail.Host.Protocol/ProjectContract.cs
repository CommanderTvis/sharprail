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
    [ProtoMember(6)] public string Url { get; set; } = "";
    [ProtoMember(7)] public int Depth { get; set; }
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
}

[ProtoContract]
public sealed class DiffSidesReply
{
    [ProtoMember(1)] public string Original { get; set; } = "";
    [ProtoMember(2)] public string Modified { get; set; } = "";
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
public sealed class WorktreesReply
{
    [ProtoMember(1)] public List<WorktreeReply> Worktrees { get; set; } = [];
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

[ProtoContract]
public sealed class WorkspaceFileChangesReply
{
    [ProtoMember(1)] public List<string> Paths { get; set; } = [];
    [ProtoMember(2)] public bool GitChanged { get; set; }
    [ProtoMember(3)] public bool Rescan { get; set; }
}

[Service]
public interface IProjectRpc
{
    ValueTask<SaveFileReply> SaveFileAsync(SaveFileRequest request, CallContext context = default);
    ValueTask<WorkspaceReply> OpenProjectAsync(ProjectRequest request, CallContext context = default);
    ValueTask<ProjectFilesReply> ListFilesAsync(ProjectRequest request, CallContext context = default);
    IAsyncEnumerable<WorkspaceFileChangesReply> WatchFilesAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DocumentReply> ReadFileAsync(ProjectRequest request, CallContext context = default);
    /// <summary>Searches the workspace for <see cref="ProjectRequest.Path"/> as a plain substring.</summary>
    ValueTask<SearchReply> SearchAsync(ProjectRequest request, CallContext context = default);
    ValueTask<GitReply> GetGitAsync(ProjectRequest request, CallContext context = default);
    ValueTask<CommitsReply> ListCommitsAsync(ProjectRequest request, CallContext context = default);
    ValueTask<CommitLookupReply> GetCommitAsync(ProjectRequest request, CallContext context = default);
    /// <summary>Lists the workspaces of the project at <see cref="ProjectRequest.Path"/>.</summary>
    ValueTask<WorktreesReply> ListWorkspacesAsync(ProjectRequest request, CallContext context = default);
    /// <summary>Clones <see cref="ProjectRequest.Url"/> into <see cref="ProjectRequest.Path"/>/<see cref="ProjectRequest.Branch"/>; a zero depth is a full clone.</summary>
    ValueTask<DocumentReply> CloneProjectAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DocumentReply> GetDiffAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DiffSidesReply> GetDiffSidesAsync(ProjectRequest request, CallContext context = default);
    ValueTask<GitReply> ApplyGitActionAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SaveFileReply> ApplyFileActionAsync(FileActionRequest request, CallContext context = default);
    ValueTask<BranchesReply> ListBranchesAsync(BranchesRequest request, CallContext context = default);
    ValueTask<EditorsReply> ListEditorsAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SaveFileReply> OpenInEditorAsync(OpenInEditorRequest request, CallContext context = default);
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
public sealed class CommitLookupReply
{
    [ProtoMember(1)] public CommitReply? Commit { get; set; }
}

[ProtoContract]
public sealed class CommitsReply
{
    [ProtoMember(1)] public List<CommitReply> Commits { get; set; } = [];
}