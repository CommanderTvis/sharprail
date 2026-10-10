using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

namespace SharpRail.Host.Protocol;

public enum TerminalInputKind { Attach = 0, Data = 1, Resize = 2, Kill = 3 }

// The first message of a terminal call attaches to a session; later messages drive it. Ending the call
// detaches without ending the shell.
[ProtoContract]
public sealed class TerminalInput
{
    [ProtoMember(1)] public TerminalInputKind Kind { get; set; }
    [ProtoMember(2)] public string SessionId { get; set; } = "";
    [ProtoMember(3)] public string WorkspaceRoot { get; set; } = "";
    [ProtoMember(4)] public int Columns { get; set; }
    [ProtoMember(5)] public int Rows { get; set; }
    [ProtoMember(6)] public byte[] Data { get; set; } = [];
    [ProtoMember(7)] public string ClientId { get; set; } = "";
    [ProtoMember(8)] public long Offset { get; set; } = -1;
    [ProtoMember(9)] public string TabKey { get; set; } = "";
    [ProtoMember(10)] public bool Yield { get; set; }
    [ProtoMember(11)] public bool Watch { get; set; }
}

// One Attached message, then output chunks with the host position after each, then Exited with the exit
// code or Detached when another client took the session over. The Attached message is also Detached when
// the attachment never held the session: a yielding attach, or a resume, that found another client there.
// It is Watching instead when the attach asked to watch; a watched stream carries a chunk without data when
// only the grid changed. A host that predates watching leaves Watching and the grid unset.
[ProtoContract]
public sealed class TerminalOutput
{
    [ProtoMember(1)] public byte[] Data { get; set; } = [];
    [ProtoMember(2)] public bool Attached { get; set; }
    [ProtoMember(3)] public bool Exited { get; set; }
    [ProtoMember(4)] public int ExitCode { get; set; }
    [ProtoMember(5)] public bool Detached { get; set; }
    [ProtoMember(6)] public bool Created { get; set; }
    [ProtoMember(7)] public long Position { get; set; }
    // On the Attached message of a shell started for a tab a plugin offered to revive.
    [ProtoMember(8)] public string PrefillText { get; set; } = "";
    [ProtoMember(9)] public bool PrefillSubmit { get; set; }
    [ProtoMember(10)] public bool Watching { get; set; }
    // The grid the shell runs at, on the Attached message and every chunk.
    [ProtoMember(11)] public int Columns { get; set; }
    [ProtoMember(12)] public int Rows { get; set; }
}

[ProtoContract]
public sealed class TerminalSessionRequest
{
    [ProtoMember(1)] public string SessionId { get; set; } = "";
}

[ProtoContract]
public sealed class TerminalBusyReply
{
    [ProtoMember(1)] public bool Busy { get; set; }
}

[ProtoContract]
public sealed class TerminalClosed;

[Service]
public interface ITerminalRpc
{
    IAsyncEnumerable<TerminalOutput> RunAsync(IAsyncEnumerable<TerminalInput> input, CallContext context = default);
    ValueTask<TerminalBusyReply> IsBusyAsync(TerminalSessionRequest request, CallContext context = default);
    ValueTask<TerminalClosed> CloseAsync(TerminalSessionRequest request, CallContext context = default);
}