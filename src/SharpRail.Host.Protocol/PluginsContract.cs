using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class PluginRosterRequest { }

[ProtoContract]
public sealed class PluginRetryRequest
{
    [ProtoMember(1)] public string Id { get; set; } = "";
}

[ProtoContract]
public sealed class PluginSideToolMessage
{
    [ProtoMember(1)] public string Tool { get; set; } = "";
    [ProtoMember(2)] public string Label { get; set; } = "";
    [ProtoMember(3)] public string Icon { get; set; } = "";
    [ProtoMember(4)] public string DefaultSide { get; set; } = "";
    [ProtoMember(5)] public bool RequiresGit { get; set; }
}

[ProtoContract]
public sealed class PluginFileViewerMessage
{
    [ProtoMember(1)] public List<string> Extensions { get; set; } = [];
    [ProtoMember(2)] public List<string> Names { get; set; } = [];
    [ProtoMember(3)] public string Read { get; set; } = "";
}

[ProtoContract]
public sealed class PluginChannelMessage
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public string Kind { get; set; } = "";
    [ProtoMember(3)] public string Snapshot { get; set; } = "";
    [ProtoMember(4)] public List<string> Key { get; set; } = [];
}

// Enums travel as PluginJson's camelCase names; an absent string is empty.
[ProtoContract]
public sealed class PluginRosterMessage
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Label { get; set; } = "";
    [ProtoMember(3)] public string Description { get; set; } = "";
    [ProtoMember(4)] public string Icon { get; set; } = "";
    [ProtoMember(5)] public string Version { get; set; } = "";
    [ProtoMember(6)] public int WireVersion { get; set; }
    [ProtoMember(7)] public string Origin { get; set; } = "";
    [ProtoMember(8)] public string Status { get; set; } = "";
    [ProtoMember(9)] public string Reason { get; set; } = "";
    [ProtoMember(10)] public List<string> DependsOn { get; set; } = [];
    [ProtoMember(11)] public List<PluginSideToolMessage> SideTools { get; set; } = [];
    [ProtoMember(12)] public List<PluginFileViewerMessage> FileViewers { get; set; } = [];
    [ProtoMember(13)] public List<PluginChannelMessage> Channels { get; set; } = [];
    [ProtoMember(14)] public string Ui { get; set; } = "";
    [ProtoMember(15)] public string Assets { get; set; } = "";
}

[ProtoContract]
public sealed class PluginRosterReply
{
    [ProtoMember(1)] public List<PluginRosterMessage> Plugins { get; set; } = [];
}

// Params, results and payloads are JSON written with PluginJson.Options.
[ProtoContract]
public sealed class PluginCallMessage
{
    [ProtoMember(1)] public string PluginId { get; set; } = "";
    [ProtoMember(2)] public string Method { get; set; } = "";
    [ProtoMember(3)] public byte[] ParamsJson { get; set; } = [];
    [ProtoMember(4)] public string ClientKey { get; set; } = "";
}

[ProtoContract]
public sealed class PluginCallReply
{
    // JSON null for a null result.
    [ProtoMember(1)] public byte[] ResultJson { get; set; } = [];
}

[ProtoContract]
public sealed class PluginSubscribeMessage
{
    [ProtoMember(1)] public string PluginId { get; set; } = "";
    [ProtoMember(2)] public string Channel { get; set; } = "";
    // Empty for a subscription without a key.
    [ProtoMember(3)] public byte[] KeyJson { get; set; } = [];
    [ProtoMember(4)] public string ClientKey { get; set; } = "";
}

[ProtoContract]
public sealed class PluginPushMessage
{
    [ProtoMember(1)] public byte[] PayloadJson { get; set; } = [];
}

[ProtoContract]
public sealed class PluginFileRequest
{
    [ProtoMember(1)] public string PluginId { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
}

[ProtoContract]
public sealed class PluginFileReply
{
    [ProtoMember(1)] public bool Found { get; set; }
    [ProtoMember(2)] public byte[] Data { get; set; } = [];
}

// A failed call is an RpcException: Unknown is NotFound, Disabled is FailedPrecondition, InvalidParams is
// InvalidArgument and Failed is Unknown, with the message as the status detail.
[Service]
public interface IPluginRpc
{
    ValueTask<PluginRosterReply> ListAsync(PluginRosterRequest request, CallContext context = default);
    ValueTask<PluginRosterReply> RescanAsync(PluginRosterRequest request, CallContext context = default);
    ValueTask<PluginRosterReply> RetryAsync(PluginRetryRequest request, CallContext context = default);
    ValueTask<PluginCallReply> CallAsync(PluginCallMessage request, CallContext context = default);
    /// <summary>Server streaming: every later publish that reaches the subscriber; never a replay.</summary>
    IAsyncEnumerable<PluginPushMessage> SubscribeAsync(PluginSubscribeMessage request, CallContext context = default);
    ValueTask<PluginFileReply> ReadFileAsync(PluginFileRequest request, CallContext context = default);
}