using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Configuration;

namespace SharpRail.Host.Protocol;

public enum TerminalInputKind { Start = 0, Data = 1, Resize = 2, Kill = 3 }

// The first message of a terminal call starts the session; later messages drive it.
[ProtoContract]
public sealed class TerminalInput
{
    [ProtoMember(1)] public TerminalInputKind Kind { get; set; }
    [ProtoMember(2)] public string SessionId { get; set; } = "";
    [ProtoMember(3)] public string WorkspaceRoot { get; set; } = "";
    [ProtoMember(4)] public int Columns { get; set; }
    [ProtoMember(5)] public int Rows { get; set; }
    [ProtoMember(6)] public byte[] Data { get; set; } = [];
}

// Output chunks follow one Started message; a final Exited message carries the exit code.
[ProtoContract]
public sealed class TerminalOutput
{
    [ProtoMember(1)] public byte[] Data { get; set; } = [];
    [ProtoMember(2)] public bool Started { get; set; }
    [ProtoMember(3)] public bool Exited { get; set; }
    [ProtoMember(4)] public int ExitCode { get; set; }
}

[ProtoContract]
public sealed class TerminalBusyRequest
{
    [ProtoMember(1)] public string SessionId { get; set; } = "";
}

[ProtoContract]
public sealed class TerminalBusyReply
{
    [ProtoMember(1)] public bool Busy { get; set; }
}

[Service]
public interface ITerminalRpc
{
    IAsyncEnumerable<TerminalOutput> RunAsync(IAsyncEnumerable<TerminalInput> input, CallContext context = default);
    ValueTask<TerminalBusyReply> IsBusyAsync(TerminalBusyRequest request, CallContext context = default);
}
