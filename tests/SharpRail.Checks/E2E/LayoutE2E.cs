using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LayoutE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-preview-groups"));
        app.Open("notes.txt", true); app.Open("README.md");
        var readmeGroup = app.Center;
        app.ContextAction(app.Tab("notes.txt"), "Split right");
        Until(() => app.Window.Layout.State.Center.Leaves().Count() == 2);
        var notesGroup = app.Center;
        Require(notesGroup != readmeGroup && app.Window.Layout.Tabs(readmeGroup).Single().Preview,
            "Splitting a kept tab must leave the other group's preview intact.");
        app.Open("LINKS.md");
        Require(app.Window.Layout.Tabs(notesGroup).Any(tab => tab.Path == "LINKS.md" && tab.Preview) &&
            app.Window.Layout.Tabs(readmeGroup).Single().Path == "README.md" &&
            app.Window.Layout.Tabs(readmeGroup).Single().Preview &&
            app.Window.Layout.State.Center.Leaves().SelectMany(app.Window.Layout.Tabs).Count(tab => tab.Preview) == 2,
            "Each center group must own an independent preview slot.");
        Console.WriteLine("PASS upstream layout.spec.ts: each center group owns an independent preview slot");
    }
}
