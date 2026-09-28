// Constraint projection adapted from react-resizable-panels 2.1.9.
// Copyright (c) 2023 Brian Vaughn; MIT license in licenses/React-Resizable-Panels.txt.
namespace SharpRail.UI.Docking;

internal readonly record struct PanelConstraint(double Minimum, double Maximum = 1, bool Collapsible = false);

internal static class PanelProjection
{
    private static double Safe(double value, PanelConstraint constraint)
    {
        if (Math.Round(value, 12) < Math.Round(constraint.Minimum, 12))
            value = constraint.Collapsible && Math.Round(value, 12) < Math.Round(constraint.Minimum / 2, 12) ? 0 : constraint.Minimum;
        return Math.Round(Math.Min(constraint.Maximum, value), 12);
    }

    public static double[] Validate(double[] sizes, PanelConstraint[] constraints)
    {
        var total = sizes.Sum();
        var next = sizes.Select(value => value / total).ToArray();
        var remaining = 0d;
        for (var i = 0; i < next.Length; i++)
        {
            var safe = Safe(next[i], constraints[i]);
            remaining += next[i] - safe;
            next[i] = safe;
        }
        for (var i = 0; i < next.Length && Math.Abs(remaining) > 1e-10; i++)
        {
            var safe = Safe(next[i] + remaining, constraints[i]);
            remaining -= safe - next[i];
            next[i] = safe;
        }
        return next;
    }

    public static double[] Adjust(double[] initial, PanelConstraint[] constraints, int pivot, double delta)
    {
        if (Math.Abs(delta) < 1e-10) return initial;
        var next = (double[])initial.Clone();
        var grow = delta < 0 ? pivot + 1 : pivot;
        var shrink = delta < 0 ? pivot : pivot + 1;
        var direction = delta < 0 ? -1 : 1;
        var available = 0d;
        for (var i = grow; i >= 0 && i < next.Length; i -= direction)
            available += Safe(1, constraints[i]) - initial[i];
        var requested = Math.Min(Math.Abs(delta), Math.Abs(available));
        var applied = 0d;
        for (var i = shrink; i >= 0 && i < next.Length; i += direction)
        {
            var safe = Safe(initial[i] - (requested - applied), constraints[i]);
            applied += initial[i] - safe;
            next[i] = safe;
            if (applied >= requested - 1e-10) break;
        }
        var wanted = initial[grow] + applied;
        next[grow] = Safe(wanted, constraints[grow]);
        var remaining = wanted - next[grow];
        for (var i = grow; i >= 0 && i < next.Length && Math.Abs(remaining) > 1e-10; i -= direction)
        {
            var safe = Safe(next[i] + remaining, constraints[i]);
            remaining -= safe - next[i];
            next[i] = safe;
        }
        return Math.Abs(next.Sum() - 1) < 1e-9 ? next : initial;
    }
}
