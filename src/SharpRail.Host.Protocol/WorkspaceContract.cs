using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class WorkspaceRequest;

[ProtoContract]
public sealed class WorkspaceReply
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public string ProjectName { get; set; } = "";
    [ProtoMember(3)] public string RootPath { get; set; } = "";
    [ProtoMember(4)] public string ProjectRoot { get; set; } = "";
}

[ProtoContract]
public sealed class FileReply
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public bool IsDirectory { get; set; }
}

[ProtoContract]
public sealed class FilesReply
{
    [ProtoMember(1)] public List<FileReply> Entries { get; set; } = [];
}

[Service]
public interface IWorkspaceRpc
{
    ValueTask<WorkspaceReply> GetWorkspaceAsync(WorkspaceRequest request, CallContext context = default);
    ValueTask<FilesReply> ListRootFilesAsync(WorkspaceRequest request, CallContext context = default);
}
