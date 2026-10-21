using System.Buffers;
using System.Text;

using SharpRail.Scintilla.Core;

using SkiaSharp;

namespace SharpRail.Scintilla.View;

/// <summary>
/// Lays out document lines (wrapping into sub-lines) and paints the editor onto an SKCanvas
/// (upstream src/EditView.cxx, MarginView.cxx, PositionCache.cxx LineLayout, trimmed). The editor drives it.
/// </summary>
internal sealed partial class EditView(LineShaper shaper)
{
    // Representation blobs are their mnemonic plus padding, drawn as a rounded box.
    private const float RepresentationPadding = 4;

    private static readonly string[] ControlMnemonics =
    [
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US",
    ];

    /// <summary>A document line's text (EOL excluded), its wrap sub-lines and their shapes.</summary>
    private sealed class LineLayout
    {
        public required byte[] Text;
        public required byte[] Styles;
        public required ShapeItem[] Items;
        public required float WrapWidth;
        /// <summary>Sub-line start offsets, plus the text length as a final entry.</summary>
        public required int[] Starts;
        public required LineShape[] Shapes;
        public int Count => Shapes.Length;
        public int SubLineOf(int offset)
        {
            for (var i = Count - 1; i > 0; i--)
                if (offset >= Starts[i]) return i;
            return 0;
        }
    }

    private readonly Dictionary<int, LineLayout> layouts = [];

    public LineShaper Shaper => shaper;

    /// <summary>Drops cached layouts from <paramref name="line"/> on, after edits, style or width changes.</summary>
    public void Invalidate(int line = 0)
    {
        if (line <= 0) { layouts.Clear(); return; }
        foreach (var key in layouts.Keys.Where(k => k >= line).ToList()) layouts.Remove(key);
    }

    /// <summary>Display rows a document line takes at <paramref name="wrapWidth"/> (text-area pixels; infinity = no wrap).</summary>
    /// <remarks>
    /// A printable ASCII line whose widest-glyph bound fits is one row without shaping. Otherwise the line is shaped,
    /// and cached only when <paramref name="cache"/> is set, so background wrapping keeps no layouts off screen.
    /// </remarks>
    public int WrapLine(Document document, ViewStyle style, int line, float wrapWidth, bool cache = true)
    {
        if (layouts.TryGetValue(line, out var cached) && cached.WrapWidth.Equals(wrapWidth)) return cached.Count;
        if (!float.IsFinite(wrapWidth) || FitsUnshaped(document, style, line, wrapWidth)) return 1;
        return cache ? Layout(document, style, line, wrapWidth).Count : Break(document, style, line, wrapWidth).Starts.Count - 1;
    }

    // Slack for kerning across the space-separated pieces a single-row estimate measures apart.
    private const float WordSlack = 2;
    private const int WordWidthLimit = 65536;
    private readonly Dictionary<byte[], float> wordWidths = new(WordComparer.Instance);
    private FontSpec? wordFont;
    private readonly float[] asciiWidths = new float[0x80];

    /// <summary>
    /// Whether a line fits on one row without shaping it whole: a printable ASCII bound first, then, for a single-font
    /// line, the sum of its words' widths, ASCII ones per character and others from a cache (upstream PositionCache
    /// measures runs alike). Shaping can differ from the sum by kerning, so the sum must fit with slack.
    /// </summary>
    private bool FitsUnshaped(Document document, ViewStyle style, int line, float wrapWidth)
    {
        if (style.MaxAsciiAdvance <= 0) return false;
        int start = document.LineStart(line), end = document.LineEnd(line);
        var tab = TabPixels(style);
        var width = 0f;
        var ascii = true;
        for (var position = start; position < end && ascii; position++)
        {
            var b = document.CharAt(position);
            if (b == (byte)'\t') width += tab;
            else if (b is >= 0x20 and < 0x7F) width += style.MaxAsciiAdvance;
            else ascii = false;
            if (width > wrapWidth) break;
        }
        if (ascii && width <= wrapWidth) return true;
        return LineFont(document, style, start, end) is { } font && SumOfWords(document, font, start, end, tab, wrapWidth - WordSlack) <= wrapWidth - WordSlack;
    }

    /// <summary>The font of every style on a line, or null when the line mixes fonts.</summary>
    private static FontSpec? LineFont(Document document, ViewStyle style, int start, int end)
    {
        var length = end - start;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            var styles = rented.AsSpan(0, length);
            document.GetStyleRange(styles, start, length);
            var font = style.Styles[length > 0 ? styles[0] : ViewStyle.Default].FontSpec;
            foreach (var s in styles)
                if (style.Styles[s].FontSpec != font) return null;
            return font;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private float SumOfWords(Document document, FontSpec font, int start, int end, float tab, float limit)
    {
        if (wordFont != font || wordWidths.Count > WordWidthLimit)
        {
            wordWidths.Clear();
            wordFont = font;
            Span<byte> glyph = stackalloc byte[1];
            for (glyph[0] = 0x20; glyph[0] < 0x7F; glyph[0]++) asciiWidths[glyph[0]] = shaper.Measure(font, glyph);
        }
        var lookup = wordWidths.GetAlternateLookup<ReadOnlySpan<byte>>();
        var length = end - start;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            var text = rented.AsSpan(0, length);
            document.GetCharRange(text, start, length);
            var width = 0f;
            var word = 0;
            for (var i = 0; i <= length && width <= limit; i++)
            {
                if (i < length && text[i] is not ((byte)' ' or (byte)'\t')) continue;
                if (i > word)
                {
                    var piece = text[word..i];
                    if (piece.ContainsAnyInRange((byte)0, (byte)0x1F) || piece.IndexOf((byte)0x7F) >= 0) return float.PositiveInfinity;
                    if (!piece.ContainsAnyInRange((byte)0x80, (byte)0xFF))
                        foreach (var b in piece) width += asciiWidths[b];
                    else
                    {
                        if (!lookup.TryGetValue(piece, out var measured)) lookup[piece] = measured = shaper.Measure(font, piece);
                        width += measured;
                    }
                }
                if (i < length) width += text[i] == (byte)'\t' ? tab : asciiWidths[' '];
                word = i + 1;
            }
            return width;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private sealed class WordComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly WordComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);
        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);
        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            var hash = new HashCode();
            hash.AddBytes(alternate);
            return hash.ToHashCode();
        }
        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }

    /// <summary>Drops cached layouts outside the painted lines and a margin around them, bounding memory on large documents.</summary>
    private void Trim(int first, int last)
    {
        const int margin = 256;
        if (layouts.Count <= last - first + 2 * margin) return;
        foreach (var key in layouts.Keys.Where(k => k < first - margin || k > last + margin).ToList()) layouts.Remove(key);
    }

    /// <summary>Width in pixels of the widest row of a line, for scroll-width tracking.</summary>
    public float LineWidth(Document document, ViewStyle style, int line)
    {
        var layout = layouts.TryGetValue(line, out var cached) ? cached : Layout(document, style, line, float.PositiveInfinity);
        var width = 0f;
        foreach (var shape in layout.Shapes) width = Math.Max(width, shape.Width);
        return width;
    }

    /// <summary>Text-area point (x before scroll, y relative to the line's first row) of a position.</summary>
    public SKPoint LocationInLine(Document document, ViewStyle style, int position, float wrapWidth)
    {
        var line = document.LineFromPosition(position);
        var layout = Layout(document, style, line, wrapWidth);
        var offset = Math.Clamp(position - document.LineStart(line), 0, layout.Text.Length);
        var sub = layout.SubLineOf(offset);
        return new(layout.Shapes[sub].XFromPosition(offset - layout.Starts[sub]), sub * Metrics(style).LineHeight);
    }

    /// <summary>Position in a document line nearest x on a given sub-row; characterPosition picks the cluster start, not nearest edge.</summary>
    public int PositionInLine(Document document, ViewStyle style, int line, int subLine, float x, bool characterPosition, float wrapWidth)
    {
        var layout = Layout(document, style, line, wrapWidth);
        var sub = Math.Clamp(subLine, 0, layout.Count - 1);
        var offset = layout.Starts[sub] + layout.Shapes[sub].PositionFromX(x, characterPosition);
        // The wrap point belongs to the next row, so a hit past a wrapped row's end stays before its last character.
        if (sub < layout.Count - 1 && offset >= layout.Starts[sub + 1])
            offset = layout.Starts[sub] + LastClusterStart(layout.Shapes[sub]);
        return document.LineStart(line) + offset;
    }

    private static int LastClusterStart(LineShape shape)
    {
        var start = 0;
        foreach (var c in shape.Clusters) start = Math.Max(start, c.Start);
        return start;
    }

    /// <summary>Fills the derived metrics when the editor has not (zero line height or space width).</summary>
    private ViewStyle Metrics(ViewStyle style)
    {
        if (style.LineHeight > 0 && style.SpaceWidth > 0) return style;
        var font = style.Styles[ViewStyle.Default].FontSpec;
        (style.Ascent, style.Descent, style.LineHeight) = shaper.Metrics(font);
        style.SpaceWidth = shaper.Measure(font, " "u8);
        return style;
    }

    private float TabPixels(ViewStyle style) =>
        style.TabPixels > 0 ? style.TabPixels : Metrics(style).SpaceWidth * Math.Max(1, style.TabWidth);

    private LineLayout Layout(Document document, ViewStyle style, int line, float wrapWidth, bool cache = true)
    {
        if (layouts.TryGetValue(line, out var cached) && cached.WrapWidth.Equals(wrapWidth)) return cached;
        var (text, styles, items, whole, starts) = Break(document, style, line, wrapWidth);
        var tab = TabPixels(style);
        var shapes = new LineShape[starts.Count - 1];
        for (var i = 0; i < shapes.Length; i++)
            shapes[i] = shapes.Length == 1 ? whole
                : shaper.Shape(text.AsSpan(starts[i], starts[i + 1] - starts[i]), Clip(items, starts[i], starts[i + 1]), tab);
        var layout = new LineLayout { Text = text, Styles = styles, Items = items, WrapWidth = wrapWidth, Starts = [.. starts], Shapes = shapes };
        if (cache) layouts[line] = layout;
        return layout;
    }

    /// <summary>Shapes a whole line once and finds its wrap points, without shaping the rows.</summary>
    private (byte[] Text, byte[] Styles, ShapeItem[] Items, LineShape Whole, List<int> Starts) Break(Document document, ViewStyle style, int line, float wrapWidth)
    {
        var start = document.LineStart(line);
        var length = Math.Max(0, document.LineEnd(line) - start);
        var text = new byte[length];
        var styles = new byte[length];
        document.GetCharRange(text, start, length);
        document.GetStyleRange(styles, start, length);
        var items = Items(style, text, styles);
        var tab = TabPixels(style);
        var whole = shaper.Shape(text, items, tab);
        List<int> starts = [0];
        if (float.IsFinite(wrapWidth) && wrapWidth > 0 && whole.Width > wrapWidth)
            starts.AddRange(WrapPoints(text, whole, wrapWidth));
        starts.Add(length);
        return (text, styles, items, whole, starts);
    }

    private static ShapeItem[] Clip(ShapeItem[] items, int start, int end)
    {
        var clipped = new List<ShapeItem>();
        foreach (var item in items)
        {
            int s = Math.Max(item.Start, start), e = Math.Min(item.End, end);
            if (s < e) clipped.Add(item with { Start = s - start, End = e - start });
        }
        return [.. clipped];
    }

    // Items split where the font changes, at tabs and at control-character or invalid-byte representations.
    private ShapeItem[] Items(ViewStyle style, byte[] text, byte[] styles)
    {
        var items = new List<ShapeItem>();
        var runStart = 0;
        FontSpec? runFont = null;
        void Flush(int end)
        {
            if (runFont is { } font && runStart < end) items.Add(new(runStart, end, font, 0));
            runFont = null;
        }
        for (var i = 0; i < text.Length;)
        {
            var font = style.Styles[styles[i]].FontSpec;
            var representation = Representation(text.AsSpan(i), out var consumed);
            if (representation is not null || text[i] == '\t')
            {
                Flush(i);
                items.Add(new(i, i + consumed, font, representation is null ? -1 : RepresentationWidth(font, representation)));
                i += consumed;
                continue;
            }
            if (runFont is { } current && current != font) Flush(i);
            if (runFont is null) { runStart = i; runFont = font; }
            i += consumed;
        }
        Flush(text.Length);
        return [.. items];
    }

    private float RepresentationWidth(FontSpec font, string text) =>
        shaper.Measure(font, Encoding.ASCII.GetBytes(text)) + RepresentationPadding;

    /// <summary>The mnemonic shown for a C0 control, DEL or an invalid UTF-8 byte (xHH) at the start of bytes, else null.</summary>
    internal static string? Representation(ReadOnlySpan<byte> bytes, out int consumed)
    {
        if (Rune.DecodeFromUtf8(bytes, out var rune, out consumed) != OperationStatus.Done)
        {
            consumed = 1;
            return $"x{bytes[0]:X2}";
        }
        return rune.Value < 0x20 && rune.Value != '\t' ? ControlMnemonics[rune.Value] : rune.Value == 0x7f ? "DEL" : null;
    }

    // Greedy wrap over clusters in logical order: break after the last space or punctuation boundary that
    // fits, else before the cluster that overflows, keeping at least one cluster on each sub-line.
    private static List<int> WrapPoints(byte[] text, LineShape whole, float wrapWidth)
    {
        var clusters = whole.Clusters.OrderBy(c => c.Start).ToArray();
        var points = new List<int>();
        var subStart = 0;
        var width = 0f;
        for (var i = 0; i < clusters.Length; i++)
        {
            var c = clusters[i];
            if (width + c.Width <= wrapWidth || c.Start <= subStart)
            {
                width += c.Width;
                continue;
            }
            int point;
            if (IsSpace(text[c.Start]))
            {
                // Overflowing spaces stay at the end of this row; the next row starts at the following word.
                point = c.Start;
                while (point < text.Length && IsSpace(text[point])) point++;
                if (point >= text.Length) break;
            }
            else
            {
                point = c.Start;
                for (var b = c.Start; b > subStart; b--)
                    if (IsBreak(text, b) && Array.Exists(clusters, k => k.Start == b)) { point = b; break; }
            }
            points.Add(point);
            subStart = point;
            width = 0;
            i = Array.FindIndex(clusters, k => k.Start >= point) - 1;
            if (i < -1) break;
        }
        return points;
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t';

    private static bool IsPunctuation(byte b) => b < 0x80 && (char.IsPunctuation((char)b) || char.IsSymbol((char)b));

    private static bool IsBreak(byte[] text, int b) =>
        b > 0 && b < text.Length && !IsSpace(text[b]) &&
        (IsSpace(text[b - 1]) || (text[b] < 0x80 && text[b - 1] < 0x80 && IsPunctuation(text[b - 1]) != IsPunctuation(text[b])));
}

/// <summary>Everything a paint needs from the editor model.</summary>
internal sealed record PaintState(
    Document Document,
    ViewStyle Style,
    ContractionState Display,
    Selection Selection,
    SKRect Client,
    int TopDisplayLine,
    float XOffset,
    float WrapWidth,
    bool Focused,
    bool CaretVisible,
    bool ReadOnly);