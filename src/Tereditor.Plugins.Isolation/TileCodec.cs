using System.Buffers;
using System.Buffers.Binary;

namespace SharpRail.Plugins.Isolation;

/// <summary>How one tile's bytes are written. Zero is never sent.</summary>
public enum TileCodecKind { None, Solid, Qoi }

/// <summary>
/// Lossless tile compression for plugin surfaces: premultiplied BGRA pixels, 4 bytes each, rows of a larger buffer. A
/// tile of one colour is <see cref="TileCodecKind.Solid"/> (its 4 bytes); any other is QOI without its header, in row
/// order. Decoding writes exactly the tile's pixels and refuses data that is short, long or malformed.
/// </summary>
public static class TileCodec
{
    /// <summary>Encodes the tile at (<paramref name="x"/>, <paramref name="y"/>) and answers the codec it used.</summary>
    public static TileCodecKind Encode(ReadOnlySpan<byte> pixels, int stride, int x, int y, int width, int height, IBufferWriter<byte> output)
    {
        var first = BinaryPrimitives.ReadUInt32LittleEndian(pixels.Slice(y * stride + x * 4, 4));
        if (Solid(pixels, stride, x, y, width, height, first))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(4), first);
            output.Advance(4);
            return TileCodecKind.Solid;
        }
        Qoi.Encode(pixels, stride, x, y, width, height, output);
        return TileCodecKind.Qoi;
    }

    /// <summary>Decodes a tile into its place in <paramref name="pixels"/>; throws <see cref="InvalidDataException"/> on bad data.</summary>
    public static void Decode(TileCodecKind codec, ReadOnlySpan<byte> data, Span<byte> pixels, int stride, int x, int y, int width, int height)
    {
        switch (codec)
        {
            case TileCodecKind.Solid:
                if (data.Length != 4) throw new InvalidDataException("A solid tile is 4 bytes.");
                var value = BinaryPrimitives.ReadUInt32LittleEndian(data);
                for (var row = 0; row < height; row++)
                    System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.Slice((y + row) * stride + x * 4, width * 4)).Fill(value);
                return;
            case TileCodecKind.Qoi:
                Qoi.Decode(data, pixels, stride, x, y, width, height);
                return;
            default:
                throw new InvalidDataException($"Tile codec {(int)codec} is not known.");
        }
    }

    private static bool Solid(ReadOnlySpan<byte> pixels, int stride, int x, int y, int width, int height, uint value)
    {
        for (var row = 0; row < height; row++)
            if (System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.Slice((y + row) * stride + x * 4, width * 4)).ContainsAnyExcept(value)) return false;
        return true;
    }

    /// <summary>The QOI operations without its header, over the tile's pixels in row order.</summary>
    private static class Qoi
    {
        private const byte Index = 0x00, Diff = 0x40, Luma = 0x80, Run = 0xc0, Rgb = 0xfe, Rgba = 0xff, Mask = 0xc0;

        private static int Hash(uint pixel)
        {
            int c0 = (byte)pixel, c1 = (byte)(pixel >> 8), c2 = (byte)(pixel >> 16), c3 = (byte)(pixel >> 24);
            return (c2 * 3 + c1 * 5 + c0 * 7 + c3 * 11) % 64;
        }

        public static void Encode(ReadOnlySpan<byte> pixels, int stride, int x, int y, int width, int height, IBufferWriter<byte> output)
        {
            Span<uint> seen = stackalloc uint[64];
            seen.Clear();
            var span = output.GetSpan(width * height * 5 + 1);
            var at = 0;
            var previous = 0xff000000u;
            var run = 0;
            for (var row = 0; row < height; row++)
            {
                var line = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.Slice((y + row) * stride + x * 4, width * 4));
                foreach (var pixel in line)
                {
                    if (pixel == previous)
                    {
                        if (++run == 62) { span[at++] = (byte)(Run | (run - 1)); run = 0; }
                        continue;
                    }
                    if (run > 0) { span[at++] = (byte)(Run | (run - 1)); run = 0; }
                    var slot = Hash(pixel);
                    if (seen[slot] == pixel) span[at++] = (byte)(Index | slot);
                    else
                    {
                        seen[slot] = pixel;
                        if ((pixel >> 24) == (previous >> 24))
                        {
                            int dr = (sbyte)(byte)((pixel >> 16) - (previous >> 16)), dg = (sbyte)(byte)((pixel >> 8) - (previous >> 8)), db = (sbyte)(byte)(pixel - previous);
                            int drdg = dr - dg, dbdg = db - dg;
                            if (dr is >= -2 and <= 1 && dg is >= -2 and <= 1 && db is >= -2 and <= 1) span[at++] = (byte)(Diff | (dr + 2) << 4 | (dg + 2) << 2 | (db + 2));
                            else if (dg is >= -32 and <= 31 && drdg is >= -8 and <= 7 && dbdg is >= -8 and <= 7)
                            {
                                span[at++] = (byte)(Luma | (dg + 32));
                                span[at++] = (byte)((drdg + 8) << 4 | (dbdg + 8));
                            }
                            else
                            {
                                span[at++] = Rgb;
                                span[at++] = (byte)(pixel >> 16);
                                span[at++] = (byte)(pixel >> 8);
                                span[at++] = (byte)pixel;
                            }
                        }
                        else
                        {
                            span[at++] = Rgba;
                            span[at++] = (byte)(pixel >> 16);
                            span[at++] = (byte)(pixel >> 8);
                            span[at++] = (byte)pixel;
                            span[at++] = (byte)(pixel >> 24);
                        }
                    }
                    previous = pixel;
                }
            }
            if (run > 0) span[at++] = (byte)(Run | (run - 1));
            output.Advance(at);
        }

        public static void Decode(ReadOnlySpan<byte> data, Span<byte> pixels, int stride, int x, int y, int width, int height)
        {
            Span<uint> seen = stackalloc uint[64];
            seen.Clear();
            var pixel = 0xff000000u;
            var at = 0;
            var run = 0;
            for (var row = 0; row < height; row++)
            {
                var line = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.Slice((y + row) * stride + x * 4, width * 4));
                for (var column = 0; column < width; column++)
                {
                    if (run > 0) run--;
                    else
                    {
                        var op = Next(data, ref at);
                        if (op == Rgb) pixel = (pixel & 0xff000000u) | (uint)Next(data, ref at) << 16 | (uint)Next(data, ref at) << 8 | Next(data, ref at);
                        else if (op == Rgba) pixel = (uint)Next(data, ref at) << 16 | (uint)Next(data, ref at) << 8 | Next(data, ref at) | (uint)Next(data, ref at) << 24;
                        else
                            switch (op & Mask)
                            {
                                case Index: pixel = seen[op & 0x3f]; break;
                                case Diff:
                                    pixel = Add(pixel, ((op >> 4) & 3) - 2, ((op >> 2) & 3) - 2, (op & 3) - 2);
                                    break;
                                case Luma:
                                    var dg = (op & 0x3f) - 32;
                                    var second = Next(data, ref at);
                                    pixel = Add(pixel, dg - 8 + (second >> 4), dg, dg - 8 + (second & 0x0f));
                                    break;
                                default: run = op & 0x3f; break;
                            }
                        seen[Hash(pixel)] = pixel;
                    }
                    line[column] = pixel;
                }
            }
            if (run > 0 || at != data.Length) throw new InvalidDataException("A tile holds more than its pixels.");
        }

        private static byte Next(ReadOnlySpan<byte> data, ref int at) => at < data.Length ? data[at++] : throw new InvalidDataException("A tile holds fewer than its pixels.");

        private static uint Add(uint pixel, int dr, int dg, int db) =>
            (pixel & 0xff000000u) | (uint)(byte)((pixel >> 16) + dr) << 16 | (uint)(byte)((pixel >> 8) + dg) << 8 | (byte)(pixel + db);
    }
}