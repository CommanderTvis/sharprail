using ProtoBuf;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class TerminalTabMessage
{
    [ProtoMember(1)] public string Key { get; set; } = "";
    [ProtoMember(2)] public string Title { get; set; } = "";
}

// OpenWorkspace carries the client's tabs, Reserve and CloseTab one tab (its key alone for a close), and
// CloseWorkspace and Watch none.
[ProtoContract]
public sealed class TerminalCatalogRequest
{
    [ProtoMember(1)] public string WorkspaceRoot { get; set; } = "";
    [ProtoMember(2)] public List<TerminalTabMessage> Tabs { get; set; } = [];
}

[ProtoContract]
public sealed class TerminalWorkspaceMessage
{
    [ProtoMember(1)] public string WorkspaceRoot { get; set; } = "";
    [ProtoMember(2)] public List<TerminalTabMessage> Tabs { get; set; } = [];
}

[ProtoContract]
public sealed class TerminalCatalogReply
{
    [ProtoMember(1)] public long Revision { get; set; }
    [ProtoMember(2)] public List<TerminalWorkspaceMessage> Workspaces { get; set; } = [];
}

// One terminal signal: Kind 0 progress (State and Percent, -1 for none), 1 bell, 2 notification (Title, Body).
[ProtoContract]
public sealed class TerminalSignalMessage
{
    [ProtoMember(1)] public string SessionId { get; set; } = "";
    [ProtoMember(2)] public int Kind { get; set; }
    [ProtoMember(3)] public int State { get; set; }
    [ProtoMember(4, IsRequired = true)] public int Percent { get; set; } = -1;
    [ProtoMember(5)] public string Title { get; set; } = "";
    [ProtoMember(6)] public string Body { get; set; } = "";
}

public interface ITerminalCatalogRpc
{
    ValueTask<TerminalCatalogReply> OpenWorkspaceAsync(TerminalCatalogRequest request, CallContext context = default);
    ValueTask<TerminalCatalogReply> ReserveAsync(TerminalCatalogRequest request, CallContext context = default);
    ValueTask<TerminalCatalogReply> CloseTabAsync(TerminalCatalogRequest request, CallContext context = default);
    ValueTask<TerminalCatalogReply> CloseWorkspaceAsync(TerminalCatalogRequest request, CallContext context = default);
    /// <summary>Server streaming: the current snapshot first, then one per change.</summary>
    IAsyncEnumerable<TerminalCatalogReply> WatchAsync(TerminalCatalogRequest request, CallContext context = default);
    /// <summary>Server streaming: every session's current progress, then each later progress, bell and notification.</summary>
    IAsyncEnumerable<TerminalSignalMessage> WatchSignalsAsync(TerminalCatalogRequest request, CallContext context = default);
}