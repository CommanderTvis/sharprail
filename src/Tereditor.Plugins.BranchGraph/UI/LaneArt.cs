using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.BranchGraph.UI;

/// <summary>
/// One row's lane art. It is drawn at the width the whole read can need, so a row never has to be redrawn to widen,
/// and shown at the width the rows on screen need, sliding between widths rather than jumping.
/// </summary>
internal sealed class LaneArt : Control
{
    public const double LaneWidth = 14;
    public const double RowHeight = 40;
    // The radius every corner turns through, so one turn looks like every other.
    private const double Turn = LaneWidth / 2;
    private const double Middle = RowHeight / 2;
    private GraphRow row;
    private int drawn;
    private bool marked;

    public LaneArt(GraphRow row, int drawn, int shown, bool marked)
    {
        this.row = row; this.drawn = drawn; this.marked = marked;
        Name = "GraphLanes";
        Height = RowHeight;
        Width = shown * LaneWidth;
        ClipToBounds = true;
        Transitions = [new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(120) }];
    }

    public int Shown { set => Width = value * LaneWidth; }

    internal void Update(GraphRow model, int width, int shown, bool hasWorktree)
    {
        row = model;
        drawn = width;
        marked = hasWorktree;
        Shown = shown;
        InvalidateVisual();
    }

    private static double Centre(int lane) => lane * LaneWidth + LaneWidth / 2;

    // Every turn is the same quarter of the same circle, whatever distance the line covers: a fan of merges reads
    // as one comb rather than a splay of different curvatures.
    private static void Arriving(StreamGeometryContext path, int from, int to)
    {
        double x = Centre(from), y = Centre(to);
        var step = Math.Sign(y - x) * Math.Min(Turn, Math.Abs(y - x));
        path.BeginFigure(new Point(x, 0), false);
        path.LineTo(new Point(x, Middle - Turn));
        path.QuadraticBezierTo(new Point(x, Middle), new Point(x + step, Middle));
        path.LineTo(new Point(y, Middle));
        path.EndFigure(false);
    }

    private static void Leaving(StreamGeometryContext path, int from, int to)
    {
        double x = Centre(from), y = Centre(to);
        var step = Math.Sign(y - x) * Math.Min(Turn, Math.Abs(y - x));
        path.BeginFigure(new Point(x, Middle), false);
        path.LineTo(new Point(y - step, Middle));
        path.QuadraticBezierTo(new Point(y, Middle), new Point(y, Middle + Turn));
        path.LineTo(new Point(y, RowHeight));
        path.EndFigure(false);
    }

    private static void Line(StreamGeometryContext path, double x, double from, double to)
    {
        path.BeginFigure(new Point(x, from), false);
        path.LineTo(new Point(x, to));
        path.EndFigure(false);
    }

    /// <summary>How many lanes this row opens below its dot, for the checks.</summary>
    public int BranchCount => row.Branches.Count;

    public override void Render(DrawingContext context)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            if (row.FromAbove) Line(path, Centre(row.Lane), 0, Middle);
            if (row.ToBelow) Line(path, Centre(row.Lane), Middle, RowHeight);
            foreach (var branch in row.Branches) Leaving(path, row.Lane, branch);
            foreach (var edge in row.Edges)
            {
                if (edge.Joins is { } joins) Arriving(path, edge.Lane, joins);
                else Line(path, Centre(edge.Lane), 0, RowHeight);
            }
        }
        using (context.PushClip(new Rect(0, 0, drawn * LaneWidth, RowHeight)))
        {
            context.DrawGeometry(null, new Pen(Ui.Hint, 1.5), geometry);
            context.DrawEllipse(marked ? Ui.Accent : Ui.Hint, null, new Point(Centre(row.Lane), Middle), marked ? 4.5 : 3.5, marked ? 4.5 : 3.5);
        }
    }
}