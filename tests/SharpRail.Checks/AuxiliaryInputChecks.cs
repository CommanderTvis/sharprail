using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class AuxiliaryInputChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        var profilePath = root + "-auxiliary-input";
        var window = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(profilePath));
        window.Show(); window.Width = 1600; window.Height = 1040;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!window.WorkspaceMounted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Require(window.WorkspaceMounted, "Auxiliary resize workspace did not mount.");
        T Find<T>(string name) where T : Control
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            return window.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
        }
        void Load(string region, int count, Func<int, bool> folded)
        {
            var preset = DockState.Preset("balanced");
            var tools = preset.Groups.Where(group => group.Region == region).SelectMany(group => group.Tools).ToList();
            preset.Groups.RemoveAll(group => group.Region == region);
            for (var i = 0; i < count; i++)
                preset.Groups.Add(new DockGroup { Region = region, Tools = i == 0 ? tools : [], Folded = folded(i), Weight = count == 3 ? new[] { .2, .3, .5 }[i] : 1d / count });
            preset.BottomVisible = region == "bottom"; preset.BottomAlignment = "full";
            preset.SideLimit = 32; preset.BottomLimit = 32;
            window.Layout.ApplyPreset(preset);
        }
        foreach (var region in new[] { "left", "right", "bottom" })
            foreach (var foldedIndex in new[] { 0, 1 })
            {
                Load(region, 3, i => i == foldedIndex);
                var horizontal = region == "bottom";
                var grid = Find<Grid>("AuxiliaryStack_" + region);
                var extent = horizontal ? grid.Bounds.Width : grid.Bounds.Height;
                Require(extent == (horizontal ? 1600 : 1000), "Reference auxiliary fixture has the wrong extent.");
                var expected = (horizontal, foldedIndex) switch
                {
                    (false, 0) => new[] { .027, .368405078597, .604594921403 },
                    (false, 1) => new[] { .285242090784, .027, .687757909216 },
                    (true, 0) => new[] { .016875, .371036247131, .612088752869 },
                    _ => new[] { .285653334786, .016875, .697471665214 }
                };
                double Fraction(int i) => (horizontal ? grid.ColumnDefinitions[i * 2].Width.Value : grid.RowDefinitions[i * 2].Height.Value) / (extent - 2);
                for (var i = 0; i < 3; i++)
                    Require(Math.Abs(Fraction(i) - expected[i]) < 1e-8,
                        $"Auxiliary projection differs from the reference library for {region}/{foldedIndex}/{i}.");
                var groups = window.Layout.State.Groups.Where(group => group.Region == region).ToArray();
                var saved = groups.Select(group => group.Weight).ToArray();
                var epoch = window.Layout.Epoch;
                var name = "AuxiliarySeparator_" + groups[1].Id + "_" + groups[2].Id;
                var handle = Find<ResizeHandle>(name);
                Require(handle.IsEnabled, "A separator beside a folded pane must remain usable when two expanded panes can resize.");
                var origin = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
                var destination = origin + (horizontal ? new Vector(extent * .1, 0) : new Vector(0, extent * .1));
                window.MouseDown(origin, MouseButton.Left); window.MouseMove(destination);
                Require(handle.IsActive && window.Layout.Epoch == epoch && groups.Select(group => group.Weight).SequenceEqual(saved),
                    "Auxiliary resize draft persisted pane weights.");
                for (var i = 0; i < 3; i++)
                    Require(Math.Abs(Fraction(i) - expected[i] - (i == foldedIndex ? 0 : i == 2 ? -.1 : .1)) < 1e-8,
                        "Auxiliary resize did not transfer space across its fixed folded pane.");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.MouseUp(destination, MouseButton.Left);
                Require(window.Layout.Epoch == epoch, "Canceling an auxiliary resize changed saved geometry.");
                handle = Find<ResizeHandle>(name);
                origin = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
                destination = origin + (horizontal ? new Vector(extent * .1, 0) : new Vector(0, extent * .1));
                window.MouseDown(origin, MouseButton.Left); window.MouseMove(destination); window.MouseUp(destination, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                var committed = window.Layout.State.Groups.Where(group => group.Region == region).ToArray();
                Require(committed[foldedIndex].Weight == saved[foldedIndex] && Math.Abs(committed.Sum(group => group.Weight) - 1) < 1e-8 &&
                    committed[foldedIndex == 0 ? 1 : 0].Weight > saved[foldedIndex == 0 ? 1 : 0] && committed[2].Weight < saved[2],
                    "Auxiliary resize did not preserve folded weights while committing expanded proportions.");
                var persisted = new ProfileStore(profilePath).Data.Layout.Groups.Where(group => group.Region == region).ToArray();
                Require(persisted.Select(group => group.Weight).SequenceEqual(committed.Select(group => group.Weight)),
                    "Auxiliary resized weights did not persist.");
            }
        foreach (var region in new[] { "left", "right", "bottom" })
        {
            Load(region, 3, _ => true);
            var grid = Find<Grid>("AuxiliaryStack_" + region);
            var extent = region == "bottom" ? grid.Bounds.Width : grid.Bounds.Height;
            var spacer = region == "bottom" ? grid.ColumnDefinitions[^1].Width.Value : grid.RowDefinitions[^1].Height.Value;
            Require(Math.Abs(spacer / (extent - 2) - (1 - 81 / extent)) < 1e-8 &&
                grid.GetLogicalDescendants().OfType<ResizeHandle>().All(handle => !handle.IsEnabled),
                "All-folded auxiliary panes must retain their trailing spacer and disable separators.");
        }
        window.Height = 540;
        foreach (var region in new[] { "left", "right", "bottom" })
            foreach (var allFolded in new[] { true, false })
            {
                Load(region, 32, i => allFolded || i % 2 == 0);
                var grid = Find<Grid>("AuxiliaryStack_" + region);
                var extent = region == "bottom" ? grid.Bounds.Width : grid.Bounds.Height;
                if (region == "bottom") window.Width = 800;
                grid = Find<Grid>("AuxiliaryStack_" + region);
                extent = region == "bottom" ? grid.Bounds.Width : grid.Bounds.Height;
                Require(grid.GetLogicalDescendants().OfType<ResizeHandle>().All(handle => !handle.IsEnabled),
                    "Auxiliary separators must be disabled when the viewport cannot satisfy expanded minima.");
                var sizes = region == "bottom" ? grid.ColumnDefinitions.Where((_, i) => i % 2 == 0).Select(column => column.ActualWidth) :
                    grid.RowDefinitions.Where((_, i) => i % 2 == 0).Select(row => row.ActualHeight);
                Require(Math.Abs(sizes.Sum() + 31 - extent) <= 1 && sizes.All(size => size >= 0 && size <= 27),
                    $"Narrow auxiliary projection overflowed: {region}, folded={allFolded}, extent={extent}, sum={sizes.Sum()}, min={sizes.Min()}, max={sizes.Max()}.");
                Require(window.Layout.State.Groups.Where(group => group.Region == region).All(group => group.Weight == 1d / 32),
                    "Viewport compression rewrote saved auxiliary weights.");
            }
        window.Close();
        Console.WriteLine("PASS auxiliary source projection, folded-neighbor resizing, persistence, spacer and narrow viewport compression");
    }
}
