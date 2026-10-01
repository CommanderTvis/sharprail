using System.Runtime.ExceptionServices;

using SkiaSharp;

namespace SharpRail.Scintilla;

internal sealed unsafe class SkiaSurface : IDisposable
{
    private readonly Dictionary<(float Size, int Weight, int Italic), SKFont> fonts = [];
    private readonly Dictionary<int, SKSurface> surfaces = [];
    private readonly SKTypeface typeface;
    private readonly TextShaper shaper;
    private readonly SKTextBlobBuilder blobBuilder = new();
    private readonly SKPaint fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private ushort[] glyphs = [];
    private SKPoint[] points = [];
    private int nextSurface;
    private SKCanvas? canvas;
    private ExceptionDispatchInfo? error;
    internal ScintillaNative.DrawCallback Callback { get; }

    internal SkiaSurface(SKTypeface typeface)
    {
        this.typeface = typeface;
        shaper = new(typeface);
        Callback = Dispatch;
    }

    internal void CheckError()
    {
        var pending = error; error = null;
        pending?.Throw();
        if (ScintillaNative.sr_failed() != 0) throw new InvalidOperationException("Scintilla could not complete the editor operation.");
    }

    internal SKPicture Record(nint editor, float width, float height)
    {
        using var recorder = new SKPictureRecorder();
        canvas = recorder.BeginRecording(new SKRect(0, 0, width, height));
        try
        {
            ScintillaNative.sr_paint(editor);
            CheckError();
            return recorder.EndRecording();
        }
        finally { canvas = null; }
    }

    private SKFont Font(in ScintillaNative.DrawCommand c)
    {
        var key = ((float)c.Size, c.Weight, c.Italic);
        if (!fonts.TryGetValue(key, out var font))
        {
            font = new SKFont(typeface, key.Item1) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias, Embolden = c.Weight >= 600, SkewX = c.Italic != 0 ? -.2f : 0 };
            fonts.Add(key, font);
        }
        return font;
    }

    private TextShaper.Shaped Shape(in ScintillaNative.DrawCommand c, int direction) =>
        shaper.Shape(Font(c), new ReadOnlySpan<byte>(c.Data, checked((int)c.Length)), direction);

    // Scintilla indexes UTF-8 bytes: every byte of a grapheme reports the grapheme's right edge,
    // or, for line layout, only its last byte does and the others report -1.
    private double Measure(in ScintillaNative.DrawCommand c, int direction, bool graphemeEndsOnly)
    {
        var shaped = Shape(c, direction);
        if (c.Positions != null)
        {
            for (int grapheme = 0, b = 0; grapheme < shaped.GraphemeEnds.Length; grapheme++)
                for (var end = shaped.GraphemeEnds[grapheme]; b < end; b++)
                    c.Positions[b] = !graphemeEndsOnly || b == end - 1 ? shaped.Advances[grapheme] : -1;
        }
        return shaped.Width;
    }

    // Draws the glyphs whose clusters start inside [start, end) of the shaped text.
    private void DrawText(in ScintillaNative.DrawCommand c, SKCanvas target, int direction, nint start, nint end)
    {
        var shaped = Shape(c, direction);
        var origin = new SKPoint((float)c.Left, (float)c.Baseline);
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
        if (blob is not null) target.DrawText(blob, 0, 0, Paint(fill, c.Fore));
    }

    private static SKColor Color(uint rgba) => new((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), (byte)(rgba >> 24));

    // Pictures copy paint state when recording, so one mutable paint per style is safe.
    private static SKPaint Paint(SKPaint paint, uint color, double width = 1)
    {
        paint.Color = Color(color); paint.StrokeWidth = (float)width; paint.Shader = null;
        return paint;
    }

    private double Dispatch(nint context, in ScintillaNative.DrawCommand c)
    {
        // Never unwind a managed exception through Scintilla's C++ frames.
        try { return Execute(c); }
        catch (Exception exception) { error ??= ExceptionDispatchInfo.Capture(exception); return 0; }
    }

    private double Execute(in ScintillaNative.DrawCommand c)
    {
        switch (c.Operation)
        {
            case 16:
            case 17:
                return Measure(c, -1, false);
            case 18:
                return -Font(c).Metrics.Ascent;
            case 19:
                return Font(c).Metrics.Descent;
            case 20:
                var metrics = Font(c).Metrics;
                return Math.Ceiling(metrics.Descent - metrics.Ascent + metrics.Leading);
            case 23:
                return Measure(c, c.Rtl, true);
        }
        if (c.Operation == 1)
        {
            var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, (int)c.Right), Math.Max(1, (int)c.Bottom)))
                ?? throw new InvalidOperationException("Unable to allocate a Scintilla drawing surface.");
            surfaces.Add(++nextSurface, surface);
            return nextSurface;
        }
        if (c.Operation == 2) { if (surfaces.Remove(c.Surface, out var old)) old.Dispose(); return 0; }
        var target = c.Surface == 0 ? canvas : surfaces[c.Surface].Canvas;
        if (target is null) return 0;
        var rect = new SKRect((float)c.Left, (float)c.Top, (float)c.Right, (float)c.Bottom);
        var back = Paint(fill, c.Back);
        var line = Paint(stroke, c.Fore, c.Width);
        switch (c.Operation)
        {
            case 3: target.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, line); break;
            case 4:
            case 5:
                using (var builder = new SKPathBuilder())
                {
                    var points = new ReadOnlySpan<double>(c.Data, checked((int)c.Length * 2));
                    for (int i = 0; i < points.Length; i += 2)
                        if (i == 0) builder.MoveTo((float)points[i], (float)points[i + 1]);
                        else builder.LineTo((float)points[i], (float)points[i + 1]);
                    if (c.Operation == 5) builder.Close();
                    using var path = builder.Detach();
                    if (c.Operation == 5) target.DrawPath(path, back);
                    target.DrawPath(path, line);
                }
                break;
            case 6: target.DrawRect(rect, back); target.DrawRect(rect, line); break;
            case 7: target.DrawRect(rect, line); break;
            case 8: target.DrawRect(rect, Paint(fill, c.Fore)); break;
            case 9:
                using (var image = surfaces[(int)c.Length].Snapshot())
                using (var shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat))
                { back.Shader = shader; target.DrawRect(rect, back); }
                break;
            case 10: target.DrawRoundRect(rect, (float)c.Size, (float)c.Size, back); target.DrawRoundRect(rect, (float)c.Size, (float)c.Size, line); break;
            case 11:
                var stops = new ReadOnlySpan<double>(c.Data, checked((int)c.Length * 2));
                var colors = new SKColor[(int)c.Length]; var positions = new float[colors.Length];
                for (int i = 0; i < colors.Length; i++) { positions[i] = (float)stops[i * 2]; colors[i] = Color((uint)stops[i * 2 + 1]); }
                using (var shader = SKShader.CreateLinearGradient(new(rect.Left, rect.Top), c.Width != 0 ? new(rect.Left, rect.Bottom) : new(rect.Right, rect.Top), colors, positions, SKShaderTileMode.Clamp))
                { back.Shader = shader; target.DrawRect(rect, back); }
                break;
            case 12:
                using (var bitmap = new SKBitmap(new SKImageInfo((int)c.Width, (int)c.Size, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
                {
                    new ReadOnlySpan<byte>(c.Data, bitmap.ByteCount).CopyTo(new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount));
                    using var image = SKImage.FromBitmap(bitmap);
                    target.DrawImage(image, rect, SKSamplingOptions.Default);
                }
                break;
            case 13: target.DrawOval(rect, back); target.DrawOval(rect, line); break;
            case 14:
                using (var image = surfaces[(int)c.Length].Snapshot())
                    target.DrawImage(image, new SKRect((float)c.Width, (float)c.Size, (float)c.Width + rect.Width, (float)c.Size + rect.Height), rect, SKSamplingOptions.Default);
                break;
            case 15: DrawText(c, target, -1, 0, c.Length); break;
            case 24: DrawText(c, target, c.Rtl, c.Start, c.End); break;
            case 21: target.Save(); target.ClipRect(rect); break;
            case 22: target.Restore(); break;
            default: throw new InvalidOperationException($"Unknown Scintilla drawing operation {c.Operation}.");
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (var surface in surfaces.Values) surface.Dispose();
        foreach (var font in fonts.Values) font.Dispose();
        surfaces.Clear(); fonts.Clear(); shaper.Dispose();
        blobBuilder.Dispose(); fill.Dispose(); stroke.Dispose();
    }
}