using System.Text;

using Avalonia;

using SkiaSharp;

using Shim = ShimSkiaSharp;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>
/// The text a rendered diagram draws, read once from its drawing commands: each label run with one box per character
/// in picture coordinates, in reading order (top to bottom, then left to right), so the picture can be selected and copied.
/// </summary>
internal sealed class DiagramText
{
    internal sealed record Run(string Text, Rect[] Boxes, Rect Bounds, int Line);

    private DiagramText(Run[] runs, bool outline)
    {
        Runs = runs;
        Outline = outline;
        Length = runs.Sum(run => run.Text.Length);
    }

    internal static DiagramText Empty { get; } = new([], false);

    internal IReadOnlyList<Run> Runs { get; }

    /// <summary>The number of selectable characters, across every run.</summary>
    internal int Length { get; }

    /// <summary>True for a tree view, whose copied lines keep their nesting as indentation.</summary>
    private bool Outline { get; }

    /// <summary>Reads the text runs of a parsed SVG drawing; call it off the UI thread.</summary>
    internal static DiagramText From(Shim.SKPicture? model, bool outline)
    {
        if (model is null) return Empty;
        var found = new List<(string Text, Rect[] Boxes, Rect Bounds)>();
        Collect(model, Shim.SKMatrix.CreateIdentity(), found);
        // Reading order: runs whose vertical middles fall inside one another's band share a line.
        var ordered = found.Where(run => run.Text.Length > 0).OrderBy(run => run.Bounds.Center.Y).ToList();
        var lines = new List<List<(string Text, Rect[] Boxes, Rect Bounds)>>();
        foreach (var run in ordered)
        {
            var line = lines.Count > 0 ? lines[^1] : null;
            if (line is not null && line.Any(other => run.Bounds.Center.Y < other.Bounds.Bottom && run.Bounds.Center.Y > other.Bounds.Top)) line.Add(run);
            else lines.Add([run]);
        }
        var runs = lines.SelectMany((line, index) => line.OrderBy(run => run.Bounds.X).Select(run => new Run(run.Text, run.Boxes, run.Bounds, index))).ToArray();
        return new DiagramText(runs, outline);
    }

    private static void Collect(Shim.SKPicture picture, Shim.SKMatrix origin, List<(string, Rect[], Rect)> found)
    {
        var matrix = origin;
        var saved = new Stack<Shim.SKMatrix>();
        foreach (var command in picture.Commands ?? [])
        {
            switch (command)
            {
                case Shim.SaveCanvasCommand or Shim.SaveLayerCanvasCommand: saved.Push(matrix); break;
                case Shim.RestoreCanvasCommand: if (saved.Count > 0) matrix = saved.Pop(); break;
                case Shim.SetMatrixCanvasCommand set: matrix = origin.PreConcat(set.TotalMatrix); break;
                case Shim.DrawPictureCanvasCommand nested when nested.Picture is { } inner:
                    Collect(inner, matrix, found);
                    break;
                case Shim.DrawTextCanvasCommand text when text.Text is { Length: > 0 }:
                    Add(found, text.Text, text.X, text.Y, null, text.Paint, text.Font, text.TextAlign ?? text.Paint?.TextAlign ?? Shim.SKTextAlign.Left, matrix);
                    break;
                case Shim.DrawTextBlobCanvasCommand blob when blob.TextBlob?.Text is { Length: > 0 } blobText:
                    Add(found, blobText, blob.X, blob.Y, blob.TextBlob.Points, blob.Paint, blob.TextBlob.Font, Shim.SKTextAlign.Left, matrix);
                    break;
            }
        }
    }

    private static void Add(List<(string, Rect[], Rect)> found, string text, float x, float y, Shim.SKPoint[]? points,
        Shim.SKPaint? paint, Shim.SKFont? shimFont, Shim.SKTextAlign align, Shim.SKMatrix matrix)
    {
        var typeface = shimFont?.Typeface ?? paint?.Typeface;
        using var face = SKTypeface.FromFamilyName(typeface?.FamilyName, (SKFontStyleWeight)(int)(typeface?.FontWeight ?? Shim.SKFontStyleWeight.Normal),
            (SKFontStyleWidth)(int)(typeface?.FontWidth ?? Shim.SKFontStyleWidth.Normal), (SKFontStyleSlant)(int)(typeface?.FontSlant ?? Shim.SKFontStyleSlant.Upright));
        using var font = new SKFont(face, shimFont?.Size ?? paint?.TextSize ?? 12);
        var metrics = font.Metrics;
        // Edges between characters: measured prefixes, so kerning and shaping stay out of the box widths' way.
        var edges = new float[text.Length + 1];
        for (var index = 1; index <= text.Length; index++) edges[index] = font.MeasureText(text.AsSpan(0, index));
        var start = align switch { Shim.SKTextAlign.Center => x - edges[^1] / 2, Shim.SKTextAlign.Right => x - edges[^1], _ => x };
        var boxes = new Rect[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            var (left, baseline) = points is { } placed && index < placed.Length
                ? (x + placed[index].X, y + placed[index].Y)
                : (start + edges[index], y);
            var width = edges[index + 1] - edges[index];
            boxes[index] = Map(matrix, left, baseline + metrics.Ascent, left + width, baseline + metrics.Descent);
        }
        var bounds = boxes.Aggregate((first, next) => first.Union(next));
        found.Add((text, boxes, bounds));
    }

    private static Rect Map(Shim.SKMatrix matrix, float left, float top, float right, float bottom)
    {
        var mapped = matrix.MapRect(new Shim.SKRect(left, top, right, bottom));
        return new Rect(mapped.Left, mapped.Top, Math.Max(0, mapped.Width), Math.Max(0, mapped.Height));
    }

    /// <summary>The character index nearest a picture point: inside a run, the gap closest to it; elsewhere, the nearest run's end.</summary>
    internal int HitTest(Point point)
    {
        var offset = 0;
        var best = (Distance: double.MaxValue, Index: 0);
        foreach (var run in Runs)
        {
            var bounds = run.Bounds;
            var dx = Math.Max(0, Math.Max(bounds.Left - point.X, point.X - bounds.Right));
            var dy = Math.Max(0, Math.Max(bounds.Top - point.Y, point.Y - bounds.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance < best.Distance)
            {
                var within = 0;
                while (within < run.Boxes.Length && point.X > run.Boxes[within].Center.X) within++;
                best = (distance, offset + within);
            }
            offset += run.Text.Length;
        }
        return best.Index;
    }

    /// <summary>The run under a picture point, if any, as its first and end character indexes.</summary>
    internal (int Start, int End)? RunAt(Point point, double slop = 0)
    {
        var offset = 0;
        foreach (var run in Runs)
        {
            if (run.Bounds.Inflate(slop).Contains(point)) return (offset, offset + run.Text.Length);
            offset += run.Text.Length;
        }
        return null;
    }

    /// <summary>The characters from <paramref name="start"/> up to <paramref name="end"/>: runs on one line join with a space, lines with a newline.</summary>
    internal string Text(int start, int end)
    {
        var text = new StringBuilder();
        var steps = Outline ? Runs.Select(run => run.Bounds.Left).Distinct().Order().ToArray() : [];
        var offset = 0;
        Run? previous = null;
        int? first = null;
        foreach (var run in Runs)
        {
            var from = Math.Max(start, offset) - offset;
            var to = Math.Min(end, offset + run.Text.Length) - offset;
            offset += run.Text.Length;
            if (from >= to) continue;
            if (previous is not null) text.Append(previous.Line == run.Line ? " " : "\n");
            // An outline's lines indent by their level below the first selected line's.
            var level = Array.IndexOf(steps, run.Bounds.Left);
            first ??= level;
            if (Outline && previous is not null && previous.Line != run.Line) text.Append(' ', 4 * Math.Max(0, level - first.Value));
            text.Append(run.Text, from, to - from);
            previous = run;
        }
        return text.ToString();
    }

    /// <summary>The character boxes from <paramref name="start"/> up to <paramref name="end"/>, merged into one rectangle per run.</summary>
    internal IEnumerable<Rect> Highlight(int start, int end)
    {
        var offset = 0;
        foreach (var run in Runs)
        {
            var from = Math.Max(start, offset) - offset;
            var to = Math.Min(end, offset + run.Text.Length) - offset;
            offset += run.Text.Length;
            if (from < to) yield return run.Boxes[from].Union(run.Boxes[to - 1]);
        }
    }
}