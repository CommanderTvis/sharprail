using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.UI.Panels;

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
        // Two centre groups, so the read-only list under this workspace must keep them apart.
        app.Open("OTHER.md", true);
        var center = app.Center;
        Require(app.Window.Layout.NewGroup(center, "after"), "A second centre group is created.");
        var split = app.Window.Layout.State.Center.Leaves().Single(group => group != center);
        Require(app.Window.Layout.Move("markdown:OTHER.md", center, split, 0), "OTHER.md moves into the second group.");
        app.Click(app.Find<Button>("Tab_projects"));
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any(window => window.IsVisible));
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single(window => window.IsVisible);
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Layout"));
        settings.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "VerticalCenterTabs").IsChecked = true;
        settings.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "VerticalTabsInProjects").IsChecked = true;
        settings.Close(); Until(() => !settings.IsVisible);
        _ = app.Window.OpenProjectAsync(first);
        Until(() => app.Window.WorkspaceMounted && app.Window.ProjectRoot == first);
        Button Preview() => app.Window.GetLogicalDescendants().OfType<ContentControl>()
            .Single(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, second))
            .GetLogicalDescendants().OfType<Button>().First(button => button.Name == "WorkspaceTabPreview");
        Until(() => app.Window.GetLogicalDescendants().OfType<ContentControl>().Any(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, second) &&
            host.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "WorkspaceTabPreview")));
        var listed = app.Window.GetLogicalDescendants().OfType<ContentControl>().Single(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, second));
        var sections = listed.GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Name == "WorkspaceTabsPreviewGroup").ToArray();
        Require(sections.Length == 2 && sections.All(section => section.Children.OfType<Button>().Count(button =>
            button.Background is Avalonia.Media.ISolidColorBrush { Color.A: > 0 }) == 1),
            "Another workspace's tabs keep their groups apart, each with its selected tab marked, as the live strip draws them.");
        Console.WriteLine("PASS another workspace's tabs in Projects keep their groups and selected tabs");
        app.Click(Preview());
        Until(() => app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == second);
        Settle(500);
        Require(app.Window.ProjectRoot == second,
            $"Choosing another project's tab in Projects opens that project, not only its folder (project is {app.Window.ProjectRoot}).");
        Console.WriteLine("PASS a tab under another project's workspace opens that project");
    }
}