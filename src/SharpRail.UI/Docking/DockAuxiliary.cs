using Avalonia.Controls;
using Avalonia.LogicalTree;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Docking;

public sealed partial class DockSurface
{
    private Control BuildAuxiliary(string region)
    {
        var groups = Session.State.Groups.Where(group => group.Region == region).ToArray();
        var horizontal = region == "bottom";
        var grid = new Grid { Name = "AuxiliaryStack_" + region, ClipToBounds = true, UseLayoutRounding = false };
        var handles = new List<ResizeHandle>();
        double Extent() => horizontal ? grid.Bounds.Width : grid.Bounds.Height;
        AuxiliaryGeometry Geometry() => new(groups, Extent());
        void Preview(double[] sizes)
        {
            var available = Math.Max(0, Extent() - (groups.Length - 1));
            for (var i = 0; i < groups.Length; i++)
            {
                var length = new GridLength(sizes[i] * available);
                if (horizontal) grid.ColumnDefinitions[i * 2].Width = length;
                else grid.RowDefinitions[i * 2].Height = length;
            }
            if (groups.All(group => group.Folded))
            {
                var spacer = new GridLength((sizes.Length > groups.Length ? sizes[^1] : 0) * available);
                if (horizontal) grid.ColumnDefinitions[^1].Width = spacer;
                else grid.RowDefinitions[^1].Height = spacer;
            }
        }
        void Commit(double[] sizes)
        {
            var total = groups.Sum(group => group.Weight);
            var available = total - groups.Where(group => group.Folded).Sum(group => group.Weight);
            var expanded = groups.Select((group, i) => group.Folded ? 0 : sizes[i]).Sum();
            if (expanded <= 0) return;
            Session.Geometry(state =>
            {
                for (var i = 0; i < groups.Length; i++)
                    if (!groups[i].Folded)
                        state.Groups.Single(group => group.Id == groups[i].Id).Weight = sizes[i] / expanded * available;
            });
        }
        for (var i = 0; i < groups.Length; i++)
        {
            if (horizontal) grid.ColumnDefinitions.Add(new(new GridLength(0)));
            else grid.RowDefinitions.Add(new(new GridLength(0)));
            var control = BuildGroup(groups[i]);
            control.ClipToBounds = true;
            Ui.Place(grid, control, horizontal ? 0 : i * 2, horizontal ? i * 2 : 0);
            if (i == groups.Length - 1) continue;
            if (horizontal) grid.ColumnDefinitions.Add(new(new GridLength(1)));
            else grid.RowDefinitions.Add(new(new GridLength(1)));
            var pivot = i;
            AuxiliaryGeometry? gesture = null;
            double[] Change(double delta) => (gesture ??= Geometry()).Resize(pivot, delta / Math.Max(1, Extent()));
            var splitter = Separator(horizontal, delta => Preview(Change(delta)), delta =>
            {
                if (Geometry().CanResize) Commit(Change(delta));
            }, Rebuild);
            splitter.Name = "AuxiliarySeparator_" + groups[i].Id + "_" + groups[i + 1].Id;
            splitter.ConfigureRange(() => Geometry().Range(pivot), value =>
            {
                var current = Geometry();
                Commit(current.Resize(pivot, value - current.Sizes[pivot]));
            });
            handles.Add(splitter);
            Ui.Place(grid, splitter, horizontal ? 0 : i * 2 + 1, horizontal ? i * 2 + 1 : 0);
        }
        if (groups.All(group => group.Folded))
        {
            if (horizontal) grid.ColumnDefinitions.Add(new(new GridLength(0)));
            else grid.RowDefinitions.Add(new(new GridLength(0)));
        }
        void Project()
        {
            var geometry = Geometry();
            if (!grid.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.IsActive)) Preview(geometry.Sizes);
            foreach (var handle in handles) handle.IsEnabled = geometry.CanResize;
        }
        grid.SizeChanged += (_, _) => Project();
        Project();
        return grid;
    }
}
