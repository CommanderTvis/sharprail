using ProtoBuf;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class WorkspaceRecordMessage
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string ProjectRoot { get; set; } = "";
    [ProtoMember(3)] public string Kind { get; set; } = "";
    [ProtoMember(4)] public string Path { get; set; } = "";
    [ProtoMember(5)] public string Branch { get; set; } = "";
    [ProtoMember(6)] public string BaseBranch { get; set; } = "";
    [ProtoMember(7)] public bool InitialTerminalPending { get; set; }
}

[ProtoContract]
public sealed class ExistingWorktreeMessage
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Branch { get; set; } = "";
}

[ProtoContract]
public sealed class WorkspaceCatalogRequest
{
    [ProtoMember(1)] public string ProjectRoot { get; set; } = "";
}

[ProtoContract]
public sealed class WorkspaceCatalogReply
{
    [ProtoMember(1)] public List<WorkspaceRecordMessage> Workspaces { get; set; } = [];
    [ProtoMember(2)] public List<ExistingWorktreeMessage> Existing { get; set; } = [];
}

[ProtoContract]
public sealed class WorkspaceActionRequest
{
    [ProtoMember(1)] public string Kind { get; set; } = "";
    [ProtoMember(2)] public string ProjectRoot { get; set; } = "";
    [ProtoMember(3)] public string Id { get; set; } = "";
    [ProtoMember(4)] public string Path { get; set; } = "";
    [ProtoMember(5)] public string Name { get; set; } = "";
    [ProtoMember(6)] public string BaseBranch { get; set; } = "";
}

[ProtoContract]
public sealed class WorkspaceActionReply
{
    /// <summary>Absent when the action had no workspace to return.</summary>
    [ProtoMember(1)] public WorkspaceRecordMessage? Workspace { get; set; }
}

[ProtoContract]
public sealed class LifecycleMessage
{
    [ProtoMember(1)] public string Channel { get; set; } = "";
    [ProtoMember(2)] public string Kind { get; set; } = "";
    [ProtoMember(3)] public string ProjectRoot { get; set; } = "";
    [ProtoMember(4)] public string WorkspaceId { get; set; } = "";
    [ProtoMember(5)] public WorkspaceRecordMessage? Workspace { get; set; }
}