// Resize projection adapted from react-resizable-panels 2.1.9.
// Copyright (c) 2023 Brian Vaughn; MIT license in licenses/React-Resizable-Panels.txt.
namespace SharpRail.UI.Docking;

internal sealed class SideGeometry
{
    private readonly bool left, right;
    private readonly double[] sizes;
    private readonly PanelConstraint[] constraints;
    private readonly int centerIndex;

    public SideGeometry(DockState state, double width)
    {
        left = state.LeftVisible && state.Groups.Any(group => group.Region == "left");
        right = state.RightVisible && state.Groups.Any(group => group.Region == "right");
        var leftTarget = left ? state.LeftWidth : 0;
        var rightTarget = right ? state.RightWidth : 0;
        var centerMinimum = Math.Min(1 - (left ? .08 : 0) - (right ? .08 : 0), width > 0 ? 320 / width : .1);
        centerIndex = left ? 1 : 0;
        constraints = [.. left ? new[] { new PanelConstraint(.08, Collapsible: true) } : [],
            new(centerMinimum), .. right ? new[] { new PanelConstraint(.08, Collapsible: true) } : []];
        sizes = PanelProjection.Validate([.. left ? new[] { leftTarget } : [],
            1 - leftTarget - rightTarget, .. right ? new[] { rightTarget } : []], constraints);
    }

    public (double Left, double Center, double Right) Project() => Project(sizes);

    public (double Left, double Center, double Right) Resize(string side, double delta) =>
        Project(PanelProjection.Adjust(sizes, constraints, side == "left" ? 0 : sizes.Length - 2, delta));

    private (double Left, double Center, double Right) Project(double[] projected) =>
        (left ? projected[0] : 0, projected[centerIndex], right ? projected[^1] : 0);
}