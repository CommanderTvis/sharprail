using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;


using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>A tab listed under another project's workspace in Projects opens that project, not just its folder.</summary>
internal static class CrossProjectTabsE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "cross-project-tabs-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "cross-project-tabs"));
        var first = app.Window.ProjectRoot;
        var second = Path.Combine(root, "cross-project-tabs", "second-project");
        IsolatedGit.Repository(second);
        File.WriteAllText(Path.Combine(second, "README.md"), "second\n");
        File.WriteAllText(Path.Combine(second, "OTHER.md"), "other\n");
        IsolatedGit.Run(second, "add", "-A");
        IsolatedGit.Run(second, "commit", "-m", "second");
        _ = app.Window.OpenProjectAsync(second);
        Until(() => app.Window.WorkspaceMounted && app.Window.ProjectRoot == second && !app.Window.AtProjectHome);
        app.Open("README.md", true);
        // The main column holds one group, so the read-only list under this workspace shows one section.
        app.Open("OTHER.md", true);
        app.ShowTool("projects");
        _ = app.Window.OpenProjectAsync(first);
        Until(() => app.Window.WorkspaceMounted && app.Window.ProjectRoot == first);
        Button Preview() => app.Window.GetLogicalDescendants().OfType<ContentControl>()
            .Single(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, second))
            .GetLogicalDescendants().OfType<Button>().First(button => button.Name == "WorkspaceTabPreview");
        Until(() => app.Window.GetLogicalDescendants().OfType<ContentControl>().Any(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, second) &&
            host.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "WorkspaceTabPreview")));
        var listed = app.Window.GetLogicalDescendants().OfType<ContentControl>().Single(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, second));
        var sections = listed.GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Name == "WorkspaceTabsPreviewGroup").ToArray();
        Require(sections.Length == 1 && sections.All(section => section.Children.OfType<Button>().Count(button =>
            button.Background is Avalonia.Media.ISolidColorBrush { Color.A: > 0 }) == 1),
            "Another workspace's tabs keep their group, with its selected tab marked, as the live strip draws it.");
        Console.WriteLine("PASS another workspace's tabs in Projects keep their groups and selected tabs");
        TerminalStaysHome(app, first, second, Preview);
        app.Click(Preview());
        Until(() => app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == second);
        Settle(500);
        Require(app.Window.ProjectRoot == second,
            $"Choosing another project's tab in Projects opens that project, not only its folder (project is {app.Window.ProjectRoot}).");
        Console.WriteLine("PASS a tab under another project's workspace opens that project");
    }

    // A terminal tab moves only within its own workspace: dragged onto another workspace's row in Projects it paints no
    // destination, shows the not-allowed pointer and stays where it was; dragged within its strip it still reorders.
    private static void TerminalStaysHome(E2eWorkspace app, string home, string other, Func<Button> preview)
    {
        app.ShowTool("files"); app.Open("README.md", true); app.ShowTool("projects");
        Until(() => app.Window.GetLogicalDescendants().OfType<ContentControl>().Any(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, other)));
        app.Window.Layout.NewTerminal(app.Center);
        var terminal = app.Tabs.Single(tab => tab.Kind == "terminal").Id;
        Settle(550);
        var surface = app.Find<DockSurface>("WorkspaceWorkbench");
        var overlay = surface.Children.OfType<Canvas>().Single();
        Button Terminal() => app.Find<Button>("Tab_" + terminal.Replace(':', '_'));
        Point Middle(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), app.Window)!.Value;
        var start = Middle(Terminal());
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(12, 8)); Settle();
        Require(surface.IsDragging, "Pointer movement must start the terminal's drag.");
        var row = preview();
        var over = Middle(row);
        app.Window.MouseMove(over); Settle();
        var rowBounds = new Rect(row.TranslatePoint(default, surface)!.Value, row.Bounds.Size);
        Require(surface.Cursor is not null && !overlay.Children.OfType<Border>().Any(hint => hint.Name != "DragPreview" &&
            new Rect(Canvas.GetLeft(hint), Canvas.GetTop(hint), hint.Width, hint.Height).Intersects(rowBounds)),
            "Another workspace's row in Projects must offer a terminal no destination and show the not-allowed pointer: cursor=" + surface.Cursor + " row=" + rowBounds + " hints=" +
            string.Join(";", overlay.Children.OfType<Border>().Select(hint => hint.Name + new Rect(Canvas.GetLeft(hint), Canvas.GetTop(hint), hint.Width, hint.Height))));
        app.Window.MouseUp(over, MouseButton.Left); Settle();
        Require(app.Window.WorkspaceRoot == home && surface.Cursor is null && app.Tabs.Any(tab => tab.Id == terminal) &&
            app.Window.Layout.State.Workspaces.GetValueOrDefault(other)?.Documents.Values.Any(tabs => tabs.Any(tab => tab.Id == terminal)) != true &&
            app.Workbench.TerminalCatalog?.Workspaces.GetValueOrDefault(other)?.Any(tab => tab.Key == terminal) != true,
            "A terminal dropped on another workspace's row must stay in its own workspace.");
        var readme = app.Tab("README.md");
        start = Middle(Terminal());
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(4, -12)); Settle();
        var before = readme.TranslatePoint(new Point(readme.Bounds.Width / 2, 2), app.Window)!.Value;
        app.Window.MouseMove(before); Settle();
        Require(surface.Cursor is null, "A legal destination in the terminal's own workspace keeps the ordinary pointer.");
        app.Window.MouseUp(before, MouseButton.Left); Settle();
        Require(app.Tabs[0].Id == terminal, "A terminal still reorders within its own workspace.");
        Console.WriteLine("PASS a terminal tab cannot be dropped into another workspace and still moves within its own");
    }
}