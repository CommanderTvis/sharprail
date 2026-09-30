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
public sealed class WorkspaceListMessage
{
    [ProtoMember(1)] public string ProjectRoot { get; set; } = "";
    [ProtoMember(2)] public List<string> Paths { get; set; } = [];
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
    [ProtoMember(7)] public List<WorkspaceListMessage> Workspaces { get; set; } = [];
}

[Service]
public interface IStateRpc
{
    ValueTask<StateReply> GetStateAsync(StateRequest request, CallContext context = default);
    ValueTask<StateReply> ChangeAsync(StateChangeRequest request, CallContext context = default);
    /// <summary>Server streaming: the current snapshot first, then one per change.</summary>
    IAsyncEnumerable<StateReply> WatchAsync(StateRequest request, CallContext context = default);
}
