using Avalonia;
using Avalonia.Controls;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>Switching between one project's workspaces restyles the rail in place instead of rebuilding its rows.</summary>
internal static class RailRetentionChecks
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "rail-retention-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "rail-retention"));
        var first = CreateWorkspaceViaDialog(app);
        var second = CreateWorkspaceViaDialog(app);
        Settle(500);
        var rows = Rows(app);
        var scroller = Controls(app).OfType<ScrollViewer>().First(item => item.Content is StackPanel && item.Parent is Grid { Name: "ProjectsPanel" });
        var add = Controls(app).OfType<Button>().Single(button => button.Name == "AddWorkspace");
        var addRight = add.TranslatePoint(new Point(add.Bounds.Width, 0), scroller)!.Value.X;
        Require(addRight <= scroller.Bounds.Width - 12,
            $"Projects keeps a gutter for its scrollbar beside the rows' buttons ({addRight} of {scroller.Bounds.Width}).");
        var panel = Controls(app).OfType<Grid>().Single(control => control.Name == "ProjectsPanel");
        var detached = 0;
        panel.DetachedFromVisualTree += (_, _) => detached++;
        foreach (var target in new[] { first, app.Root, second })
        {
            app.Click(Select(app, target));
            Until(() => Active(app, target) && app.Find<TextBlock>("BranchLabel").Text is { Length: > 0 });
            Settle(500);
            var now = Rows(app);
            Require(now.Count == rows.Count && now.Zip(rows).All(pair => ReferenceEquals(pair.First, pair.Second)),
                $"Switching to {Path.GetFileName(target)} keeps the workspace rows instead of rebuilding the list.");
            Require(detached == 0, $"Switching to {Path.GetFileName(target)} keeps the rail mounted instead of re-attaching it.");
        }
        Console.WriteLine("PASS switching workspaces keeps the rail's rows");
    }

    private static List<Grid> Rows(E2eWorkspace app) => Controls(app).OfType<Grid>().Where(item => item.Name == "WorkspaceItem").ToList();
}