using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using Buffer = HarfBuzzSharp.Buffer;
using HarfBuzzFont = HarfBuzzSharp.Font;

namespace SharpRail.Scintilla;

/// <summary>
/// Shapes UTF-8 text with HarfBuzz. Each grapheme cluster takes one font: the editor's
/// typeface, the system emoji font for emoji presentation, or a system fallback.
/// </summary>
internal sealed class TextShaper(SKTypeface typeface) : IDisposable
{
    /// <summary>Glyph runs in visual order, and cumulative logical advances at each grapheme's end byte.</summary>
    internal sealed record Shaped(GlyphRun[] Runs, int[] GraphemeEnds, float[] Advances, float Width);
    /// <summary>Glyphs positioned from the text origin, with the byte offset of each glyph's cluster.</summary>
    internal sealed record GlyphRun(SKFont Font, ushort[] Glyphs, SKPoint[] Positions, int[] Clusters);

    private const int CachedTexts = 4096;
    // Two generations: a full current generation becomes the previous one, and a hit there moves back,
    // so a pass over the whole document (idle wrapping) does not evict the lines on screen.
    private readonly Dictionary<(SKFont Font, int Direction), (Dictionary<byte[], Shaped> Current, Dictionary<byte[], Shaped> Previous)> cache = [];
    private readonly Dictionary<SKTypeface, (Face Face, HarfBuzzFont Font)> harfBuzz = [];
    private readonly Dictionary<(SKFont Font, SKTypeface Face), SKFont> variants = [];
    private readonly SKFont coverage = new(typeface);
    // Most graphemes are one code point, whose font never changes.
    private readonly Dictionary<int, SKTypeface> runeFaces = [];
    private readonly Buffer buffer = new();
    // System fonts are immutable and shared by every editor on the UI thread; they are never disposed.
    private static readonly SKTypeface? Emoji = SKFontManager.Default.MatchCharacter(0x1F600);
    private static readonly SKFont? EmojiCoverage = Emoji is null ? null : new(Emoji);
    private static readonly Dictionary<(string Family, int Weight, SKFontStyleSlant Slant, int Codepoint), SKTypeface?> Fallbacks = [];
    private static readonly Dictionary<(string Family, int Weight, SKFontStyleSlant Slant), SKTypeface> Faces = [];

    /// <summary>Shapes text; direction is 0 for left-to-right, 1 for right-to-left, or -1 to follow its script.</summary>
    internal Shaped Shape(SKFont font, ReadOnlySpan<byte> text, int direction)
    {
        if (!cache.TryGetValue((font, direction), out var entries))
            cache.Add((font, direction), entries = (new(ByteComparer.Instance), new(ByteComparer.Instance)));
        var current = entries.Current.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (current.TryGetValue(text, out var shaped)) return shaped;
        if (entries.Previous.GetAlternateLookup<ReadOnlySpan<byte>>().TryGetValue(text, out shaped)) { }
        else
        {
            shaped = Build(font, text, direction);
        }
        if (entries.Current.Count >= CachedTexts)
        {
            entries = (entries.Previous, entries.Current);
            entries.Current.Clear();
            cache[(font, direction)] = entries;
            current = entries.Current.GetAlternateLookup<ReadOnlySpan<byte>>();
        }
        current.TryAdd(text, shaped);
        return shaped;
    }

    private Shaped Build(SKFont font, ReadOnlySpan<byte> utf8, int direction)
    {
        // Decode once, remembering each UTF-16 unit's byte offset; invalid bytes become U+FFFD.
        var chars = new char[utf8.Length];
        var byteAt = new int[utf8.Length + 1];
        var length = 0;
        for (var offset = 0; offset < utf8.Length;)
        {
            Rune.DecodeFromUtf8(utf8[offset..], out var rune, out var consumed);
            var written = rune.EncodeToUtf16(chars.AsSpan(length));
            for (var i = 0; i < written; i++) byteAt[length + i] = offset;
            length += written; offset += Math.Max(consumed, 1);
        }
        byteAt[length] = utf8.Length;

        var ends = new List<int>();
        var graphemeFaces = new List<SKTypeface>();
        for (var i = 0; i < length;)
        {
            // Below U+0300 nothing extends a cluster, so such a character before another is a grapheme of its own; CR LF is the exception.
            var size = chars[i] < 0x300 && chars[i] != '\r' && (i + 1 == length || chars[i + 1] < 0x300) ? 1
                : StringInfo.GetNextTextElementLength(chars.AsSpan(i, length - i));
            graphemeFaces.Add(FaceFor(chars.AsSpan(i, size)));
            ends.Add(byteAt[i + size]);
            i += size;
        }

        // Consecutive graphemes in one font shape together, with the whole text as context.
        var runs = new List<(GlyphRun Run, float Advance)>();
        var advances = new float[utf8.Length];
        var clusterStart = new bool[utf8.Length];
        for (int first = 0, next; first < ends.Count; first = next)
        {
            next = first + 1;
            while (next < ends.Count && graphemeFaces[next] == graphemeFaces[first]) next++;
            var start = first == 0 ? 0 : ends[first - 1];
            runs.Add(ShapeRun(font, graphemeFaces[first], utf8, start, ends[next - 1] - start, direction, advances, clusterStart));
        }
        if (direction == 1) runs.Reverse();

        var width = 0f;
        foreach (var (run, advance) in runs)
        {
            for (var i = 0; i < run.Positions.Length; i++) run.Positions[i].X += width;
            width += advance;
        }
        return new(runs.Select(run => run.Run).ToArray(), ends.ToArray(), GraphemeAdvances(ends, advances, clusterStart), width);
    }

    private (GlyphRun Run, float Advance) ShapeRun(SKFont font, SKTypeface face, ReadOnlySpan<byte> utf8, int start, int length, int direction,
        float[] advances, bool[] clusterStart)
    {
        var (harfBuzzFace, harfBuzzFont) = HarfBuzz(face);
        buffer.ClearContents();
        buffer.AddUtf8(utf8, start, length);
        buffer.GuessSegmentProperties();
        if (direction >= 0) buffer.Direction = direction == 1 ? Direction.RightToLeft : Direction.LeftToRight;
        harfBuzzFont.Shape(buffer);
        var infos = buffer.GetGlyphInfoSpan();
        var positions = buffer.GetGlyphPositionSpan();
        var scale = font.Size / harfBuzzFace.UnitsPerEm;
        var run = new GlyphRun(Variant(font, face), new ushort[infos.Length], new SKPoint[infos.Length], new int[infos.Length]);
        var pen = 0f;
        for (var i = 0; i < infos.Length; i++)
        {
            var cluster = (int)infos[i].Cluster;
            run.Glyphs[i] = (ushort)infos[i].Codepoint;
            run.Clusters[i] = cluster;
            run.Positions[i] = new(pen + positions[i].XOffset * scale, -positions[i].YOffset * scale);
            pen += positions[i].XAdvance * scale;
            advances[cluster] += positions[i].XAdvance * scale;
            clusterStart[cluster] = true;
        }
        return (run, pen);
    }

    // A HarfBuzz cluster spanning several graphemes, like a ligature, shares its advance equally
    // between them; a grapheme spanning several clusters sums them.
    private static float[] GraphemeAdvances(List<int> ends, float[] advances, bool[] clusterStart)
    {
        var graphemeOf = new int[advances.Length];
        for (int grapheme = 0, start = 0; grapheme < ends.Count; start = ends[grapheme++])
            Array.Fill(graphemeOf, grapheme, start, ends[grapheme] - start);
        var widths = new float[ends.Count];
        for (var start = 0; start < advances.Length;)
        {
            var end = start + 1;
            while (end < advances.Length && !clusterStart[end]) end++;
            var (first, last) = (graphemeOf[start], graphemeOf[end - 1]);
            for (var grapheme = first; grapheme <= last; grapheme++) widths[grapheme] += advances[start] / (last - first + 1);
            start = end;
        }
        var total = 0f;
        for (var i = 0; i < widths.Length; i++) widths[i] = total += widths[i];
        return widths;
    }

    private SKTypeface FaceFor(ReadOnlySpan<char> grapheme)
    {
        Rune.DecodeFromUtf16(grapheme, out var first, out var used);
        if (used != grapheme.Length) return MatchFace(grapheme, first);
        if (runeFaces.TryGetValue(first.Value, out var face)) return face;
        runeFaces.Add(first.Value, face = MatchFace(grapheme, first));
        return face;
    }

    private SKTypeface MatchFace(ReadOnlySpan<char> grapheme, Rune first)
    {
        if (EmojiCoverage is not null && IsEmoji(grapheme) && EmojiCoverage.ContainsGlyph(first.Value)) return Emoji!;
        foreach (var rune in grapheme.EnumerateRunes())
            if (!IsIgnorable(rune.Value) && !coverage.ContainsGlyph(rune.Value))
                return Fallback(rune.Value) ?? typeface;
        return typeface;
    }

    private SKTypeface? Fallback(int codepoint)
    {
        var key = (typeface.FamilyName, typeface.FontWeight, typeface.FontSlant, codepoint);
        if (Fallbacks.TryGetValue(key, out var face)) return face;
        face = SKFontManager.Default.MatchCharacter(typeface.FamilyName, typeface.FontStyle, null, codepoint);
        // The font manager returns a new instance per query; share one per font so runs merge.
        if (face is not null) face = Faces.TryAdd((face.FamilyName, face.FontWeight, face.FontSlant), face) ? face : Faces[(face.FamilyName, face.FontWeight, face.FontSlant)];
        Fallbacks.Add(key, face);
        return face;
    }

    // Explicit emoji presentation, emoji sequences and the pictographic planes use the emoji font.
    private static bool IsEmoji(ReadOnlySpan<char> grapheme)
    {
        var emoji = false;
        var index = 0;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == 0xFE0E) return false;
            emoji |= value is 0xFE0F or 0x200D or 0x20E3 or >= 0x1F1E6 and <= 0x1F1FF or >= 0x1F3FB and <= 0x1F3FF or >= 0xE0020 and <= 0xE007F
                || index == 0 && value is >= 0x1F000 and <= 0x1FAFF;
            index++;
        }
        return emoji;
    }

    private static bool IsIgnorable(int codepoint) => codepoint is 0x00AD or 0x034F or >= 0x200B and <= 0x200F or >= 0x202A and <= 0x202E
        or >= 0x2060 and <= 0x206F or >= 0xFE00 and <= 0xFE0F or 0xFEFF or >= 0xE0000 and <= 0xE0FFF;

    private SKFont Variant(SKFont font, SKTypeface face)
    {
        if (face == typeface) return font;
        if (variants.TryGetValue((font, face), out var variant)) return variant;
        variant = new SKFont(face, font.Size) { Subpixel = true, Edging = SKFontEdging.Antialias, Embolden = font.Embolden, SkewX = font.SkewX };
        variants.Add((font, face), variant);
        return variant;
    }

    private (Face Face, HarfBuzzFont Font) HarfBuzz(SKTypeface face)
    {
        if (harfBuzz.TryGetValue(face, out var entry)) return entry;
        // HarfBuzz reads only the tables it shapes with, so large colour emoji fonts stay cheap.
        var harfBuzzFace = new Face((_, tag) => Table(face, tag));
        var harfBuzzFont = new HarfBuzzFont(harfBuzzFace);
        harfBuzzFont.SetScale(harfBuzzFace.UnitsPerEm, harfBuzzFace.UnitsPerEm);
        harfBuzz.Add(face, entry = (harfBuzzFace, harfBuzzFont));
        return entry;
    }

    private static Blob? Table(SKTypeface face, Tag tag)
    {
        if (!face.TryGetTableData(tag, out var data)) return null;
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        return new Blob(handle.AddrOfPinnedObject(), data.Length, MemoryMode.ReadOnly, () => handle.Free());
    }

    public void Dispose()
    {
        foreach (var (face, font) in harfBuzz.Values) { font.Dispose(); face.Dispose(); }
        foreach (var variant in variants.Values) variant.Dispose();
        coverage.Dispose(); buffer.Dispose();
        harfBuzz.Clear(); runeFaces.Clear(); variants.Clear(); cache.Clear();
    }

    private sealed class ByteComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        internal static readonly ByteComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] bytes) => Hash(bytes);
        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);
        public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);
        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
        private static int Hash(ReadOnlySpan<byte> bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }
}
