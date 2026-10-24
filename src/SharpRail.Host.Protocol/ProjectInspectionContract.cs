using ProtoBuf;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class InspectProjectReply
{
    /// <summary>0 repository, 1 initable, 2 missing, 3 not a directory.</summary>
    [ProtoMember(1)] public int Kind { get; set; }
}

[ProtoContract]
public sealed class HostDirectoryEntryReply
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public bool IsDirectory { get; set; }
    [ProtoMember(3)] public long Size { get; set; }
}

[ProtoContract]
public sealed class HostDirectoryReply
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    /// <summary>Empty at a root.</summary>
    [ProtoMember(2)] public string Parent { get; set; } = "";
    [ProtoMember(3)] public string Home { get; set; } = "";
    [ProtoMember(4)] public List<HostDirectoryEntryReply> Entries { get; set; } = [];
}

public partial interface IProjectRpc
{
    /// <summary>Lists the host directory at <see cref="ProjectRequest.Path"/> (absolute or <c>~</c>); since <c>HostProtocol.HostDirectoryBrowse</c>.</summary>
    ValueTask<HostDirectoryReply> ListHostDirectoryAsync(ProjectRequest request, CallContext context = default);
    ValueTask<InspectProjectReply> InspectProjectPathAsync(ProjectRequest request, CallContext context = default);
    ValueTask<SaveFileReply> PrewarmWorkspaceAsync(ProjectRequest request, CallContext context = default);
}