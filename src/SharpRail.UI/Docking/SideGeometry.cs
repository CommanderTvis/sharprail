// Resize projection adapted from react-resizable-panels 2.1.9.
// Copyright (c) 2023 Brian Vaughn; MIT license in licenses/React-Resizable-Panels.txt.
namespace SharpRail.UI.Docking;

internal sealed class SideGeometry
{
    private readonly bool left, right, outerLeft, outerRight;
    private readonly double leftTarget, rightTarget, centerMinimum;
    private readonly double[] outer, outerMinimums;
    private readonly bool[] outerCollapsible;
    private readonly int alignedIndex;

    public SideGeometry(DockState state, double width)
    {
        left = state.LeftVisible && state.Groups.Any(group => group.Region == "left");
        right = state.RightVisible && state.Groups.Any(group => group.Region == "right");
        outerLeft = left && state.BottomAlignment is not ("full" or "center-left");
        outerRight = right && state.BottomAlignment is not ("full" or "center-right");
        leftTarget = left ? state.LeftWidth : 0;
        rightTarget = right ? state.RightWidth : 0;
        centerMinimum = Math.Min(1 - (left ? .08 : 0) - (right ? .08 : 0), width > 0 ? 320 / width : .1);
        alignedIndex = outerLeft ? 1 : 0;
        outer = [.. outerLeft ? new[] { leftTarget } : [],
            1 - (outerLeft ? leftTarget : 0) - (outerRight ? rightTarget : 0),
            .. outerRight ? new[] { rightTarget } : []];
        outerMinimums = [.. outerLeft ? new[] { .08 } : [],
            centerMinimum + (left && !outerLeft ? .08 : 0) + (right && !outerRight ? .08 : 0),
            .. outerRight ? new[] { .08 } : []];
        outerCollapsible = [.. outerLeft ? new[] { true } : [], false, .. outerRight ? new[] { true } : []];
        outer = Validate(outer, outerMinimums, outerCollapsible);
    }

    public (double Left, double Center, double Right) Project() => Project(outer, null);

    public (double Left, double Center, double Right) Resize(string side, double delta)
    {
        var isOuter = side == "left" ? outerLeft : outerRight;
        if (isOuter)
        {
            var pivot = side == "left" ? 0 : outer.Length - 2;
            return Project(Adjust(outer, outerMinimums, outerCollapsible, pivot, delta), null);
        }
        var alignedWidth = outer[alignedIndex];
        var row = Row(alignedWidth);
        var rowPivot = side == "left" ? 0 : row.Sizes.Length - 2;
        return Project(outer, Adjust(row.Sizes, row.Minimums, row.Collapsible, rowPivot, delta / alignedWidth));
    }

    private (double[] Sizes, double[] Minimums, bool[] Collapsible) Row(double width)
    {
        var rowLeft = left && !outerLeft;
        var rowRight = right && !outerRight;
        double[] sizes = [.. rowLeft ? new[] { leftTarget } : [],
            Math.Max(double.Epsilon, width - (rowLeft ? leftTarget : 0) - (rowRight ? rightTarget : 0)),
            .. rowRight ? new[] { rightTarget } : []];
        double[] minimums = [.. rowLeft ? new[] { Math.Min(1, .08 / width) } : [],
            Math.Min(1, centerMinimum / width), .. rowRight ? new[] { Math.Min(1, .08 / width) } : []];
        bool[] collapsible = [.. rowLeft ? new[] { true } : [], false, .. rowRight ? new[] { true } : []];
        return (Validate(sizes, minimums, collapsible), minimums, collapsible);
    }

    private (double Left, double Center, double Right) Project(double[] outside, double[]? inside)
    {
        var width = outside[alignedIndex];
        var row = inside ?? Row(width).Sizes;
        var centerIndex = left && !outerLeft ? 1 : 0;
        return (outerLeft ? outside[0] : left ? row[0] * width : 0,
            row[centerIndex] * width, outerRight ? outside[^1] : right ? row[^1] * width : 0);
    }

    private static PanelConstraint[] Constraints(double[] minimums, bool[] collapsible) =>
        minimums.Select((minimum, i) => new PanelConstraint(minimum, Collapsible: collapsible[i])).ToArray();

    private static double[] Validate(double[] sizes, double[] minimums, bool[] collapsible) =>
        PanelProjection.Validate(sizes, Constraints(minimums, collapsible));

    private static double[] Adjust(double[] sizes, double[] minimums, bool[] collapsible, int pivot, double delta) =>
        PanelProjection.Adjust(sizes, Constraints(minimums, collapsible), pivot, delta);
}
