namespace SharpRail.UI.Editor;

public readonly record struct EditorScroll(double Maximum, double Viewport, double Value);

public sealed partial class ScintillaEditor
{
    private (EditorScroll Vertical, EditorScroll Horizontal) scroll;
    public event EventHandler? ScrollChanged;

    // Vertical units are lines; horizontal units are pixels.
    public EditorScroll VerticalScroll => scroll.Vertical;
    public EditorScroll HorizontalScroll => scroll.Horizontal;

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
        var vertical = new EditorScroll(Math.Max(0, document.Send(ScintillaMessage.GetLineCount) - lines), lines, FirstVisibleLine);
        double margins = document.Send(ScintillaMessage.GetMarginLeft) + document.Send(ScintillaMessage.GetMarginRight);
        for (var margin = 0; margin < document.Send(ScintillaMessage.GetMargins); margin++)
            margins += document.Send(ScintillaMessage.GetMarginWidthN, margin);
        var text = Math.Max(0, Bounds.Width - margins);
        var horizontal = new EditorScroll(Math.Max(0, document.Send(ScintillaMessage.GetScrollWidth) - text), text, document.Send(ScintillaMessage.GetXOffset));
        if (scroll == (vertical, horizontal)) return;
        scroll = (vertical, horizontal);
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }
}
