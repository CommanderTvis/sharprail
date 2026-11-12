using System.Buffers.Binary;

using SharpRail.Wire;

namespace SharpRail.Plugins.Isolation;

/// <summary>A received frame broke the channel protocol; the host kills the runner that sent it.</summary>
public sealed class IsolationProtocolException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The channel's framing and limits (Security.SPEC.md, "Calls across the boundary").</summary>
public static class IsoWire
{
    /// <summary>The largest frame body. Bigger data travels by descriptor.</summary>
    public const int FrameBytes = 4 * 1024 * 1024;
    /// <summary>Items a stream may send ahead of the receiver's credit.</summary>
    public const int StreamWindow = 16;
    /// <summary>Each side's outbound queue holds at most this many frames and <see cref="QueueBytes"/>.</summary>
    public const int QueueFrames = 1024;
    public const long QueueBytes = 32L * 1024 * 1024;
    /// <summary>Descriptors one frame may carry.</summary>
    public const int FrameDescriptors = 16;

    static IsoWire() => IsoCodec.Register();

    /// <summary>The frame behind its 4-byte little-endian body length.</summary>
    public static byte[] Encode(IsoFrame frame)
    {
        var writer = new ProtoWriter(FrameBytes, static () => new InvalidOperationException("A plugin channel frame cannot exceed 4 MiB."));
        RpcMessage<IsoFrame>.Write!(writer, frame);
        var body = writer.ToArray();
        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, body.Length);
        body.CopyTo(framed.AsSpan(4));
        return framed;
    }

    /// <summary>Decodes one frame body; malformed bytes are a protocol violation.</summary>
    public static IsoFrame Decode(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new ProtoReader(body);
            return RpcMessage<IsoFrame>.Read!(ref reader);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException)
        {
            throw new IsolationProtocolException("A plugin channel frame is malformed.", error);
        }
    }
}