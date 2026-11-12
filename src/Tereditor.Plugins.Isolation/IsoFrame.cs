using Tereditor.Wire;

namespace Tereditor.Plugins.Isolation;

/// <summary>What an <see cref="IsoFrame"/> is. Zero is never sent.</summary>
public enum IsoFrameKind { None, Hello, Init, Ready, Call, Reply, Fault, OneWay, Cancel, Release, Item, End, Credit, Push, Grants, Surface, Ping, Pong }

/// <summary>Why a call failed: the plugin call errors first, then the channel's own.</summary>
public enum IsoError { None, Unknown, Disabled, InvalidParams, Failed, Denied, DeadlineExceeded, Released, Protocol, Cancelled }

/// <summary>
/// One message on a plugin's channel, behind a 4-byte little-endian length. Ids of calls and streams are odd when the
/// host allocates them and even when the runner does. <see cref="Fds"/> lists the descriptors that travel with the
/// frame: on Unix one placeholder each (the descriptors ride as SCM_RIGHTS), on Windows the handle values the host
/// duplicated into the runner.
/// </summary>
[ProtoContract]
public sealed class IsoFrame
{
    [ProtoMember(1)] public IsoFrameKind Kind { get; set; }
    [ProtoMember(2)] public long Id { get; set; }
    [ProtoMember(3)] public int Member { get; set; }
    [ProtoMember(4)] public long Target { get; set; }
    [ProtoMember(5)] public string Payload { get; set; } = "";
    [ProtoMember(6)] public long DeadlineMs { get; set; }
    [ProtoMember(7)] public IsoError Error { get; set; }
    [ProtoMember(8)] public string Detail { get; set; } = "";
    [ProtoMember(9)] public List<long> Handles { get; set; } = [];
    [ProtoMember(10)] public List<long> Fds { get; set; } = [];
    [ProtoMember(11)] public string Permission { get; set; } = "";
    [ProtoMember(12)] public byte[] Bytes { get; set; } = [];
    [ProtoMember(13)] public int Count { get; set; }
}