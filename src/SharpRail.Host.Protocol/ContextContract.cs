using ProtoBuf;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class ContextConsentRequestDto
{
    [ProtoMember(1)] public string Operation { get; set; } = "review";
    [ProtoMember(2)] public string ArtifactId { get; set; } = "";
    [ProtoMember(3)] public string TermsSha256 { get; set; } = "";
}

[ProtoContract]
public sealed class ContextConsentReply
{
    [ProtoMember(1)] public string State { get; set; } = "";
    [ProtoMember(2)] public string Detail { get; set; } = "";
    [ProtoMember(3)] public string Version { get; set; } = "";
    [ProtoMember(4)] public string ArtifactId { get; set; } = "";
    [ProtoMember(5)] public string TermsSha256 { get; set; } = "";
    [ProtoMember(6)] public string Text { get; set; } = "";
    [ProtoMember(7)] public string AcceptedAtUtc { get; set; } = "";
    [ProtoMember(8)] public string Installation { get; set; } = "";
}

public partial interface IProjectRpc
{
    ValueTask<ContextConsentReply> ContextConsentAsync(ContextConsentRequestDto request, CallContext context = default);
}

[ProtoContract]
public sealed class ContextRequestDto
{
    [ProtoMember(1)] public string Workspace { get; set; } = "";
    [ProtoMember(2)] public string Operation { get; set; } = "status";
    [ProtoMember(3)] public string Query { get; set; } = "";
    [ProtoMember(4)] public string Path { get; set; } = "";
    [ProtoMember(5)] public int Limit { get; set; } = 20;
}

[ProtoContract]
public sealed class ContextReply
{
    [ProtoMember(1)] public string State { get; set; } = "";
    [ProtoMember(2)] public string Detail { get; set; } = "";
    [ProtoMember(3)] public bool Enabled { get; set; }
    [ProtoMember(4)] public string Data { get; set; } = "";
    [ProtoMember(5)] public string Command { get; set; } = "";
}

public partial interface IProjectRpc
{
    ValueTask<ContextReply> ContextAsync(ContextRequestDto request, CallContext context = default);
}