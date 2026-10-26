// Resize projection adapted from react-resizable-panels 2.1.9.
// Copyright (c) 2023 Brian Vaughn; MIT license in licenses/React-Resizable-Panels.txt.
namespace SharpRail.UI.Docking;

internal sealed class SideGeometry
{
    private readonly bool left, right;
    private readonly double[] sizes;
    private readonly PanelConstraint[] constraints;
    private readonly int centerIndex;

    /// <summary>The narrowest a side tool pane may be, in pixels: wide enough for its header, toolbar and list rows.</summary>
    public const double MinPaneWidth = 220;

    /// <summary>The smallest fraction of <paramref name="width"/> a side pane may take: <see cref="MinPaneWidth"/>, never under 8% nor over 35%.</summary>
    public static double SideMinimum(double width) => width > 0 ? Math.Clamp(MinPaneWidth / width, .08, .35) : .08;

    public SideGeometry(DockState state, double width)
    {
        var sideMinimum = SideMinimum(width);
        left = Shown(state, "left");
        right = Shown(state, "right");
        var leftTarget = left ? state.LeftWidth : 0;
        var rightTarget = right ? state.RightWidth : 0;
        var centerMinimum = Math.Min(1 - (left ? sideMinimum : 0) - (right ? sideMinimum : 0), width > 0 ? 320 / width : .1);
        centerIndex = left ? 1 : 0;
        constraints = [.. left ? new[] { new PanelConstraint(sideMinimum, Collapsible: true) } : [],
            new(centerMinimum), .. right ? new[] { new PanelConstraint(sideMinimum, Collapsible: true) } : []];
        sizes = PanelProjection.Validate([.. left ? new[] { leftTarget } : [],
            1 - leftTarget - rightTarget, .. right ? new[] { rightTarget } : []], constraints);
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