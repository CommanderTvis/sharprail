using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class StateRequest { }

[ProtoContract]
public sealed class StateChangeMessage
{
    [ProtoMember(1)] public string Kind { get; set; } = "";
    [ProtoMember(2)] public string Key { get; set; } = "";
    [ProtoMember(3)] public string Value { get; set; } = "";
}

[ProtoContract]
public sealed class StateChangeRequest
{
    [ProtoMember(1)] public List<StateChangeMessage> Changes { get; set; } = [];
}

[ProtoContract]
public sealed class SettingsMessage
{
    [ProtoMember(1)] public string Theme { get; set; } = "";
    [ProtoMember(2)] public string ThemeMode { get; set; } = "fixed";
    [ProtoMember(3)] public string SystemLight { get; set; } = "";
    [ProtoMember(4)] public string SystemDark { get; set; } = "";
    [ProtoMember(5)] public int FileLineWidth { get; set; }
    [ProtoMember(6)] public bool FileLineWidthUnbounded { get; set; }
    [ProtoMember(7)] public int MarkdownLineWidth { get; set; }
    [ProtoMember(8)] public bool MarkdownLineWidthUnbounded { get; set; }
    /// <summary>Always written, so zero (no replay) differs from a host that predates the setting.</summary>
    [ProtoMember(9, IsRequired = true)] public int TerminalReplayKb { get; set; } = 64;
}

[ProtoContract]
public sealed class PresetMessage
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public string Layout { get; set; } = "";
}

[ProtoContract]
public sealed class LabelMessage
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Label { get; set; } = "";
}

[ProtoContract]
public sealed class WorkspaceRefMessage
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string Reference { get; set; } = "";
}

[ProtoContract]
public sealed class ProjectRecordMessage
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
    [ProtoMember(3)] public string Slug { get; set; } = "";
    [ProtoMember(4)] public long LastOpened { get; set; }
}

[ProtoContract]
public sealed class StateReply
{
    [ProtoMember(1)] public long Revision { get; set; }
    [ProtoMember(2)] public SettingsMessage Settings { get; set; } = new();
    [ProtoMember(3)] public List<PresetMessage> Presets { get; set; } = [];
    [ProtoMember(4)] public List<string> Projects { get; set; } = [];
    [ProtoMember(5)] public List<string> RecentProjects { get; set; } = [];
    [ProtoMember(6)] public List<LabelMessage> Labels { get; set; } = [];
    // 7 carried the unpersisted per-project worktree paths that the registry replaced.
    [ProtoMember(8)] public List<WorkspaceRefMessage> Bases { get; set; } = [];
    [ProtoMember(9)] public List<WorkspaceRefMessage> DiffBases { get; set; } = [];
    [ProtoMember(10)] public List<WorkspaceRecordMessage> Workspaces { get; set; } = [];
    [ProtoMember(11)] public List<ProjectRecordMessage> ProjectRecords { get; set; } = [];
    [ProtoMember(13)] public List<PluginSettingsMessage> PluginSettings { get; set; } = [];
    [ProtoMember(14)] public List<string> PluginPaths { get; set; } = [];
    [ProtoMember(15)] public List<PluginRosterMessage> Plugins { get; set; } = [];
    [ProtoMember(16)] public List<TerminalAgentMessage> TerminalAgents { get; set; } = [];
    [ProtoMember(17)] public List<TerminalTitleMessage> TerminalTitles { get; set; } = [];
    // Empty when the host does not report its platform.
    [ProtoMember(12)] public string Platform { get; set; } = "";
}

[ProtoContract]
public sealed class PluginSettingsMessage
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    // The namespace as a JSON object.
    [ProtoMember(2)] public string Json { get; set; } = "";
}

[ProtoContract]
public sealed class TerminalTitleMessage
{
    [ProtoMember(1)] public string WorkspaceId { get; set; } = "";
    [ProtoMember(2)] public string TabKey { get; set; } = "";
    [ProtoMember(3)] public string Title { get; set; } = "";
}

[ProtoContract]
public sealed class TerminalAgentMessage
{
    [ProtoMember(1)] public string WorkspaceId { get; set; } = "";
    [ProtoMember(2)] public string TabKey { get; set; } = "";
    [ProtoMember(3)] public string Kind { get; set; } = "";
    [ProtoMember(4)] public string Command { get; set; } = "";
    [ProtoMember(5)] public string SessionId { get; set; } = "";
    [ProtoMember(6)] public string Cwd { get; set; } = "";
    [ProtoMember(7)] public string Model { get; set; } = "";
}

[ProtoContract]
public sealed class HandshakeRequest
{
    /// <summary>The caller's own protocol version; 0 when unknown.</summary>
    [ProtoMember(1)] public int ClientProtocolVersion { get; set; }
}

[ProtoContract]
public sealed class HandshakeReply
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string HostVersion { get; set; } = "";
}

[Service]
public interface IStateRpc
{
    ValueTask<HandshakeReply> HandshakeAsync(HandshakeRequest request, CallContext context = default);
    ValueTask<StateReply> GetStateAsync(StateRequest request, CallContext context = default);
    ValueTask<StateReply> ChangeAsync(StateChangeRequest request, CallContext context = default);
    /// <summary>Server streaming: the current snapshot first, then one per change.</summary>
    IAsyncEnumerable<StateReply> WatchAsync(StateRequest request, CallContext context = default);
    /// <summary>Server streaming: project and workspace lifecycle changes from now on, each tagged with its channel.</summary>
    IAsyncEnumerable<LifecycleMessage> WatchLifecycleAsync(StateRequest request, CallContext context = default);
}