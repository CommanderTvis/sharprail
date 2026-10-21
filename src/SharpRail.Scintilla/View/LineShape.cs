using SkiaSharp;

namespace SharpRail.Scintilla.View;

/// <summary>One logical item of a screen line: same font, or a fixed-width representation (width &gt; 0) or a tab (width -1).</summary>
internal readonly record struct ShapeItem(int Start, int End, FontSpec Font, float Width);

/// <summary>A caret stop in visual order, byte offsets relative to the screen line.</summary>
internal readonly record struct ShapeCluster(int Start, int End, float Left, float Width, bool RightToLeft);

/// <summary>An x interval (selection, indicator) in screen-line coordinates.</summary>
internal readonly record struct ShapeInterval(float Left, float Right);

/// <summary>
/// A screen line after bidi reordering and HarfBuzz shaping (port of Native/Layout.cxx ShapedLayout).
/// Immutable and cached by <see cref="LineShaper"/>.
/// </summary>
internal sealed class LineShape
{
    /// <summary>A shaped text piece: byte range in the line, its left edge, and the glyphs with clusters relative to Start.</summary>
    internal readonly record struct Run(int Start, int End, float Left, TextShaper.Shaped Shaped);

    internal LineShaper? Owner { get; init; }
    internal IReadOnlyList<Run> Runs { get; init; } = [];
    public IReadOnlyList<ShapeCluster> Clusters { get; init; } = [];
    public float Width { get; init; }

    public int PositionFromX(float x, bool characterPosition)
    {
        if (Clusters.Count == 0) return 0;
        var cluster = Clusters[^1];
        foreach (var c in Clusters)
            if (x < c.Left + c.Width) { cluster = c; break; }
        if (characterPosition) return cluster.Start;
        // The half nearer the cluster's logical start maps to its start.
        var left = x < cluster.Left + cluster.Width / 2;
        return left != cluster.RightToLeft ? cluster.Start : cluster.End;
    }

    // A caret sits on the leading edge of the cluster it precedes, or else on the trailing
    // edge of the cluster it follows, as at the end of the line.
    public float XFromPosition(int position)
    {
        foreach (var c in Clusters)
            if (c.Start == position) return c.RightToLeft ? c.Left + c.Width : c.Left;
        foreach (var c in Clusters)
            if (c.End == position) return c.RightToLeft ? c.Left : c.Left + c.Width;
        foreach (var c in Clusters)
            if (c.Start < position && position < c.End) return c.RightToLeft ? c.Left + c.Width : c.Left;
        return Clusters.Count == 0 ? 0 : Clusters[^1].Left + Clusters[^1].Width;
    }

    public IReadOnlyList<ShapeInterval> RangeIntervals(int start, int end)
    {
        var intervals = new List<ShapeInterval>();
        foreach (var c in Clusters)
        {
            if (c.Start >= end || c.End <= start || c.Width <= 0) continue;
            if (intervals.Count > 0 && Math.Abs(intervals[^1].Right - c.Left) < 0.01f)
                intervals[^1] = intervals[^1] with { Right = c.Left + c.Width };
            else
                intervals.Add(new(c.Left, c.Left + c.Width));
        }
        return intervals;
    }

    /// <summary>Draws glyphs whose clusters start in [start, end) at x origin and baseline.</summary>
    public void Draw(SKCanvas canvas, int start, int end, float x, float baseline, SKColor fore)
    {
        if (Owner is null) return;
        foreach (var run in Runs)
        {
            if (run.Start >= end || run.End <= start) continue;
            Owner.DrawShaped(canvas, run.Shaped, Math.Max(start, run.Start) - run.Start, Math.Min(end, run.End) - run.Start,
                x + run.Left, baseline, fore);
        }
    }
}

/// <summary>Builds and caches <see cref="LineShape"/>s for a typeface; owns the HarfBuzz <see cref="TextShaper"/> and font objects.</summary>
internal sealed class LineShaper(SKTypeface typeface) : IDisposable
{
    private const int CachedShapes = 256;

    private sealed record Key(byte[] Text, ShapeItem[] Items, ScintillaTextDirection Direction, float TabPixels, int Hash);

    private readonly TextShaper shaper = new(typeface);
    private readonly Dictionary<FontSpec, SKFont> fonts = [];
    // Most recently used first; the editor asks for the same line several times per paint.
    private readonly LinkedList<(Key Key, LineShape Shape)> cache = new();
    private readonly SKTextBlobBuilder blobBuilder = new();
    private readonly SKPaint fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private ushort[] glyphs = [];
    private SKPoint[] points = [];

    public SKTypeface Typeface => typeface;
    public ScintillaTextDirection Direction { get; set; }

    /// <summary>Shapes a screen line. Tabs advance to the next multiple of <paramref name="tabPixels"/> past x.</summary>
    public LineShape Shape(ReadOnlySpan<byte> text, IReadOnlyList<ShapeItem> items, float tabPixels)
    {
        var hash = new HashCode();
        hash.AddBytes(text);
        foreach (var item in items) hash.Add(item);
        var hashCode = hash.ToHashCode();
        for (var node = cache.First; node is not null; node = node.Next)
        {
            var key = node.Value.Key;
            if (key.Hash != hashCode || key.Direction != Direction || key.TabPixels != tabPixels || !text.SequenceEqual(key.Text)
                || !items.SequenceEqual(key.Items)) continue;
            cache.Remove(node);
            cache.AddFirst(node);
            return node.Value.Shape;
        }
        var shape = Build(text, items, tabPixels);
        cache.AddFirst((new(text.ToArray(), [.. items], Direction, tabPixels, hashCode), shape));
        if (cache.Count > CachedShapes) cache.RemoveLast();
        return shape;
    }

    private LineShape Build(ReadOnlySpan<byte> text, IReadOnlyList<ShapeItem> items, float tabPixels)
    {
        var clusters = new List<ShapeCluster>();
        var runs = new List<LineShape.Run>();
        var x = 0f;
        foreach (var level in Bidi.VisualRuns(text, Direction))
        {
            var pieces = new List<ShapeItem>();
            foreach (var item in items)
            {
                int start = Math.Max(item.Start, level.Start), end = Math.Min(item.End, level.End);
                if (start < end) pieces.Add(item with { Start = start, End = end });
            }
            if (level.RightToLeft) pieces.Reverse();
            foreach (var piece in pieces)
            {
                if (piece.Width != 0)
                {
                    var width = piece.Width > 0 ? piece.Width : TabPositionAfter(x, tabPixels) - x;
                    clusters.Add(new(piece.Start, piece.End, x, width, level.RightToLeft));
                    x += width;
                    continue;
                }
                var shaped = shaper.Shape(Font(piece.Font), text[piece.Start..piece.End], level.RightToLeft ? 1 : 0);
                var left = x;
                runs.Add(new(piece.Start, piece.End, left, shaped));
                var graphemes = new List<(int Start, int End, float Width)>();
                float previous = 0;
                for (int g = 0, start = 0; g < shaped.GraphemeEnds.Length; start = shaped.GraphemeEnds[g++])
                {
                    graphemes.Add((piece.Start + start, piece.Start + shaped.GraphemeEnds[g], shaped.Advances[g] - previous));
                    previous = shaped.Advances[g];
                }
                // Right-to-left graphemes are placed from the last logical one leftwards.
                if (level.RightToLeft) graphemes.Reverse();
                foreach (var (start, end, width) in graphemes)
                {
                    clusters.Add(new(start, end, x, width, level.RightToLeft));
                    x += width;
                }
                x = left + shaped.Width;
            }
        }
        return new LineShape { Owner = this, Runs = runs, Clusters = clusters, Width = x };
    }

    private static float TabPositionAfter(float x, float tabPixels) =>
        tabPixels <= 0 ? x : MathF.Ceiling((x + 1) / tabPixels) * tabPixels;

    public SKFont Font(FontSpec spec)
    {
        if (fonts.TryGetValue(spec, out var font)) return font;
        font = new SKFont(typeface, spec.Size)
        {
            Subpixel = true,
            Edging = SKFontEdging.SubpixelAntialias,
            Embolden = spec.Weight >= 600,
            SkewX = spec.Italic ? -.2f : 0,
        };
        fonts.Add(spec, font);
        return font;
    }

    /// <summary>Ascent, descent (both positive) and line height (ceil of descent+ascent+leading), as the native surface reported.</summary>
    public (float Ascent, float Descent, float Height) Metrics(FontSpec spec)
    {
        var metrics = Font(spec).Metrics;
        return (-metrics.Ascent, metrics.Descent, MathF.Ceiling(metrics.Descent - metrics.Ascent + metrics.Leading));
    }

    /// <summary>Advance of plain UTF-8 text in one font, e.g. margin labels and TEXTWIDTH.</summary>
    public float Measure(FontSpec spec, ReadOnlySpan<byte> utf8) => shaper.Shape(Font(spec), utf8, -1).Width;

    /// <summary>Draws unordered single-font text (margin labels).</summary>
    public void DrawText(SKCanvas canvas, FontSpec spec, ReadOnlySpan<byte> utf8, float x, float baseline, SKColor fore) =>
        DrawShaped(canvas, shaper.Shape(Font(spec), utf8, -1), 0, utf8.Length, x, baseline, fore);

    // Draws the glyphs whose clusters start inside [start, end) of the shaped text.
    internal void DrawShaped(SKCanvas canvas, TextShaper.Shaped shaped, int start, int end, float x, float baseline, SKColor fore)
    {
        var origin = new SKPoint(x, baseline);
        foreach (var run in shaped.Runs)
        {
            if (glyphs.Length < run.Glyphs.Length) { glyphs = new ushort[run.Glyphs.Length]; points = new SKPoint[run.Glyphs.Length]; }
            var count = 0;
            for (var i = 0; i < run.Glyphs.Length; i++)
            {
                if (run.Clusters[i] < start || run.Clusters[i] >= end) continue;
                glyphs[count] = run.Glyphs[i]; points[count++] = run.Positions[i] + origin;
            }
            if (count > 0) blobBuilder.AddPositionedRun(glyphs.AsSpan(0, count), run.Font, points.AsSpan(0, count));
        }
        using var blob = blobBuilder.Build();
        fill.Color = fore;
        if (blob is not null) canvas.DrawText(blob, 0, 0, fill);
    }

    public void Dispose()
    {
        foreach (var font in fonts.Values) font.Dispose();
        fonts.Clear(); cache.Clear();
        shaper.Dispose(); blobBuilder.Dispose(); fill.Dispose();
    }
}