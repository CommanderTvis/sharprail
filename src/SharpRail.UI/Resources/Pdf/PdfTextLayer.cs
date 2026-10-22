using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Resources.Pdf;

/// <summary>
/// The page's text over its rasterized image: the image is a picture of the page, so the text a reader selects and
/// copies is this layer. Character boxes are in points and scale with the layer's own width, so it follows the live
/// zoom without being rebuilt.
/// </summary>
internal sealed class PdfTextLayer : Control
{
    private readonly PdfPage page;
    private readonly PdfSelection selection;
    private bool dragging;
    private readonly DispatcherTimer dragScroll;
    private TopLevel? dragRoot;
    private ScrollViewer? scroller;
    private Point dragPoint;

    public PdfTextLayer(PdfPage page, PdfSelection selection)
    {
        this.page = page;
        this.selection = selection;
        selection.Add(this);
        Name = "PdfTextLayer";
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        dragScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        dragScroll.Tick += (_, _) => ScrollSelection();
        PointerCaptureLost += (_, _) => StopDragging();
        DetachedFromVisualTree += (_, _) => StopDragging();
    }

    public string Text => page.Text;

    public string SelectedText => selection.SelectedText;

    private double Scale => page.Width > 0 ? Bounds.Width / page.Width : 1;

    // The character under the point, or the nearest one, so a drag that starts in a margin still selects.
    internal int CharacterAt(Point point)
    {
        var target = point / Scale;
        var best = -1;
        var distance = double.MaxValue;
        for (var index = 0; index < page.Boxes.Count; index++)
        {
            var box = page.Boxes[index];
            if (box.Width <= 0 && box.Height <= 0) continue;
            var dx = Math.Max(0, Math.Max(box.Left - target.X, target.X - box.Right));
            var dy = Math.Max(0, Math.Max(box.Top - target.Y, target.Y - box.Bottom));
            var next = dy * 4 + dx;
            if (next < distance) { distance = next; best = index; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        dragging = selection.Start(this, CharacterAt(e.GetPosition(this)));
        if (dragging)
        {
            dragRoot = TopLevel.GetTopLevel(this);
            scroller = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            dragPoint = e.GetPosition(dragRoot);
            e.Pointer.Capture(this);
            dragScroll.Start();
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging) return;
        dragPoint = e.GetPosition(dragRoot);
        selection.Move(layer => e.GetPosition(layer));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!dragging) return;
        StopDragging();
        e.Pointer.Capture(null);
    }

    private void StopDragging()
    {
        dragging = false;
        dragScroll.Stop();
        dragRoot = null;
        scroller = null;
    }

    private void ScrollSelection()
    {
        if (dragRoot is null || scroller is null || !dragging) return;
        var point = dragRoot.TranslatePoint(dragPoint, scroller);
        if (point is null) { StopDragging(); return; }
        static double Step(double position, double extent) => position < 24 ? -Math.Clamp((24 - position) / 2, 2, 40)
            : position > extent - 24 ? Math.Clamp((position - extent + 24) / 2, 2, 40) : 0;
        var offset = scroller.Offset;
        scroller.Offset = new Vector(offset.X + Step(point.Value.X, scroller.Viewport.Width), offset.Y + Step(point.Value.Y, scroller.Viewport.Height));
        scroller.UpdateLayout();
        selection.Move(layer => dragRoot.TranslatePoint(dragPoint, layer) ?? default);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.A)
        {
            selection.SelectAll();
            e.Handled = true;
        }
        else if (command && e.Key == Key.C && SelectedText.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            e.Handled = true;
            await clipboard.SetTextAsync(SelectedText);
        }
    }

    public override void Render(DrawingContext context)
    {
        // Transparent, so the whole page takes the pointer rather than only its glyphs.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var (start, end) = selection.Range(this);
        if (start < 0) return;
        var scale = Scale;
        // The boxes cover the page's own glyphs, so the accent is laid over them as a 40% wash (the theme's selection is too translucent on white paper).
        var wash = Ui.PdfSelection;
        foreach (var line in SelectionRects(page.Text, page.Boxes, start, end))
            context.FillRectangle(wash, new Rect(line.X * scale, line.Y * scale, line.Width * scale, line.Height * scale));
    }

    /// <summary>
    /// One highlight per text line, in points: consecutive glyph boxes on the same line are united, so the gaps between
    /// letters and words are covered and the line takes its tallest glyph's extent. A line break in the text, a glyph
    /// outside the line's band or one starting left of it begins the next line; a line overlapping the one above is
    /// trimmed so the translucent wash never doubles.
    /// </summary>
    internal static List<Rect> SelectionRects(string text, IReadOnlyList<Rect> boxes, int start, int end)
    {
        var lines = new List<Rect>();
        Rect? line = null;
        for (var index = Math.Max(0, start); index <= end && index < boxes.Count; index++)
        {
            var box = boxes[index];
            var breaks = index < text.Length && text[index] is '\n' or '\r';
            if (breaks) { Close(); continue; }
            if (box.Width <= 0 && box.Height <= 0) continue;
            if (line is { } current)
            {
                var middle = box.Y + box.Height / 2;
                if (middle >= current.Top && middle <= current.Bottom && box.X >= current.Left - current.Height / 2) { line = current.Union(box); continue; }
                Close();
            }
            line = box;
        }
        Close();
        return lines;

        void Close()
        {
            if (line is not { } done) return;
            line = null;
            if (lines.Count > 0 && lines[^1] is var above && done.Top < above.Bottom && done.Bottom > above.Top
                && done.Left < above.Right && done.Right > above.Left)
            {
                var top = Math.Min(above.Bottom, done.Bottom);
                done = new Rect(done.X, top, done.Width, done.Bottom - top);
            }
            if (done.Height > 0) lines.Add(done);
        }
    }
}