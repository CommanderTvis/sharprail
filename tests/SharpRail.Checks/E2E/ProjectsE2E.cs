using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.UI;
using SharpRail.UI.Docking;

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
        TouchHover(root);
    }

    private static void TouchHover(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "projects-touch-git"));
        using var app = new E2eWorkspace(IsolatedGit.Repository(Path.Combine(root, "projects-touch")), openFiles: false);
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        var panel = app.Find<Grid>("ProjectsPanel");
        WorkbenchWindow.ConfigureProjectsTouch(panel, true);
        Settle();
        var row = app.Window.GetLogicalDescendants().OfType<Button>().First(button => button.Name == "WorkspaceSelect" && Equals(button.Tag, app.Window.WorkspaceRoot));
        var point = row.TranslatePoint(new Point(12, row.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(point); Settle();
        var presenter = row.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter");
        Require(Equals(presenter.Background, row.Background), "Android workspace hover must preserve its normal selected color.");
        Require(!ToolTip.GetServiceEnabled(panel) && !ToolTip.GetServiceEnabled(row) && app.Window.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "WorkspaceMenu").All(button => !button.IsVisible),
            "Android Projects must expose neither hover tooltips nor workspace menu targets.");
        var workspace = app.Window.WorkspaceRoot;
        var finger = app.Window.TouchBegin(point, RawInputModifiers.None);
        app.Window.TouchMove(finger, point + new Vector(0, 30), RawInputModifiers.None);
        app.Window.TouchEnd(finger, point + new Vector(0, 30), RawInputModifiers.None); Settle();
        Require(app.Window.WorkspaceRoot == workspace && !app.Window.OwnedWindows.Any(), "Scrolling the Projects rail must not open a workspace menu or switch workspaces.");
        var surface = app.Window.GetLogicalDescendants().OfType<DockSurface>().Single();
        surface.NestedHoverEnabled = false;
        app.Window.Preferences.VerticalCenterTabs = true;
        app.Window.Preferences.VerticalTabsInProjects = true;
        app.Window.ApplyTabLayout();
        var group = app.Window.Layout.State.Center.Leaves().First();
        app.Window.Layout.NewTerminal(group);
        var first = app.Window.Layout.Tabs(group).Single(tab => tab.Kind == "terminal");
        app.Window.Layout.NewTerminal(group);
        Until(() => app.Window.Layout.Tabs(group).Count(tab => tab.Kind == "terminal") == 2);
        Settle();
        var chrome = app.Find<Grid>("DockTab_" + first.Id.Replace(':', '_'));
        app.Window.MouseMove(chrome.TranslatePoint(new Point(4, chrome.Bounds.Height / 2), app.Window)!.Value); Settle();
        var frame = chrome.Children.OfType<Border>().First(border => border.Child is Grid);
        Require(chrome.IsPointerOver && Equals(frame.Background, Avalonia.Media.Brushes.Transparent),
            "An inactive terminal row in Android Projects must not highlight on hover.");
        Console.WriteLine("PASS Android Projects: hover preserves selection, suppresses tooltips and menu targets, and a touch swipe does not activate rows");
    }

    private static void ExpectExpansion(E2eWorkspace app, bool expanded)
    {
        var toggle = app.Find<Button>("ProjectExpand");
        Require(Equals(ToolTip.GetTip(toggle), expanded ? "Collapse project" : "Expand project"),
            "Project expansion must survive window recreation.");
        var workspaces = app.Find<StackPanel>("ProjectWorkspaces");
        Require(workspaces.IsEffectivelyVisible == expanded, "Project expansion must show or hide its workspace container.");
        Require(expanded || !app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.ContextMenu is not null &&
            Equals(ToolTip.GetTip(button), app.Root) && button.IsEffectivelyVisible),
            "Only expanded projects may expose workspace rows.");
    }
}