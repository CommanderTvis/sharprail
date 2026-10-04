using Avalonia.Controls;
using Avalonia.LogicalTree;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ProjectsE2E
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "projects-rail-persistence");
        using (var app = new E2eWorkspace(directory, openFiles: false))
            ExpectExpansion(app, true);
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            ExpectExpansion(app, true);
            app.Click(app.Find<Button>("ProjectExpand"));
            ExpectExpansion(app, false);
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
            ExpectExpansion(app, false);
        Console.WriteLine("PASS upstream projects.spec.ts: rail expansion is per-browser view state that survives a reload");
    }

    private static void ExpectExpansion(E2eWorkspace app, bool expanded)
    {
        var toggle = app.Find<Button>("ProjectExpand");
        Require(Equals(ToolTip.GetTip(toggle), expanded ? "Collapse project" : "Expand project"),
            "Project expansion must survive window recreation.");
        Require(app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.ContextMenu is not null &&
            Equals(ToolTip.GetTip(button), app.Root) && button.IsEffectivelyVisible) == expanded,
            "Only expanded projects may expose workspace rows.");
    }
}