using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.UI;
using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks;

/// <summary>The window without a top bar: the fixed Projects rail with its header, the right bar's footer and the layout migration.</summary>
internal static class UnifiedChromeChecks
{
    internal static void Run(string root)
    {
        Migration();
        Window(Path.Combine(root, "unified-chrome"));
    }

    private static void Migration()
    {
        var state = DockState.Preset("balanced");
        var left = state.Groups.Single(group => group.Region == "left");
        left.Tools.Add(DockState.Tool("files"));
        state.Groups.Add(new DockGroup { Region = "left", Tools = [DockState.Tool("lsp")] });
        state.Groups.Single(group => group.Region == "right" && group.Tools.Any(tab => tab.Id == "files")).Tools.RemoveAll(tab => tab.Id == "files");
        state.LeftVisible = false;
        var legacy = new LayoutSession(state.Copy());
        Require(legacy.State.Groups.Count(group => group.Region == "left") == 2 && !legacy.State.LeftVisible, "Without unified chrome the left column keeps its tools.");
        var session = new LayoutSession(state.Copy(), fixedLeft: true);
        var lefts = session.State.Groups.Where(group => group.Region == "left").ToArray();
        var rights = session.State.Groups.Where(group => group.Region == "right").SelectMany(group => group.Tools).Select(tab => tab.Id).ToArray();
        Require(lefts.Length == 1 && lefts[0].Tools.Select(tab => tab.Id).SequenceEqual(["projects"]) && session.State.LeftVisible,
            "A saved layout's left column becomes the Projects rail alone, shown.");
        Require(rights.Contains("files") && rights.Contains("lsp"), "The tools the left column held move to the right side.");
        session.Visible("left", false);
        Require(session.State.LeftVisible, "The fixed rail cannot be hidden.");
        foreach (var name in new[] { "focus", "review", "balanced" })
        {
            session.ApplyPreset(DockState.Preset(name));
            Require(session.State.LeftVisible && session.State.Groups.Count(group => group.Region == "left") == 1, $"The {name} preset keeps the fixed rail.");
        }
        Console.WriteLine("PASS unified chrome: old layouts migrate, presets keep the rail, the rail cannot be hidden");
    }

    private static void Window(string directory)
    {
        UnifiedChrome.Override = true;
        try
        {
            using var app = OpenFixtureProject(directory);
            LocationBarChecks.Switch(app, app.Root);
            Require(!app.Find<Border>("WindowTitleBar").IsVisible && app.Find<DockSurface>("WorkspaceWorkbench").Bounds.Top == 0,
                "There is no top bar: the workbench starts at the top of the window.");
            var header = app.Find<StackPanel>("RailHeader");
            bool InHeader(Control control) => header.IsLogicalAncestorOf(control);
            Require(InHeader(app.Find<Button>("AppMenuButton")) && InHeader(app.Find<Button>("ScopeProject")) && InHeader(app.Find<Button>("ScopeBranch")) &&
                InHeader(app.Find<StackPanel>("RemoteRow")), "The rail header holds the overflow menu, the project switcher and the branch block with its sync state.");
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "left") == 1 && app.Window.Layout.State.LeftVisible,
                "The left column is the Projects rail alone.");
            var all = app.Window.GetLogicalDescendants().OfType<Control>().ToArray();
            Require(!all.Any(control => control.Name is "Tab_projects" or "leftRail" or "PaneTitle" && control.GetLogicalAncestors().Any(a => (a as Control)?.Name == "AuxiliaryRegion_left")) &&
                !all.OfType<Button>().Any(button => button.Name == "Tab_projects"), "The rail has no title, icon or tool tabs.");
            var footer = app.Find<StackPanel>("RailFooter");
            Require(footer.IsLogicalAncestorOf(app.Find<Button>("SettingsButton")) && footer.IsLogicalAncestorOf(app.Find<Button>("AboutButton")),
                "Settings and About close the right tool bar.");
            app.Click(app.Find<Button>("SettingsButton"));
            Until(() => app.Window.OpenSettingsViews.Any());
            app.Window.OpenSettingsViews.Single().Close();
            Console.WriteLine("PASS unified chrome: no top bar, the rail header, the right bar's Settings and About");
        }
        finally { UnifiedChrome.Override = false; }
    }
}
