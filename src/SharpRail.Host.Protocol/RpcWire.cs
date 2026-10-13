using System.Net.WebSockets;

using ProtoBuf;

namespace SharpRail.Host.Protocol;

public enum RpcFrameKind { Open = 1, Data, End, Complete, Cancel, Credit, Ping, Pong }
[ProtoContract]
public sealed class RpcFrame
{
    [ProtoMember(1)] public RpcFrameKind Kind { get; set; }
    [ProtoMember(2)] public long Id { get; set; }
    [ProtoMember(3)] public string Method { get; set; } = "";
    [ProtoMember(4)] public Dictionary<string, string> Headers { get; set; } = [];
    [ProtoMember(5)] public byte[] Payload { get; set; } = [];
    [ProtoMember(6)] public long TimeoutMilliseconds { get; set; }
    [ProtoMember(7)] public StatusCode Code { get; set; }
    [ProtoMember(8)] public string Detail { get; set; } = "";
    [ProtoMember(9)] public bool Last { get; set; }
}
public static class RpcWire
{
    public const string Subprotocol = "sharprail.rpc.v1";
    public const int ChunkBytes = 32 * 1024;
    public const int FrameBytes = ChunkBytes + 16 * 1024;
    public const int MessageBytes = 144 * 1024 * 1024;
    public const int ConnectionBytes = 192 * 1024 * 1024;
    public const int MaxCalls = 64;
    public const int CallWindow = 16;
    public static byte[] Encode<T>(T value)
    {
        using var stream = new LimitedStream();
        Serializer.Serialize(stream, value);
        if (stream.Length > MessageBytes) throw new RpcException(new(StatusCode.ResourceExhausted, "RPC message exceeds the size limit."));
        return stream.ToArray();
    }
    private sealed class LimitedStream : MemoryStream
    {
        private void Check(int count)
        {
            if (Position + count > MessageBytes) throw new RpcException(new(StatusCode.ResourceExhausted, "RPC message exceeds the size limit."));
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
    public static T Decode<T>(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Serializer.Deserialize<T>(stream);
    }
    public static async ValueTask<RpcFrame> Receive(WebSocket socket, CancellationToken token)
    {
        var bytes = new byte[FrameBytes];
        var length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(bytes.AsMemory(length), token);
            if (result.MessageType != WebSocketMessageType.Binary) throw new IOException("The RPC connection closed or sent a nonbinary frame.");
            length += result.Count;
            if (result.EndOfMessage) return Decode<RpcFrame>(bytes.AsSpan(0, length).ToArray());
            if (length == bytes.Length) throw new IOException("RPC frame exceeds the size limit.");
        }
    }
}