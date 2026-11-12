// Resize projection adapted from react-resizable-panels 2.1.9.
// Copyright (c) 2023 Brian Vaughn; MIT license in licenses/React-Resizable-Panels.txt.
namespace Tereditor.UI.Docking;

internal sealed class SideGeometry
{
    private readonly bool left, right;
    private readonly double[] sizes;
    private readonly PanelConstraint[] constraints;
    private readonly int centerIndex;

    /// <summary>The narrowest a side tool pane may be, in pixels: wide enough for its header, toolbar and list rows.</summary>
    public const double MinPaneWidth = 220;

    /// <summary>
    /// The smallest fraction of <paramref name="width"/> a side pane may take: <see cref="MinPaneWidth"/>, at most 35%. A frame too
    /// narrow to hold two such sides and the 320px centre keeps the old 8% floor instead, so a narrow window never squeezes its sides.
    /// </summary>
    public static double SideMinimum(double width) =>
        width > 0 && 2 * MinPaneWidth + CenterMinimumPixels <= width ? Math.Min(.35, MinPaneWidth / width) : .08;

    private const double CenterMinimumPixels = 320;

    /// <summary>
    /// The narrowest the fixed Projects rail gets: <see cref="MinPaneWidth"/>, at most 40% of the frame. Unlike a tool pane it never
    /// folds to nothing, because the window's header and its traffic-light inset live in it.
    /// </summary>
    public static double FixedLeftMinimum(double width) => width > 0 ? Math.Min(.4, MinPaneWidth / width) : .08;

    public SideGeometry(DockState state, double width, bool fixedLeft = false)
    {
        var sideMinimum = SideMinimum(width);
        left = Shown(state, "left");
        right = Shown(state, "right");
        var leftTarget = left ? state.LeftWidth : 0;
        var rightTarget = right ? state.RightWidth : 0;
        if (fixedLeft && left)
        {
            // A narrow frame gives room up in a fixed order: the right side shrinks to its minimum, then folds away, then the rail
            // shrinks to its own minimum, and only last does the main column go under its own minimum. The rail is never hidden.
            sizes = Narrow(leftTarget, rightTarget, right, width, sideMinimum);
            centerIndex = 1;
            constraints = [new PanelConstraint(FixedLeftMinimum(width)), new(.01), .. right ? new[] { new PanelConstraint(sideMinimum, Collapsible: true) } : []];
            return;
        }
        var centerMinimum = Math.Min(1 - (left ? sideMinimum : 0) - (right ? sideMinimum : 0), width > 0 ? CenterMinimumPixels / width : .1);
        centerIndex = left ? 1 : 0;
        constraints = [.. left ? new[] { new PanelConstraint(sideMinimum, Collapsible: true) } : [],
            new(centerMinimum), .. right ? new[] { new PanelConstraint(sideMinimum, Collapsible: true) } : []];
        sizes = PanelProjection.Validate([.. left ? new[] { leftTarget } : [],
            1 - leftTarget - rightTarget, .. right ? new[] { rightTarget } : []], constraints);
    }

    private static double[] Narrow(double leftTarget, double rightTarget, bool right, double width, double sideMinimum)
    {
        var railMinimum = FixedLeftMinimum(width);
        var rightMinimum = right ? Math.Max(sideMinimum, width > 0 ? Math.Min(.35, MinPaneWidth / width) : sideMinimum) : 0;
        var centerMinimum = width > 0 ? CenterMinimumPixels / width : .1;
        var rail = Math.Clamp(leftTarget, railMinimum, .7);
        var side = right ? Math.Max(rightMinimum, Math.Min(rightTarget, 1 - rail)) : 0;
        var short1 = centerMinimum - (1 - rail - side);
        if (short1 > 1e-12 && right) side = side - short1 >= rightMinimum ? side - short1 : 0;
        var short2 = centerMinimum - (1 - rail - side);
        if (short2 > 1e-12) rail = Math.Max(railMinimum, rail - short2);
        return right ? [rail, 1 - rail - side, side] : [rail, 1 - rail];
    }

    public (double Left, double Center, double Right) Project() => Project(sizes);

    /// <summary>Folded panes live only on the side rail and take no interior width.</summary>
    public static bool Shown(DockState state, string side) => (side == "left" ? state.LeftVisible : state.RightVisible) &&
        state.Groups.Any(group => group.Region == side && !group.Folded);

    public (double Left, double Center, double Right) Resize(string side, double delta) =>
        Project(PanelProjection.Adjust(sizes, constraints, side == "left" ? 0 : sizes.Length - 2, delta));

    private (double Left, double Center, double Right) Project(double[] projected) =>
        (left ? projected[0] : 0, projected[centerIndex], right ? projected[^1] : 0);
}