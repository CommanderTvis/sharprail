using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

namespace SharpRail.Host.Protocol;

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
public sealed class GitReply
{
    [ProtoMember(1)] public bool IsRepository { get; set; }
    [ProtoMember(2)] public string Branch { get; set; } = "";
    [ProtoMember(3)] public List<ChangeReply> Changes { get; set; } = [];
    [ProtoMember(4)] public List<WorktreeReply> Worktrees { get; set; } = [];
    [ProtoMember(5)] public List<string> Branches { get; set; } = [];
}

[Service]
public interface IProjectRpc
{
    ValueTask<WorkspaceReply> OpenProjectAsync(ProjectRequest request, CallContext context = default);
    ValueTask<ProjectFilesReply> ListFilesAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DocumentReply> ReadFileAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SpecsReply> ListSpecsAsync(ProjectRequest request, CallContext context = default);
    ValueTask<GitReply> GetGitAsync(ProjectRequest request, CallContext context = default);
    ValueTask<DocumentReply> GetDiffAsync(ProjectRequest request, CallContext context = default);
    ValueTask<GitReply> ApplyGitActionAsync(ProjectRequest request, CallContext context = default);
}

[ProtoContract]
public sealed class SpecReply
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Title { get; set; } = "";
    [ProtoMember(3)] public string Path { get; set; } = "";
    [ProtoMember(4)] public string Parent { get; set; } = "";
    [ProtoMember(5)] public string Type { get; set; } = "";
}

[ProtoContract]
public sealed class SpecsReply
{
    [ProtoMember(1)] public List<SpecReply> Specs { get; set; } = [];
}
