using System.Buffers.Binary;
using System.Text;

namespace Tereditor.Wire;

/// <summary>Marks a wire message; <c>scripts/generate-rpc.cs</c> emits its codec into the owning project's <c>*Codec.Generated.cs</c>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProtoContractAttribute : Attribute;

/// <summary>A wire field number; <see cref="IsRequired"/> writes the value even when it is zero.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ProtoMemberAttribute(int tag) : Attribute
{
    public int Tag { get; } = tag;
    public bool IsRequired { get; set; }
}

/// <summary>Registered per message type by the generated codec.</summary>
internal static class RpcMessage<T>
{
    internal static Action<ProtoWriter, T>? Write;
    internal static ProtoRead<T>? Read;
}

internal delegate T ProtoRead<T>(ref ProtoReader reader);

/// <summary>Receives the bytes of a streaming write in small chunks.</summary>
internal delegate void ProtoSink(ReadOnlySpan<byte> bytes);

/// <summary>
/// Writes protobuf fields into a growing buffer that refuses to pass a byte limit. A measuring writer only counts
/// the bytes and records each sub-message's size; its streaming writer replays those sizes, so a second write of
/// the same value reaches a sink in small chunks and the message is never held whole.
/// </summary>
internal sealed class ProtoWriter
{
    private const int Chunk = 4096;
    private readonly int limit;
    private readonly Func<Exception>? exceeded;
    private readonly List<long>? sizes;
    private readonly ProtoSink? sink;
    private byte[] buffer;
    private int length;
    private long passed;
    private int replayed;

    /// <summary>A writer that throws what <paramref name="exceeded"/> returns once the message would pass <paramref name="limit"/>.</summary>
    internal ProtoWriter(int limit, Func<Exception> exceeded)
    {
        this.limit = limit;
        this.exceeded = exceeded;
        buffer = new byte[256];
    }

    private ProtoWriter(List<long> sizes, ProtoSink? sink)
    {
        limit = int.MaxValue;
        this.sizes = sizes;
        this.sink = sink;
        buffer = new byte[Chunk];
    }

    internal static ProtoWriter Measuring() => new([], null);

    /// <summary>A writer for a second write of the value this measuring writer measured.</summary>
    internal ProtoWriter Streaming(ProtoSink target) => new(sizes!, target);

    private bool Counting => sizes is not null && sink is null;

    internal long Length => passed + length;

    internal byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

    internal void Flush()
    {
        sink?.Invoke(buffer.AsSpan(0, length));
        passed += length;
        length = 0;
    }

    private void Ensure(int count)
    {
        if (sizes is not null)
        {
            if (length + count > buffer.Length) Flush();
            return;
        }
        if ((long)length + count > limit) throw exceeded!();
        if (length + count <= buffer.Length) return;
        Array.Resize(ref buffer, (int)Math.Min(limit, Math.Max((long)length + count, buffer.Length * 2L)));
    }

    private static int PrefixLength(long size)
    {
        var prefix = 1;
        for (var rest = (ulong)size >> 7; rest != 0; rest >>= 7) prefix++;
        return prefix;
    }

    private void Varint(ulong value)
    {
        Ensure(10);
        while (value >= 0x80)
        {
            buffer[length++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buffer[length++] = (byte)value;
    }

    private void Tag(int field, int wire) => Varint((uint)(field << 3 | wire));

    internal void Int32(int field, int value) { Tag(field, 0); Varint((ulong)(long)value); }

    internal void Int64(int field, long value) { Tag(field, 0); Varint((ulong)value); }

    internal void Bool(int field, bool value) { Tag(field, 0); Varint(value ? 1UL : 0UL); }

    internal void Double(int field, double value)
    {
        Tag(field, 1);
        Ensure(8);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(length), value);
        length += 8;
    }

    internal void String(int field, string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        Tag(field, 2); Varint((uint)count);
        if (Counting) { passed += count; return; }
        if (sizes is null || count <= buffer.Length - length)
        {
            Ensure(count);
            length += Encoding.UTF8.GetBytes(value, buffer.AsSpan(length));
            return;
        }
        Flush();
        var encoder = Encoding.UTF8.GetEncoder();
        var chars = value.AsSpan();
        var completed = false;
        while (!completed)
        {
            encoder.Convert(chars, buffer, flush: true, out var read, out length, out completed);
            chars = chars[read..];
            Flush();
        }
    }

    internal void Bytes(int field, byte[] value)
    {
        Tag(field, 2); Varint((uint)value.Length);
        if (Counting) { passed += value.Length; return; }
        if (sizes is null || value.Length <= buffer.Length - length)
        {
            Ensure(value.Length);
            value.CopyTo(buffer.AsSpan(length));
            length += value.Length;
            return;
        }
        Flush();
        sink!(value);
        passed += value.Length;
    }

    /// <summary>Starts a sub-message; pass the result to <see cref="EndMessage"/> after writing its body.</summary>
    internal int BeginMessage(int field)
    {
        Tag(field, 2);
        if (sizes is null) return length;
        if (sink is not null) { Varint((ulong)sizes[replayed++]); return 0; }
        sizes.Add(Length);
        return sizes.Count - 1;
    }

    /// <summary>Moves the message body behind its minimal length prefix, or records its size when measuring.</summary>
    internal void EndMessage(int start)
    {
        if (sink is not null) return;
        if (sizes is not null)
        {
            var measured = Length - sizes[start];
            sizes[start] = measured;
            passed += PrefixLength(measured);
            return;
        }
        var size = length - start;
        var prefix = PrefixLength(size);
        Ensure(prefix);
        buffer.AsSpan(start, size).CopyTo(buffer.AsSpan(start + prefix));
        length = start;
        Varint((uint)size);
        length += size;
    }
}

/// <summary>Reads protobuf fields: unknown fields are skipped, sub-messages may be length-delimited or groups.</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> data = data;
    private int position;
    private int end = data.Length;
    private int group;
    private int wire;

    internal int Field { get; private set; }

    /// <summary>Advances to the next field of the current message; false at its end.</summary>
    internal bool Next()
    {
        if (position == end)
        {
            if (group != 0) throw Corrupt("An RPC group was not closed.");
            return false;
        }
        var tag = Varint();
        if (tag > uint.MaxValue) throw new OverflowException("RPC field tag overflow.");
        Field = (int)(tag >> 3);
        wire = (int)(tag & 7);
        if (Field <= 0) throw Corrupt("Invalid RPC field number 0.");
        if (wire != 4) return true;
        if (Field != group) throw Corrupt("Unexpected end of an RPC group.");
        return false;
    }

    internal void Skip()
    {
        switch (wire)
        {
            case 0: Varint(); break;
            case 1: Take(8); break;
            case 2: Take(Length()); break;
            case 5: Take(4); break;
            case 3:
                var outer = (group, end);
                group = Field;
                while (Next()) Skip();
                (group, end) = outer;
                break;
            default: throw Corrupt("Invalid RPC wire type.");
        }
    }

    internal int Int32() => wire switch
    {
        0 => Int32Varint(),
        1 => checked((int)(long)Fixed64()),
        5 => (int)Fixed32(),
        _ => throw Corrupt("Invalid RPC wire type for an integer.")
    };

    internal long Int64() => wire switch
    {
        0 => (long)Varint(),
        1 => (long)Fixed64(),
        5 => (int)Fixed32(),
        _ => throw Corrupt("Invalid RPC wire type for an integer.")
    };

    internal bool Bool()
    {
        var value = wire switch
        {
            0 => UInt32Varint(),
            1 => checked((uint)Fixed64()),
            5 => Fixed32(),
            _ => throw Corrupt("Invalid RPC wire type for a boolean.")
        };
        return value switch { 0 => false, 1 => true, _ => throw Corrupt("Unexpected RPC boolean value.") };
    }

    /// <summary>A <c>double</c> is fixed64; a fixed32 <c>float</c> widens.</summary>
    internal double Double() => wire switch
    {
        1 => BitConverter.Int64BitsToDouble((long)Fixed64()),
        5 => BitConverter.Int32BitsToSingle((int)Fixed32()),
        _ => throw Corrupt("Invalid RPC wire type for a double.")
    };

    internal string String() => Encoding.UTF8.GetString(Delimited());

    /// <summary>Appends to <paramref name="existing"/>, as repeated occurrences of a bytes field concatenate.</summary>
    internal byte[] Bytes(byte[]? existing)
    {
        var bytes = Delimited();
        if (existing is not { Length: > 0 }) return bytes.ToArray();
        var joined = new byte[existing.Length + bytes.Length];
        existing.CopyTo(joined, 0);
        bytes.CopyTo(joined.AsSpan(existing.Length));
        return joined;
    }

    /// <summary>Enters a sub-message field; pass the result to <see cref="Leave"/> after reading it.</summary>
    internal (int Group, int End) Enter()
    {
        var outer = (group, end);
        if (wire == 3) { group = Field; return outer; }
        if (wire != 2) throw Corrupt("Invalid RPC wire type for a message.");
        var size = Length();
        if (size > end - position) throw Corrupt("An RPC sub-message runs past its end.");
        end = position + size;
        group = 0;
        return outer;
    }

    internal void Leave((int Group, int End) outer)
    {
        if (group == 0 && position != end) throw Corrupt("An RPC sub-message was not read to its end.");
        (group, end) = outer;
    }

    /// <summary>One map entry of string key 1 and string value 2; a missing key or value reads as empty.</summary>
    internal (string Key, string Value) StringEntry()
    {
        var outer = Enter();
        string key = "", value = "";
        while (Next())
        {
            if (Field == 1) key = String();
            else if (Field == 2) value = String();
            else Skip();
        }
        Leave(outer);
        return (key, value);
    }

    private ReadOnlySpan<byte> Delimited()
    {
        if (wire != 2) throw Corrupt("Invalid RPC wire type for a length-delimited field.");
        return Take(Length());
    }

    private int Length()
    {
        var size = UInt32Varint();
        if (size > int.MaxValue) throw Corrupt("An RPC field length overflows.");
        return (int)size;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > end - position) throw new EndOfStreamException("An RPC message ended inside a field.");
        var slice = data.Slice(position, count);
        position += count;
        return slice;
    }

    private uint Fixed32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    private ulong Fixed64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    private ulong Varint() => Varint(out _);

    private ulong Varint(out int count)
    {
        ulong value = 0;
        for (count = 0; count < 10; count++)
        {
            if (position == end) throw new EndOfStreamException("An RPC message ended inside a varint.");
            var next = data[position++];
            if (count == 9 && next > 1) throw new OverflowException("An RPC varint overflows 64 bits.");
            value |= (ulong)(next & 0x7F) << (7 * count);
            if (next < 0x80) { count++; return value; }
        }
        throw new OverflowException("An RPC varint overflows 64 bits.");
    }

    private uint UInt32Varint()
    {
        var value = Varint(out var count);
        if (count > 5 || value > uint.MaxValue) throw new OverflowException("An RPC varint overflows 32 bits.");
        return (uint)value;
    }

    /// <summary>Accepts the five-byte form and the ten-byte sign-extended form of a negative value.</summary>
    private int Int32Varint()
    {
        var value = Varint(out var count);
        if (count <= 5 && value <= uint.MaxValue) return (int)value;
        if (count == 10 && value >> 32 == uint.MaxValue) return (int)value;
        throw new OverflowException("An RPC varint overflows 32 bits.");
    }

    private static InvalidDataException Corrupt(string message) => new(message);
}