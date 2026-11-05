using System.Net.WebSockets;

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
public delegate void RpcSink(ReadOnlySpan<byte> bytes);
public static class RpcWire
{
    public const string Subprotocol = "sharprail.rpc.v1";
    public const int ChunkBytes = 32 * 1024;
    public const int FrameBytes = ChunkBytes + 16 * 1024;
    public const int MessageBytes = 144 * 1024 * 1024;
    public const int ConnectionBytes = 192 * 1024 * 1024;
    public const int MaxCalls = 64;
    public const int CallWindow = 16;
    static RpcWire() => RpcCodec.Register();
    public static byte[] Encode<T>(T value)
    {
        if (value is null) return [];
        var writer = new ProtoWriter(MessageBytes);
        Writer<T>()(writer, value);
        return writer.ToArray();
    }
    /// <summary>The length <see cref="Encode"/> would return, without the size limit and without holding the bytes.</summary>
    public static long Measure<T>(T value)
    {
        if (value is null) return 0;
        var writer = ProtoWriter.Measuring();
        Writer<T>()(writer, value);
        return writer.Length;
    }
    /// <summary>Hands the bytes <see cref="Encode"/> would return to <paramref name="sink"/> in small chunks, without the size limit.</summary>
    public static void WriteTo<T>(T value, RpcSink sink)
    {
        if (value is null) return;
        var write = Writer<T>();
        var measuring = ProtoWriter.Measuring();
        write(measuring, value);
        var streaming = measuring.Streaming(sink);
        write(streaming, value);
        streaming.Flush();
    }
    private static Action<ProtoWriter, T> Writer<T>() => RpcMessage<T>.Write ?? throw new NotSupportedException($"{typeof(T)} is not an RPC message.");
    public static T Decode<T>(ReadOnlySpan<byte> bytes)
    {
        var read = RpcMessage<T>.Read ?? throw new NotSupportedException($"{typeof(T)} is not an RPC message.");
        var reader = new ProtoReader(bytes);
        return read(ref reader);
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
            if (result.EndOfMessage) return Decode<RpcFrame>(bytes.AsSpan(0, length));
            if (length == bytes.Length) throw new IOException("RPC frame exceeds the size limit.");
        }
    }
}