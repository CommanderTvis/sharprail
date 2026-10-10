using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

namespace SharpRail.Checks;

/// <summary>The phone-sized workbench: side panels and Settings sections as drawers over the content.</summary>
internal static class DrawerChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> done, string message)
    {
        var deadline = Awake.Now.AddSeconds(10);
        while (!done() && Awake.Now < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Require(done(), message);
    }

    private static T Named<T>(ILogical scope, string name) where T : Control =>
        scope.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "drawers");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "notes");
        var window = new WorkbenchWindow(new ProjectServices(directory), directory, new ProfileStore(directory + "-profile"), E2E.E2eTerminals.Plain, compact: true, tabsInProjects: true);
        window.Width = 400; window.Height = 800;
        window.Show();
        Pump(() => window.WorkspaceMounted, "The compact workspace did not mount.");
        var layout = window.Layout;
        var projects = Named<Button>(window, "PageProjects");
        var current = Named<Button>(window, "PageCurrent");
        var tools = Named<Button>(window, "PageTools");
        Border Region(string region) => Named<Border>(window, "AuxiliaryRegion_" + region);
        Require(Named<Border>(window, "PageBar").IsVisible && !Named<Button>(window, "SettingsButton").IsVisible && Named<Button>(window, "PageSettings").IsVisible,
            "A compact window must move between its pages, Settings included, from the bar at the bottom.");
        Require(Grid.GetRow(Named<Border>(window, "PageBar")) > Grid.GetRow(Named<Control>(window, "WorkspaceWorkbench")), "The page bar must sit below the workbench.");
        Require(!layout.State.LeftVisible && !layout.State.RightVisible, "A compact window must start on its current page.");
        Require(!window.GetLogicalDescendants().OfType<Border>().Any(border => border.Name is "leftHiddenSideRail" or "rightHiddenSideRail"),
            "A compact window must not keep the collapsed side rails.");
        Require(!Named<Button>(window, "ConnectionButton").IsHitTestVisible, "A workbench with no host to change must keep its connection status inert.");

        Click(projects);
        Require(layout.State.LeftVisible && Region("left") is { IsVisible: true } page && Grid.GetColumnSpan(page) == 5 && double.IsNaN(page.Width),
            "Projects must open as a page over the whole workbench.");
        Require(Region("left").GetLogicalDescendants().OfType<Border>().Any(border => border.Name == "ProjectsPage") &&
            !Region("left").GetLogicalDescendants().OfType<SharpRail.UI.Docking.DockTabStrip>().Any(strip => strip.Name?.Contains("left", StringComparison.Ordinal) == true),
            "The Projects page must show Projects alone, without a tool tab strip.");
        Click(tools);
        Require(!layout.State.LeftVisible && layout.State.RightVisible, "Opening Tools must leave Projects.");
        var sections = Region("right").GetLogicalDescendants().OfType<Button>().Where(button => button.Name?.StartsWith("DrawerSection_", StringComparison.Ordinal) == true).ToArray();
        Require(sections.Length == 4 && sections.All(section => section.Name != "DrawerSection_projects") &&
            Region("right").GetLogicalDescendants().OfType<Border>().Count(border => border.Name == "DrawerSectionBody") == 1,
            "Tools must list every side tool but Projects as sections and show one of them.");
        Click(sections.Single(section => section.Name == "DrawerSection_changes"));
        Require(Region("right").GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "DrawerSectionBody").Child is not null &&
            layout.State.RightVisible, "Choosing a section must show that tool and stay on Tools.");
        Click(current);
        Require(!layout.State.RightVisible && !layout.State.LeftVisible, "The current page's button must leave the side pages.");

        Click(projects);
        var back = new RoutedEventArgs(TopLevel.BackRequestedEvent);
        window.RaiseEvent(back);
        Dispatcher.UIThread.RunJobs();
        Require(back.Handled && !layout.State.LeftVisible, "Back must return from a side page to the current one.");
        back = new RoutedEventArgs(TopLevel.BackRequestedEvent);
        window.RaiseEvent(back);
        Require(!back.Handled, "Back on the current page must be left to the system.");

        Click(tools);
        _ = window.OpenDocumentAsync("notes.txt");
        Pump(() => !layout.State.RightVisible, "Opening a document must return to the current page.");
        Require(!Named<Border>(window, "CenterRegion").GetLogicalDescendants().OfType<SharpRail.UI.Docking.DockTabButton>().Any(),
            "Tabs kept in Projects must not fall back to a strip in the centre.");
        Click(projects);
        Pump(() => Region("left").GetLogicalDescendants().OfType<SharpRail.UI.Docking.DockTabButton>().Any(), "The open document's tab must be listed in Projects.");
        Click(current);

        var settings = new SettingsWindow(window, () => { });
        settings.Show(window);
        Dispatcher.UIThread.RunJobs();
        Require(window.PageBarHeight > 0 && settings.MaxHeight <= window.Bounds.Height - window.PageBarHeight + .5,
            "Compact Settings must stop above the page bar.");
        var pane = Named<Border>(settings, "SettingsNavigationPane");
        var up = Named<Button>(settings, "SettingsBack");
        Require(pane.IsVisible && !up.IsVisible && Named<TextBlock>(settings, "SettingsTitle").Text == "Settings", "Compact Settings must open on its list of sections.");
        Click(Named<Button>(settings, "Settings_Terminal"));
        Require(!pane.IsVisible && up.IsVisible && Named<TextBlock>(settings, "SettingsTitle").Text == "Terminal", "Choosing a section must give it the whole page under its name.");
        var leave = new RoutedEventArgs(TopLevel.BackRequestedEvent);
        settings.RaiseEvent(leave);
        Require(leave.Handled && pane.IsVisible, "Back in a section must return to the list.");
        leave = new RoutedEventArgs(TopLevel.BackRequestedEvent);
        settings.RaiseEvent(leave);
        Require(!leave.Handled, "Back on the list must be left to close Settings.");
        Click(Named<Button>(settings, "Settings_Layout"));
        Require(!Named<CheckBox>(settings, "VerticalCenterTabs").IsVisible && !Named<CheckBox>(settings, "VerticalTabsInProjects").IsVisible &&
            !Named<TextBlock>(settings, "EditorTabsLabel").IsVisible, "A workbench that keeps its tabs in Projects must not offer another place for them.");
        Require(Named<Button>(settings, "DefaultPane_horizontal").IsEnabled, "Pane direction must stay available where tabs always live in Projects.");
        settings.Close();
        window.ShowSettings();
        Dispatcher.UIThread.RunJobs();
        var shown = window.OwnedWindows.OfType<SettingsWindow>().Single();
        Require(!window.GetLogicalDescendants().OfType<Border>().Any(border => border.Background == Ui.Overlay && border.IsVisible && border.Name is null),
            "Compact Settings must not dim the workbench, whose page bar stays in use.");
        Click(projects);
        Pump(() => !window.OwnedWindows.OfType<SettingsWindow>().Any(), "Choosing another page must close Settings.");
        Require(layout.State.LeftVisible, "Choosing Projects from Settings must open Projects.");
        _ = shown;
        window.Close();
        Console.WriteLine("PASS phone pages: the bottom bar moves between Projects, the current tab, Tools and Settings, and a choice or Back returns");
    }
}