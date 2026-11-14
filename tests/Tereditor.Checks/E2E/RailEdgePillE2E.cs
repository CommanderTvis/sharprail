using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks.E2E;

/// <summary>
/// In a rail too short for its rows, a pill at the top or bottom edge names the focused row while it is scrolled away, hides once
/// at least half of the row shows, brings the row into view when clicked, and moves no row.
/// </summary>
internal static class RailEdgePillE2E
{
    internal static void Run(string root)
    {
        using var app = OpenFixtureProject(Path.Combine(root, "rail-edge-pill"));
        var made = new List<string>();
        for (var index = 0; index < 8; index++) made.Add(CreateWorkspaceViaDialog(app));
        var scroller = Controls(app).OfType<ScrollViewer>().Single(item => item.Content is StackPanel { Name: "ProjectsTree" });
        var tree = (StackPanel)scroller.Content!;
        // The pill shares the scroller's cell in the Projects panel, so the panel is cut short, not the scroller: a capped scroller
        // would sit centred in a taller cell while the pill keeps to the cell's edges.
        var panel = Controls(app).First(control => control.Name == "ProjectsPanel");
        panel.MaxHeight = panel.Bounds.Height - scroller.Bounds.Height + 110;
        Until(() => scroller.Extent.Height > scroller.Viewport.Height + 200);

        // The newest workspace is listed first, so an older one has rows above and below it.
        var target = made[4];
        app.Click(Select(app, target));
        Until(() => Active(app, target));
        Settle(300);
        var pill = Controls(app).OfType<Button>().Single(button => button.Name == "RailEdgePill");
        Require(!pill.GetVisualAncestors().Contains(tree), "The pill floats over the rail; it is not one of its rows.");

        // The focused row: the open workspace's current tab row when the rail marks one, else the workspace's own row.
        (Control Row, string Name) Focused()
        {
            _ = Controls(app);
            if (tree.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "ActiveWorkspaceTab" && border.IsEffectivelyVisible)?.Parent is Control tab)
                return (tab, tab.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("dock-tab-title")).Text!);
            return (Item(app, target), ItemText(app, target, "WorkspaceName"));
        }
        double Top(Control row) => (row.TranslatePoint(default, tree) ?? default).Y;
        double Shown(Control row)
        {
            var at = (row.TranslatePoint(default, scroller) ?? default).Y;
            return Math.Max(0, Math.Min(at + row.Bounds.Height, scroller.Viewport.Height) - Math.Max(at, 0)) / row.Bounds.Height;
        }
        double MaxOffset() => Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        void ScrollTo(double offset)
        {
            scroller.Offset = new Vector(0, Math.Clamp(offset, 0, MaxOffset()));
            _ = Controls(app);
            Settle(100);
        }
        string PillText() => pill.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "RailEdgePillText").Text ?? "";
        (Size Tree, Rect Scroller, (double Top, double Height)[] Rows) Geometry()
        {
            _ = Controls(app);
            return (tree.Bounds.Size, scroller.Bounds, [.. tree.GetVisualDescendants().OfType<Grid>().Where(item => item.Name == "WorkspaceItem" && item.IsEffectivelyVisible)
                .Select(item => (Top(item), item.Bounds.Height))]);
        }

        // A terminal may retitle itself, so the name is read again wherever the pill is compared with it.
        var row = Focused().Row;
        Require(Focused().Name.Length > 0, "The focused row has a name.");

        // Below the fold: the bottom pill, pointing down, with the row's name.
        ScrollTo(0);
        Until(() => pill.IsEffectivelyVisible && !pill.Classes.Contains("above") && pill.VerticalAlignment == VerticalAlignment.Bottom && PillText() == Focused().Name);
        Require(Shown(row) < 0.5, $"The focused row is below the fold: {Shown(row):0.##} of it shows.");
        Require(Math.Abs((pill.TranslatePoint(new Point(0, pill.Bounds.Height), scroller) ?? default).Y - scroller.Viewport.Height) <= 8,
            $"The bottom pill sits at the rail's bottom edge.: pill {pill.Bounds} in a cell shared with the rail {scroller.Bounds}, viewport {scroller.Viewport}.");
        Require(Named<ContentControl>(pill, "RailEdgePillChevron").RenderTransform is null, "The bottom pill's chevron points down.");
        Require(Equals(ToolTip.GetTip(pill), PillText()), "The pill's tooltip carries the full name.");
        var withPill = Geometry();
        Console.WriteLine("PASS rail edge pill: a focused row below the fold shows the bottom pill with its name and a down chevron");

        // At least half of the row in view hides the pill; less than half shows it.
        var top = Top(row);
        ScrollTo(top - scroller.Viewport.Height + row.Bounds.Height * 0.4);
        Until(() => pill.IsEffectivelyVisible && !pill.Classes.Contains("above"));
        ScrollTo(top - scroller.Viewport.Height + row.Bounds.Height * 0.6);
        Until(() => !pill.IsVisible);
        ScrollTo(top - 4);
        Until(() => !pill.IsVisible && Shown(row) >= 0.99);
        var withoutPill = Geometry();
        static bool Same((double Top, double Height)[] first, (double Top, double Height)[] second) => first.Length == second.Length &&
            first.Zip(second).All(pair => Math.Abs(pair.First.Top - pair.Second.Top) <= 0.5 && Math.Abs(pair.First.Height - pair.Second.Height) <= 0.5);
        Require(withPill.Tree == withoutPill.Tree && withPill.Scroller == withoutPill.Scroller && Same(withPill.Rows, withoutPill.Rows),
            "Rows keep their places whether the pill shows or not.");
        Console.WriteLine("PASS rail edge pill: the pill hides once half of the focused row shows, and moves no row");

        // Above the fold: the top pill, pointing up; a click brings the row into view and hides the pill.
        ScrollTo(MaxOffset());
        Until(() => pill.IsEffectivelyVisible && pill.Classes.Contains("above") && pill.VerticalAlignment == VerticalAlignment.Top && PillText() == Focused().Name);
        Require(Shown(row) < 0.5, $"The focused row is above the fold: {Shown(row):0.##} of it shows.");
        Require(Math.Abs((pill.TranslatePoint(default, scroller) ?? default).Y) <= 8, "The top pill sits at the rail's top edge.");
        Require(Named<ContentControl>(pill, "RailEdgePillChevron").RenderTransform is not null, "The top pill's chevron points up.");
        var before = scroller.Offset.Y;
        app.Click(pill);
        Until(() => !pill.IsVisible && Shown(row) >= 0.99);
        Require(scroller.Offset.Y < before, "The click scrolled the rail up to the row.");
        Require(Same(withPill.Rows, Geometry().Rows), "Rows keep their places after the pill's scroll.");
        Console.WriteLine("PASS rail edge pill: a focused row above the fold shows the top pill; clicking it brings the row into view and hides it");
    }
}
