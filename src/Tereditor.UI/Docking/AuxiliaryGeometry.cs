namespace SharpRail.UI.Docking;

internal sealed class AuxiliaryGeometry
{
    public double[] Sizes { get; }
    public PanelConstraint[] Constraints { get; }
    public bool CanResize { get; }

    public AuxiliaryGeometry(DockGroup[] groups, double extent)
    {
        var folded = groups.Count(group => group.Folded);
        var expanded = groups.Length - folded;
        var room = extent >= folded * 27 + expanded * 120;
        var equal = 1d / Math.Max(1, groups.Length);
        var requestedFold = extent > 0 ? 27 / extent : .04;
        var fold = room ? requestedFold : Math.Min(requestedFold, equal);
        var minimum = room && extent > 0 ? 120 / extent : Math.Min(.04, equal);
        var total = groups.Sum(group => group.Weight);
        var sizes = groups.Select(group => group.Folded ? fold : group.Weight / total).ToList();
        var constraints = groups.Select(group => group.Folded ? new PanelConstraint(fold, fold) : new PanelConstraint(minimum)).ToList();
        if (expanded == 0 && 1 - folded * fold > double.Epsilon)
        {
            var spacer = Math.Max(0, 1 - folded * fold);
            sizes.Add(spacer); constraints.Add(new(spacer, spacer));
        }
        Constraints = constraints.ToArray();
        Sizes = PanelProjection.Validate(sizes.ToArray(), Constraints);
        CanResize = room && expanded >= 2;
    }

    public double[] Resize(int pivot, double delta) => CanResize ? PanelProjection.Adjust(Sizes, Constraints, pivot, delta) : Sizes;

    public (double Value, double Minimum, double Maximum) Range(int pivot)
    {
        var constraint = Constraints[pivot];
        var minimum = Math.Max(constraint.Minimum, 1 - Constraints.Where((_, i) => i != pivot).Sum(item => item.Maximum));
        var maximum = Math.Min(constraint.Maximum, 1 - Constraints.Where((_, i) => i != pivot).Sum(item => item.Minimum));
        return CanResize ? (Sizes[pivot], minimum, maximum) : (Sizes[pivot], Sizes[pivot], Sizes[pivot]);
    }
}