using ProtoBuf;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class GitHubAuthenticationReply
{
    [ProtoMember(1)] public bool Connected { get; set; }
    [ProtoMember(2)] public string Detail { get; set; } = "";
}

public partial interface IProjectRpc
{
    ValueTask<GitHubAuthenticationReply> GetGitHubAuthenticationAsync(ProjectRequest request, CallContext context = default);
}