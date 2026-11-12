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
        var profile = new ProfileStore(profilePath);
        E2E.E2eWorkspace.EnableSpecPlugins(profile.OpenState());
        var window = new WorkbenchWindow(new ProjectServices(root), root, profile, E2E.E2eTerminals.Plain);
        window.Show(); window.Width = 1600 + 2 * DockSurface.RailWidth; window.Height = 1000 + window.FindControl<Grid>("WorkbenchRoot")!.RowDefinitions[0].Height.Value;
        var deadline = Awake.Now.AddSeconds(15);
        while (!window.WorkspaceMounted && Awake.Now < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
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
            preset.SideLimit = 32;
            window.Layout.ApplyPreset(preset);
        }
        // A folded side pane occupies nothing: the stack projects only the expanded panes' saved weights.
        foreach (var region in new[] { "left", "right" })
            foreach (var foldedIndex in new[] { 0, 1 })
            {
                Load(region, 3, i => i == foldedIndex);
                var grid = Find<Grid>("AuxiliaryStack_" + region);
                var weights = new[] { .2, .3, .5 }.Where((_, i) => i != foldedIndex).ToArray();
                Require(grid.RowDefinitions.Count == 3 && Enumerable.Range(0, 2).All(i =>
                        Math.Abs(grid.RowDefinitions[i * 2].Height.Value / (grid.Bounds.Height - 1) - weights[i] / weights.Sum()) < 1e-8),
                    $"A folded side pane must take no interior space: {region}/{foldedIndex}.");
                var groups = window.Layout.State.Groups.Where(group => group.Region == region).ToArray();
                var expanded = groups.Where((_, i) => i != foldedIndex).ToArray();
                var handle = Find<ResizeHandle>("AuxiliarySeparator_" + expanded[0].Id + "_" + expanded[1].Id);
                var origin = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
                var destination = origin + new Vector(0, grid.Bounds.Height * .1);
                window.MouseDown(origin, MouseButton.Left); window.MouseMove(destination); window.MouseUp(destination, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                var committed = window.Layout.State.Groups.Where(group => group.Region == region).ToArray();
                Require(committed[foldedIndex].Weight == groups[foldedIndex].Weight && Math.Abs(committed.Sum(group => group.Weight) - 1) < 1e-8 &&
                    committed.First(group => group.Id == expanded[0].Id).Weight > expanded[0].Weight,
                    "Side resize must keep a folded pane's weight while committing the expanded proportions.");
                Require(Find<Button>("RailPane_" + groups[1].Id) is var entry && Equals(entry.Tag, foldedIndex == 1 ? null : "pressed"),
                    "A folded pane's rail entry must not be pressed; an expanded one's must be.");
            }
        foreach (var region in new[] { "left", "right" })
        {
            Load(region, 3, _ => true);
            Require(!window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "AuxiliaryStack_" + region) &&
                window.Layout.State.Groups.Where(group => group.Region == region).All(group => Find<Button>(group.Tools.Count > 0 ? "Tab_" + group.Tools[0].Id.Replace(':', '_') : "RailPane_" + group.Id).Tag is null),
                "An all-folded side keeps only its rail, with no entry pressed.");
        }
        window.Close();
        Console.WriteLine("PASS auxiliary expanded projection, folded-neighbor resizing and persistent rail entries");
    }
}