namespace SharpRail.UI.Editor;

public readonly record struct EditorScroll(double Maximum, double Viewport, double Value);

public sealed partial class ScintillaEditor
{
    private (EditorScroll Vertical, EditorScroll Horizontal) scroll;
    // Scintilla scrolls in whole lines; the wheel keeps a pixel remainder past the first visible
    // line so trackpad and momentum scrolling move smoothly instead of in line-sized jumps.
    private double subLine;
    private int subLineTop = -1;
    public event EventHandler? ScrollChanged;

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
    }

    public void ScrollToLine(double line)
    {
        if (disposed) return;
        document.Send(ScintillaMessage.SetFirstVisibleLine, (nint)Math.Round(line));
        InvalidateVisual(); inputClient.NotifyScrolled();
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
