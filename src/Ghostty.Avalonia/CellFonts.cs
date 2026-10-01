using System.Text;
using HarfBuzzSharp;
using SkiaSharp;

namespace Ghostty.Avalonia;

/// <summary>The glyphs of one grapheme, positioned relative to its first cell's baseline origin.</summary>
internal sealed record CellGlyphs(SKFont Font, ushort[] Glyphs, SKPoint[] Positions);

/// <summary>
/// The grid font in four styles, its cell metrics, and grapheme lookup with system fallback. Cells are
/// rounded to whole device pixels so adjacent backgrounds and box drawing meet without seams.
/// </summary>
internal sealed class CellFonts : IDisposable
{
    // Font units per em that shaped positions are expressed in.
    private const int ShapeScale = 1 << 12;
    private readonly SKFont[] fonts = new SKFont[4];
    private readonly Dictionary<(string Text, int Style, int Span), CellGlyphs?> glyphs = [];
    private readonly Dictionary<(string Text, bool Bold, bool Italic, int Span, bool Run), SKTextBlob?> blobs = [];
    private readonly Dictionary<(string Family, int Style), SKFont> fallbacks = [];
    private readonly List<SKFont> shrunk = [];
    private readonly Dictionary<SKTypeface, HarfBuzzSharp.Font?> shapers = [];
    private readonly string family;
    private readonly SKTypeface? supplied;

    /// <param name="typeface">A caller-owned face for every style, with bold and italic synthesized; null matches <paramref name="family"/>.</param>
    internal CellFonts(SKTypeface? typeface, string family, double size, double scale)
    {
        this.family = family;
        supplied = typeface;
        for (var style = 0; style < 4; style++)
        {
            bool bold = (style & 1) != 0, italic = (style & 2) != 0;
            fonts[style] = typeface is not null ? Font(typeface, (float)size, bold, italic) : Matched(bold, italic);
        }
        var metrics = fonts[0].Metrics;
        CellWidth = Pixels(fonts[0].MeasureText("M"), scale);
        CellHeight = Pixels(metrics.Descent - metrics.Ascent + metrics.Leading, scale);
        // Centre the font's line box in the rounded cell, on a device pixel.
        Baseline = (float)(Math.Round(((CellHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent) * scale) / scale);
        LineThickness = Math.Max(1 / (float)scale, metrics.UnderlineThickness ?? (float)size / 14);
        UnderlinePosition = Baseline + Math.Max(LineThickness, metrics.UnderlinePosition ?? (float)size / 10);
        StrikePosition = Baseline + (metrics.StrikeoutPosition ?? metrics.XHeight / -2);
        static float Pixels(float value, double scale) => (float)(Math.Ceiling(value * scale - 0.01) / scale);

        SKFont Matched(bool bold, bool italic)
        {
            var face = SKFontManager.Default.MatchFamily(family, new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright))
                ?? SKFontManager.Default.MatchFamily("monospace") ?? SKTypeface.Default;
            return Font(face, (float)size, bold && face.FontWeight < (int)SKFontStyleWeight.SemiBold, italic && face.FontSlant == SKFontStyleSlant.Upright);
        }
    }

    internal SKFont Regular => fonts[0];
    internal float CellWidth { get; }
    internal float CellHeight { get; }
    internal float Baseline { get; }
    internal float UnderlinePosition { get; }
    internal float LineThickness { get; }
    internal float StrikePosition { get; }

    // Cache native draw objects as well as glyph positions, following RoyalTerminal's text caches.
    // See README.md for pinned Royal Apps source links and MIT attribution.
    internal SKTextBlob? Blob(string text, bool bold, bool italic, int span, bool asciiRun = false)
    {
        var key = (text, bold, italic, span, asciiRun);
        if (blobs.TryGetValue(key, out var cached)) return cached;
        if (blobs.Count >= 4096)
        {
            foreach (var blob in blobs.Values) blob?.Dispose();
            blobs.Clear();
        }
        var parts = asciiRun
            ? text.Select(character => Glyphs(character.ToString(), bold, italic, 1)).ToArray()
            : [Glyphs(text, bold, italic, span)];
        using var builder = new SKTextBlobBuilder();
        for (var i = 0; i < parts.Length;)
        {
            if (parts[i] is not { } first) { i++; continue; }
            var end = i + 1;
            var count = first.Glyphs.Length;
            while (end < parts.Length && parts[end] is { } next && next.Font == first.Font)
            { count += next.Glyphs.Length; end++; }
            var run = builder.AllocatePositionedRun(first.Font, count);
            var offset = 0;
            for (; i < end; i++)
            {
                var part = parts[i]!;
                part.Glyphs.CopyTo(run.Glyphs[offset..]);
                for (var g = 0; g < part.Positions.Length; g++)
                    run.Positions[offset + g] = new SKPoint(part.Positions[g].X + i * CellWidth, part.Positions[g].Y);
                offset += part.Glyphs.Length;
            }
        }
        return blobs[key] = builder.Build();
    }

    /// <summary>The glyphs that draw a grapheme across <paramref name="span"/> cells, or null when no font has it.</summary>
    internal CellGlyphs? Glyphs(string text, bool bold, bool italic, int span)
    {
        var style = (bold ? 1 : 0) | (italic ? 2 : 0);
        if (glyphs.TryGetValue((text, style, span), out var found)) return found;
        var font = FontFor(text, style);
        var shaped = Shape(font, text);
        // Fallback faces (emoji, CJK) can be wider than the grid; shrink them into their cells.
        var room = CellWidth * span;
        if (shaped is { Width: var width } && width > room * 1.05f && font != fonts[style])
        {
            font = Font(font.Typeface, font.Size * room / width, font.Embolden, font.SkewX != 0);
            shrunk.Add(font);
            shaped = Shape(font, text);
        }
        CellGlyphs? result = shaped is { } run
            ? new(font, run.Ids, [.. run.Positions.Select(point => new SKPoint(point.X + Math.Max(0, (room - run.Width) / 2), point.Y))])
            : null;
        glyphs[(text, style, span)] = result;
        return result;
    }

    private SKFont FontFor(string text, int style)
    {
        var primary = fonts[style];
        var codepoint = char.ConvertToUtf32(text, 0);
        // A presentation selector or joiner asks for the colour emoji face even when the grid font has the base glyph.
        if (!text.Contains('️') && !text.Contains('‍') && primary.ContainsGlyph(codepoint)) return primary;
        var typeface = SKFontManager.Default.MatchCharacter(family, primary.Typeface.FontStyle, null, codepoint);
        if (typeface is null) return primary;
        if (fallbacks.TryGetValue((typeface.FamilyName, style), out var cached)) { typeface.Dispose(); return cached; }
        return fallbacks[(typeface.FamilyName, style)] = Font(typeface, primary.Size, primary.Embolden, primary.SkewX != 0);
    }

    private (ushort[] Ids, SKPoint[] Positions, float Width)? Shape(SKFont font, string text)
    {
        // A single code point maps straight to a glyph; clusters (combining marks, ZWJ emoji) need shaping.
        if (text.Length == 1 || text.Length == 2 && char.IsSurrogatePair(text, 0))
        {
            var glyph = font.GetGlyph(char.ConvertToUtf32(text, 0));
            return glyph == 0 ? null : ([glyph], [default], font.GetGlyphWidths([glyph])[0]);
        }
        if (Shaper(font.Typeface) is not { } shaper) return null;
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        shaper.Shape(buffer);
        var infos = buffer.GlyphInfos; var positions = buffer.GlyphPositions;
        if (infos.Length == 0 || infos.Any(info => info.Codepoint == 0)) return null;
        var unit = font.Size / ShapeScale;
        var ids = new ushort[infos.Length]; var points = new SKPoint[infos.Length];
        float x = 0;
        for (var i = 0; i < infos.Length; i++)
        {
            ids[i] = (ushort)infos[i].Codepoint;
            points[i] = new SKPoint(x + positions[i].XOffset * unit, -positions[i].YOffset * unit);
            x += positions[i].XAdvance * unit;
        }
        return (ids, points, x);
    }

    private unsafe HarfBuzzSharp.Font? Shaper(SKTypeface typeface)
    {
        if (shapers.TryGetValue(typeface, out var shaper)) return shaper;
        using var stream = typeface.OpenStream(out var index);
        if (stream is not null)
        {
            var data = new byte[stream.Length];
            stream.Read(data, data.Length);
            // HarfBuzz retains the font bytes after this managed buffer is unpinned.
            using var blob = CopyFont(data);
            using var face = new Face(blob, index);
            shaper = new HarfBuzzSharp.Font(face);
            shaper.SetScale(ShapeScale, ShapeScale);
            shaper.SetFunctionsOpenType();
        }
        return shapers[typeface] = shaper;

        static Blob CopyFont(byte[] data)
        {
            fixed (byte* bytes = data)
                return new Blob((nint)bytes, data.Length, MemoryMode.Duplicate);
        }
    }

    private static SKFont Font(SKTypeface typeface, float size, bool embolden, bool slant) => new(typeface, size)
    {
        Subpixel = true,
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.Slight,
        // Synthesize styles the family lacks.
        Embolden = embolden,
        SkewX = slant ? -0.2f : 0
    };

    public void Dispose()
    {
        foreach (var blob in blobs.Values) blob?.Dispose();
        foreach (var shaper in shapers.Values) shaper?.Dispose();
        foreach (var font in shrunk) font.Dispose();
        foreach (var font in fallbacks.Values.Concat(fonts))
        {
            if (font.Typeface != SKTypeface.Default && font.Typeface != supplied) font.Typeface.Dispose();
            font.Dispose();
        }
    }

    internal static string Grapheme(ReadOnlySpan<uint> codepoints)
    {
        var builder = new StringBuilder(codepoints.Length * 2);
        foreach (var codepoint in codepoints)
            if (Rune.TryCreate(codepoint, out var rune)) builder.Append(rune.ToString());
        return builder.ToString();
    }
}
