using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Tereditor.Host.Abstractions;
using Tereditor.UI;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks.E2E;

/// <summary>
/// The header's status area through switches between projects whose content differs (a long and a short branch, no remote, ahead and
/// behind, a folder that is no repository, Project Home and a workspace): its bounds never change at any step. The rail (unified
/// chrome) holds every bound; the top bar holds its row, its segments' full bounds (fixed widths, names truncated) and the tools.
/// </summary>
internal static class StatusProjectChecks
{
    internal static void Run(string root)
    {
        foreach (var unified in new[] { true, false })
        {
            UnifiedChrome.Override = unified;
            try { Walk(Path.Combine(root, unified ? "status-project-rail" : "status-project-bar"), unified); }
            finally { UnifiedChrome.Override = false; }
        }
        Console.WriteLine("PASS status area: the same bounds through every project, with or without a branch, remote or repository");
    }

    private static void Walk(string directory, bool unified)
    {
        using var app = OpenFixtureProject(directory);
        var first = app.Root;
        var longBranch = "feature/" + new string('x', 120);
        var long_ = IsolatedGit.Repository(Path.Combine(directory, "long-branch-project"));
        WorkspaceFixture.Git(long_, "checkout", "-b", longBranch);
        File.WriteAllText(Path.Combine(long_, "dirty.txt"), "uncommitted");
        var plain = Path.Combine(directory, "plain-folder"); Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "note.txt"), "no repository here");
        var tiny = IsolatedGit.Repository(Path.Combine(directory, "t"));
        WorkspaceFixture.Git(tiny, "checkout", "-b", "a");
        foreach (var project in new[] { long_, plain, tiny })
        {
            AddProject(app, "Open project", project);
            Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == project && app.Window.WorkspaceMounted);
        }
        GoProjectHome(app, first);
        Settle(300);
        var baseline = Signature(app, unified);
        var variants = new Func<BranchStatus?, BranchStatus?>?[]
        {
            null,
            status => status is null ? null : status with { HasOrigin = true, Published = true, Ahead = 123, Behind = 456 },
            status => status is null ? null : status with { HasOrigin = false, Published = false, Ahead = 0, Behind = 0 },
            status => status is null ? null : status with { HasOrigin = true, Published = false },
            status => status is null ? null : status with { HasOrigin = true, Published = true, Ahead = 0, Behind = 0 }
        };
        var step = 0;
        foreach (var round in new[] { 0, 1 })
            foreach (var project in new[] { long_, plain, tiny, first, plain, long_, first })
            {
                // Project Home, then the project's own workspace.
                // A folder that is no repository has no workspace of its own, only its Project Home.
                foreach (var home in project == plain ? new[] { true } : [true, false])
                {
                    app.Host.StatusMap = variants[step++ % variants.Length];
                    if (home)
                    {
                        _ = app.Window.OpenProjectHomeAsync(project);
                        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == project && app.Window.WorkspaceMounted, $"home of {Path.GetFileName(project)} (round {round}, step {step}); at {app.Window.ProjectRoot} home={app.Window.AtProjectHome} mounted={app.Window.WorkspaceMounted} error={app.Window.FindControl<TextBlock>("WorkspaceError")?.Text} ws={app.Window.WorkspaceRoot}");
                    }
                    else
                    {
                        app.Click(Select(app, project));
                        Until(() => Active(app, project) && !app.Window.StatusLookupPending);
                    }
                    app.Window.UpdateLayout(); Settle(250); app.Window.UpdateLayout();
                    var now = Signature(app, unified);
                    Require(now == baseline, $"The status area changed on {(home ? "Project Home" : "the workspace")} of {Path.GetFileName(project)} (round {round}, branch '{app.Window.FindControl<TextBlock>("BranchLabel")!.Text}', {(unified ? "rail" : "top bar")}): [{baseline}] became [{now}].");
                }
            }
        app.Host.StatusMap = null;
    }

    private static string Signature(E2eWorkspace app, bool unified)
    {
        string Box(string name, bool size = true)
        {
            var control = app.Window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
            if (control is null) return name + "=missing";
            var origin = control.TranslatePoint(default, app.Window) ?? default;
            return size ? $"{name}={origin.X:F1},{origin.Y:F1},{control.Bounds.Width:F1},{control.Bounds.Height:F1}" : $"{name}={origin.X:F1},{origin.Y:F1},,{control.Bounds.Height:F1}";
        }
        bool Visible(string name) => app.Window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)?.IsVisible == true;
        // The rail holds its header and everything below the project; the top bar holds its row, the Project segment, the Branch slot (the segment, or the reserve holding its place when hidden) and the tools.
        return unified
            ? string.Join(" ", Box("RailHeader"), Box("ProjectSegment"), Box("HostSegment"), Box("ProjectsPanel"))
            : string.Join(" ", Box("LocationBar", false), Box("ProjectSegment"), "Branch" + Box(Visible("BranchSegment") ? "BranchSegment" : "BranchReserve")[(Visible("BranchSegment") ? "BranchSegment" : "BranchReserve").Length..], Box("HeaderTools"));
    }
}