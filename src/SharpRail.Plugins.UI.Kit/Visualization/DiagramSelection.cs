using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>
/// Draws the selected diagram text over the picture it sits on, in the theme's text-selection colour. It is laid out
/// at the picture's displayed size and maps picture coordinates by the displayed scale, so the highlight follows zoom.
/// </summary>
internal sealed class DiagramSelection : Control
{
    private DiagramText text = DiagramText.Empty;
    private Rect picture;

    public DiagramSelection() => IsHitTestVisible = false;

    internal int Start { get; private set; }

    internal int End { get; private set; }

    internal void Show(DiagramText shown, Rect cull)
    {
        text = shown;
        picture = cull;
        Select(0, 0);
    }

    internal void Select(int anchor, int active)
    {
        (Start, End) = (Math.Min(anchor, active), Math.Max(anchor, active));
        InvalidateVisual();
    }

    /// <summary>A point on this control in picture coordinates.</summary>
    internal Point ToPicture(Point point) => picture.Width > 0 && Bounds.Width > 0
        ? new Point(picture.X + point.X * picture.Width / Bounds.Width, picture.Y + point.Y * picture.Height / Bounds.Height)
        : point;

    public override void Render(DrawingContext context)
    {
        if (Start == End || picture.Width <= 0 || picture.Height <= 0) return;
        var (scaleX, scaleY) = (Bounds.Width / picture.Width, Bounds.Height / picture.Height);
        foreach (var box in text.Highlight(Start, End))
            context.FillRectangle(Ui.TextSelection, new Rect((box.X - picture.X) * scaleX, (box.Y - picture.Y) * scaleY, box.Width * scaleX, box.Height * scaleY));
    }
}