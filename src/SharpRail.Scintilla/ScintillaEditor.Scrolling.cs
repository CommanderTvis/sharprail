namespace SharpRail.Scintilla;

public readonly record struct EditorScroll(double Maximum, double Viewport, double Value);

public sealed partial class ScintillaEditor
{
    private (EditorScroll Vertical, EditorScroll Horizontal) scroll;
    // Scintilla scrolls in whole lines; the wheel keeps a pixel remainder past the first visible
    // line so trackpad and momentum scrolling move smoothly instead of in line-sized jumps.
    private double subLine;
    private int subLineTop = -1;
    public event EventHandler? ScrollChanged;
    /// <summary>Raised as soon as the wheel or <see cref="ScrollToLine"/> moves <see cref="VerticalPixelOffset"/>, for views that scroll in step.</summary>
    public event EventHandler? VerticalOffsetChanged;

    // Vertical units are display lines, which differ from document lines when wrapping; horizontal units are pixels.
    public EditorScroll VerticalScroll => scroll.Vertical;
    public EditorScroll HorizontalScroll => scroll.Horizontal;
    public double VerticalPixelOffset => FirstVisibleLine * LineHeight + SubLine;
    private double LineHeight => document.Send(ScintillaMessage.TextHeight);
    // Any other scroll (keyboard, scrollbar, caret) moves the first line and drops the remainder.
    private double SubLine => subLineTop == FirstVisibleLine ? subLine : 0;

    private long MaxFirstLine()
    {
        var last = document.Send(ScintillaMessage.GetLineCount) - 1;
        var displayLines = document.Send(ScintillaMessage.VisibleFromDocLine, last) + document.Send(ScintillaMessage.WrapCount, last);
        return Math.Max(0, displayLines - document.Send(ScintillaMessage.LinesOnScreen));
    }

    private void ScrollPixels(double pixels)
    {
        var height = LineHeight;
        var total = SubLine + pixels;
        var lines = (long)Math.Floor(total / height);
        var before = FirstVisibleLine;
        if (lines != 0) document.Send(ScintillaMessage.LineScroll, 0, (nint)lines);
        subLineTop = FirstVisibleLine;
        // An edge clamped the scroll, or the last line is fully shown: no partial line remains.
        subLine = FirstVisibleLine - before != lines || FirstVisibleLine >= MaxFirstLine() ? 0 : total - lines * height;
        VerticalOffsetChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The document line at the top and how far, in pixels, the view has scrolled into its wrapped rows.</summary>
    public (int Line, double Offset) DocumentPosition
    {
        get
        {
            var display = FirstVisibleLine;
            var line = checked((int)document.Send(ScintillaMessage.DocLineFromVisible, display));
            return (line, (display - document.Send(ScintillaMessage.VisibleFromDocLine, line)) * LineHeight + SubLine);
        }
    }

    /// <summary>Scrolls a document line to the top, offset into its wrapped rows; for views whose lines wrap differently.</summary>
    public void ScrollToDocumentPosition(int line, double offset)
    {
        var height = LineHeight;
        var rows = document.Send(ScintillaMessage.WrapCount, line);
        ScrollToPixel(document.Send(ScintillaMessage.VisibleFromDocLine, line) * height + Math.Clamp(offset, 0, Math.Max(0, rows * height - 1)));
    }

    /// <summary>Scrolls to a pixel offset from the top, keeping the partial line; it does not raise <see cref="VerticalOffsetChanged"/>.</summary>
    public void ScrollToPixel(double offset)
    {
        if (disposed) return;
        var height = LineHeight;
        var line = (long)Math.Floor(Math.Max(0, offset) / height);
        document.Send(ScintillaMessage.SetFirstVisibleLine, (nint)line);
        subLineTop = FirstVisibleLine;
        subLine = FirstVisibleLine != line || FirstVisibleLine >= MaxFirstLine() ? 0 : Math.Max(0, offset) - line * height;
        InvalidateVisual(); inputClient.NotifyScrolled();
    }

    public void ScrollToLine(double line)
    {
        if (disposed) return;
        document.Send(ScintillaMessage.SetFirstVisibleLine, (nint)Math.Round(line));
        InvalidateVisual(); inputClient.NotifyScrolled();
        VerticalOffsetChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScrollToX(double x)
    {
        if (disposed) return;
        document.Send(ScintillaMessage.SetXOffset, (nint)Math.Round(Math.Max(0, x)));
        InvalidateVisual(); inputClient.NotifyScrolled();
    }

    private void UpdateScroll()
    {
        if (disposed) return;
        var lines = document.Send(ScintillaMessage.LinesOnScreen);
        var vertical = new EditorScroll(MaxFirstLine(), lines, FirstVisibleLine);
        double margins = document.Send(ScintillaMessage.GetMarginLeft) + document.Send(ScintillaMessage.GetMarginRight);
        for (var margin = 0; margin < document.Send(ScintillaMessage.GetMargins); margin++)
            margins += document.Send(ScintillaMessage.GetMarginWidthN, margin);
        var text = Math.Max(0, Bounds.Width - margins);
        // Scintilla's tracked scroll width keeps pre-wrap line widths; wrapped text never scrolls horizontally.
        var horizontal = double.IsFinite(wrapWidth) ? new EditorScroll(0, text, 0)
            : new EditorScroll(Math.Max(0, document.Send(ScintillaMessage.GetScrollWidth) - text), text, document.Send(ScintillaMessage.GetXOffset));
        if (scroll == (vertical, horizontal)) return;
        scroll = (vertical, horizontal);
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }
}