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
        var window = new WorkbenchWindow(new ProjectServices(directory), directory, new ProfileStore(directory + "-profile"), E2E.E2eTerminals.Plain, compact: true);
        window.Width = 400; window.Height = 800;
        window.Show();
        Pump(() => window.WorkspaceMounted, "The compact workspace did not mount.");
        var layout = window.Layout;
        var left = Named<Button>(window, "LeftDrawerButton");
        var right = Named<Button>(window, "RightDrawerButton");
        var scrim = Named<Border>(window, "DrawerScrim");
        Border Region(string region) => Named<Border>(window, "AuxiliaryRegion_" + region);
        Require(left.IsVisible && right.IsVisible && !Named<ContentControl>(window, "BrandIcon").IsVisible, "A compact window must offer both drawers in its header.");
        Require(!layout.State.LeftVisible && !layout.State.RightVisible && !scrim.IsVisible, "Both drawers must start closed.");
        Require(!window.GetLogicalDescendants().OfType<Border>().Any(border => border.Name is "leftHiddenSideRail" or "rightHiddenSideRail"),
            "A compact window must not keep the collapsed side rails.");

        Click(left);
        Require(layout.State.LeftVisible && scrim.IsVisible && Region("left") is { IsVisible: true, HorizontalAlignment: HorizontalAlignment.Left } drawer &&
            drawer.Width <= window.Bounds.Width - 56 && Grid.GetColumnSpan(drawer) == 5, "The left drawer must open over the centre at the left edge.");
        Click(right);
        Require(!layout.State.LeftVisible && layout.State.RightVisible && Region("right").HorizontalAlignment == HorizontalAlignment.Right,
            "Opening the right drawer must close the left one.");
        Click(right);
        Require(!layout.State.RightVisible && !scrim.IsVisible, "A drawer's button must close it again.");

        Click(left);
        var back = new RoutedEventArgs(TopLevel.BackRequestedEvent);
        window.RaiseEvent(back);
        Dispatcher.UIThread.RunJobs();
        Require(back.Handled && !layout.State.LeftVisible, "Back must close an open drawer.");
        back = new RoutedEventArgs(TopLevel.BackRequestedEvent);
        window.RaiseEvent(back);
        Require(!back.Handled, "Back with no drawer open must be left to the system.");

        Click(right);
        _ = window.OpenDocumentAsync("notes.txt");
        Pump(() => !layout.State.RightVisible, "Opening a document must close the drawer it was chosen from.");

        var settings = new SettingsWindow(window, () => { });
        settings.Show(window);
        Dispatcher.UIThread.RunJobs();
        var pane = Named<Border>(settings, "SettingsNavigationPane");
        var menu = Named<Button>(settings, "SettingsMenu");
        Require(menu.IsVisible && !pane.IsVisible, "Compact Settings must show one section with its list behind a button.");
        Click(menu);
        Require(pane.IsVisible && Named<Border>(settings, "SettingsScrim").IsVisible, "The section list must open as a drawer.");
        Click(Named<Button>(settings, "Settings_Terminal"));
        Require(!pane.IsVisible, "Choosing a section must close the list.");
        settings.Close();
        window.Close();
        Console.WriteLine("PASS phone drawers: side panels and Settings sections open over the content, one at a time, and close on choice or Back");
    }
}