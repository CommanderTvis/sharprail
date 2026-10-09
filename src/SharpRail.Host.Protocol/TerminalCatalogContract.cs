using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

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

[Service]
public interface ITerminalCatalogRpc
{
    ValueTask<TerminalCatalogReply> OpenWorkspaceAsync(TerminalCatalogRequest request, CallContext context = default);
    ValueTask<TerminalCatalogReply> ReserveAsync(TerminalCatalogRequest request, CallContext context = default);
    ValueTask<TerminalCatalogReply> CloseTabAsync(TerminalCatalogRequest request, CallContext context = default);
    ValueTask<TerminalCatalogReply> CloseWorkspaceAsync(TerminalCatalogRequest request, CallContext context = default);
    /// <summary>Server streaming: the current snapshot first, then one per change.</summary>
    IAsyncEnumerable<TerminalCatalogReply> WatchAsync(TerminalCatalogRequest request, CallContext context = default);
}