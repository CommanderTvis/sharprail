using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Plugins.Epub.UI;

/// <summary>
/// Splits the rendered book into viewport-sized pages. Breaks are found by sweeping the document from the top:
/// each page ends at the top of the first text line or image that would be cut by the viewport's bottom edge, and
/// every chapter starts a page. The sweep runs in slices so a long book never holds the UI thread for long.
/// </summary>
internal sealed class EpubPages(MarkdownPreview preview, IReadOnlyList<int> chapterLines)
{
    private readonly List<double> tops = [0];
    private int nextChapter = 1;

    internal IReadOnlyList<double> Tops => tops;
    internal bool Complete { get; private set; }

    /// <summary>The page holding content offset <paramref name="offset"/>, or null while the sweep has not reached it.</summary>
    internal int? PageAt(double offset)
    {
        if (!Complete && offset >= tops[^1]) return null;
        var index = tops.BinarySearch(offset + 0.5);
        return Math.Max(0, index >= 0 ? index : ~index - 1);
    }

    /// <summary>The bottom of page <paramref name="index"/>: the next page's top, or the document's end.</summary>
    internal double Bottom(int index, double height) => index + 1 < tops.Count ? tops[index + 1] : tops[index] + height;

    /// <summary>Advances the sweep for up to <paramref name="budget"/>; the caller restores the reader's offset afterwards.</summary>
    internal void Advance(TimeSpan budget)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var height = preview.Viewport.Height;
        if (height < 40) return;
        while (!Complete && started.Elapsed < budget)
        {
            var top = tops[^1];
            var limit = top + height;
            if (limit >= preview.Extent.Height - 1) { Complete = true; break; }
            preview.Offset = new Vector(preview.Offset.X, top);
            preview.UpdateLayout();
            var chapter = ChapterTop(top);
            var next = chapter is { } start && start > top + 1 && start < limit ? start : Snap(top, limit);
            tops.Add(next > top + 1 ? next : limit);
        }
    }

    private double? ChapterTop(double top)
    {
        // Only blocks near the viewport are mounted, so a chapter start that answers null is further down.
        while (nextChapter < chapterLines.Count)
        {
            if (preview.LineOffset(chapterLines[nextChapter], reveal: false) is not { } start) return null;
            if (start > top + 1) return start;
            nextChapter++;
        }
        return null;
    }

    // The highest top among lines and images the edge at limit would cut, so the page ends cleanly above them.
    private double Snap(double top, double limit)
    {
        var cut = limit;
        foreach (var visual in preview.GetVisualDescendants())
        {
            if (visual is not Control { IsEffectivelyVisible: true } control || control.TranslatePoint(default, preview) is not { } point) continue;
            var y = preview.Offset.Y + point.Y;
            if (y >= limit || y + control.Bounds.Height <= top) continue;
            if (control is SelectableTextBlock text)
            {
                var lineTop = y + text.Padding.Top;
                foreach (var line in text.TextLayout.TextLines)
                {
                    if (lineTop < limit && lineTop + line.Height > limit + 0.5 && lineTop > top + 1) cut = Math.Min(cut, lineTop);
                    lineTop += line.Height;
                }
            }
            else if (control is Image && y < limit && y + control.Bounds.Height > limit + 0.5 && y > top + 1 && control.Bounds.Height < limit - top)
                cut = Math.Min(cut, y);
        }
        return cut;
    }
}