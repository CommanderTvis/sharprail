using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Plugins.UI.Kit;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>Pinning a project from its right-click menu moves it into the rail's top group, marks it, and survives a restart.</summary>
internal static class ProjectPinE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "project-pin-git"));
        var directory = Path.Combine(root, "project-pin");
        using var app = OpenFixtureProject(directory);
        var first = app.Root;
        var second = IsolatedGit.Repository(Path.Combine(directory, "second-project"));
        AddProject(app, "Open project", second);
        Until(() => Rows(app).Contains(second));
        var third = IsolatedGit.Repository(Path.Combine(directory, "third-project"));
        AddProject(app, "Open project", third);
        Until(() => Rows(app).Contains(third));
        // The host lists the newest project first; the rail must keep whatever order it has below the pins.
        var host = Rows(app);
        Require(host.Length == 3 && host.Contains(first) && host.Contains(second) && host.Contains(third), "The rail lists the three projects: " + string.Join(", ", host));
        string[] Expected(params string[] pinned) => [.. pinned, .. host.Where(project => !pinned.Contains(project))];
        Require(Marked(app).Length == 0, "No project is marked before anything is pinned.");

        ContextMenu Open(string project)
        {
            app.Click(ProjectName(app, project), mouseButton: Avalonia.Input.MouseButton.Right);
            var menu = Controls(app).OfType<Grid>().Single(row => row.Name == "ProjectRow" && Equals(row.Tag, project)).ContextMenu!;
            Until(() => menu.IsOpen);
            return menu;
        }
        string Header(string project)
        {
            var menu = Open(project);
            var header = (string)MenuEntry(menu, "ProjectMenuPin")!.Header!;
            CloseMenu(menu);
            return header;
        }
        void Choose(string project)
        {
            var menu = Open(project);
            app.Click(MenuEntry(menu, "ProjectMenuPin")!, freshGesture: false);
            Until(() => !menu.IsOpen);
        }

        Require(Header(third) == "Pin", "An unpinned project offers Pin.");
        Choose(third);
        Until(() => Rows(app).SequenceEqual(Expected(third)));
        Require(Header(third) == "Unpin" && Header(first) == "Pin", "A pinned project offers Unpin, the others Pin.");
        Choose(second);
        Until(() => Rows(app).SequenceEqual(Expected(third, second)));
        Require(Marked(app).SequenceEqual([third, second]), "Exactly the pinned projects carry the pin marker, in pin order.");
        foreach (var project in new[] { third, second })
        {
            var row = Controls(app).OfType<Grid>().Single(item => item.Name == "ProjectRow" && Equals(item.Tag, project));
            var mark = row.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == "ProjectPinMark");
            var label = row.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == "ProjectName");
            var projectName = label.GetLogicalDescendants().OfType<TextBlock>().Last();
            var markBox = mark.TranslatePoint(default, row)!.Value;
            var nameBox = projectName.TranslatePoint(default, row)!.Value;
            var trailingCluster = row.Children.OfType<Control>().Where(item => Grid.GetColumn(item) >= 3).ToArray();
            Require(!trailingCluster.Any(item => item.GetLogicalDescendants().Contains(mark)) && markBox.X + mark.Bounds.Width <= nameBox.X,
                "The pin mark sits before the project name, outside the trailing count and add cluster.");
            Require(ToolTip.GetTip(mark) as string == "Pinned" && AutomationProperties.GetName(mark) == "Pinned", "The pin mark is tooltipped and named Pinned.");
            Require(label.Bounds.Right <= row.Bounds.Width && trailingCluster.All(item => !item.IsEffectivelyVisible || item.Bounds.Width == 0 || item.Bounds.X >= label.Bounds.Right - 0.5),
                "The name area never overlaps the trailing cluster.");
        }
        Require(ActionIcons.Asset(ActionIcon.Pin) != ActionIcons.Asset(ActionIcon.Unpin), "Pin and Unpin are distinct registry icons.");
        var reread = new SharpRail.UI.State.ProfileStore(app.Workbench.Profile.DirectoryPath);
        Require(reread.Data.PinnedProjects.SequenceEqual([third, second]), "The pins are saved to the user profile in pin order.");

        Choose(third);
        Until(() => Rows(app).SequenceEqual(Expected(second)));
        Require(Marked(app).SequenceEqual([second]), "Unpinning returns the project to its place among the rest and removes its marker.");
        Require(new SharpRail.UI.State.ProfileStore(app.Workbench.Profile.DirectoryPath).Data.PinnedProjects.SequenceEqual([second]), "Unpinning is saved.");
        Console.WriteLine("PASS project pins: Pin and Unpin from the project menu order the rail, mark pinned projects and persist");
    }

    private static string[] Rows(E2eWorkspace app) => Controls(app).OfType<Grid>().Where(row => row.Name == "ProjectRow").Select(row => (string)row.Tag!).ToArray();

    private static string[] Marked(E2eWorkspace app) => Controls(app).OfType<Grid>().Where(row => row.Name == "ProjectRow" &&
        row.GetLogicalDescendants().OfType<Control>().Any(mark => mark.Name == "ProjectPinMark" && mark.IsEffectivelyVisible)).Select(row => (string)row.Tag!).ToArray();
}