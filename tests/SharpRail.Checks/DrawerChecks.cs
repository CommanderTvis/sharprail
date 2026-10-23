using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
        // The headless render timer never ticks by itself, and the pages' slide waits for its frames, which ticks
        // forced faster than a display's pace can hold back while composition catches up.
        while (!done() && Awake.Now < deadline) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); Thread.Sleep(16); }
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
        E2E.IsolatedGit.Run(directory, "init", "-q", "-b", "main");
        var window = new WorkbenchWindow(new ProjectServices(directory), directory, new ProfileStore(directory + "-profile"), E2E.E2eTerminals.Plain, compact: true);
        window.Width = 400; window.Height = 800;
        window.Show();
        Pump(() => window.WorkspaceMounted && window.Layout.Tools.Any(tool => tool.Id == "review"), "The compact Git workspace did not mount.");
        var layout = window.Layout;
        var projects = Named<Button>(window, "PageProjects");
        var current = Named<Button>(window, "PageCurrent");
        var tools = Named<Button>(window, "PageTools");
        Border Region(string region) => Named<Border>(window, "AuxiliaryRegion_" + region);
        Require(Named<Border>(window, "PageBar").IsVisible && !Named<Button>(window, "SettingsButton").IsVisible && Named<Button>(window, "PageSettings").IsVisible,
            "A compact window must move between its pages, Settings included, from the bar at the bottom.");
        Require(Grid.GetRow(Named<Border>(window, "PageBar")) > Grid.GetRow(Named<Control>(window, "WorkspaceWorkbench")), "The page bar must sit below the workbench.");
        Require(!layout.State.LeftVisible && !layout.State.RightVisible, "A compact window must start on its current page.");
        Require(!window.GetLogicalDescendants().OfType<Border>().Any(border => border.Name is "leftRail" or "rightRail"),
            "A compact window must not keep the side edge rails.");
        Require(!Named<ContentControl>(window, "BrandIcon").IsVisible, "The phone header must not show the app logo.");
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
        Require(sections.Length == window.Layout.Tools.Count - 1 && sections.Length >= 4 && sections.All(section => section.Name != "DrawerSection_projects") &&
            Region("right").GetLogicalDescendants().OfType<Border>().Count(border => border.Name == "DrawerSectionBody") == 1,
            "Tools must list every tool but Projects as sections, seated in a group or not, and show one of them.");
        Click(sections.Single(section => section.Name == "DrawerSection_review"));
        Require(Region("right").GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "DrawerSectionBody").Child is not null &&
            layout.State.RightVisible, "Choosing a section must show that tool and stay on Tools.");
        Click(current);
        Require(!layout.State.RightVisible && !layout.State.LeftVisible, "The current page's button must leave the side pages.");

        window.SwipePage(towardsLeft: true);
        Require(layout.State.LeftVisible, "A flick to the right from the current page must uncover Projects.");
        window.SwipePage(towardsLeft: false);
        window.SwipePage(towardsLeft: false);
        Require(layout.State.RightVisible && !layout.State.LeftVisible, "Flicks to the left must pass the current page and reach Tools.");
        window.SwipePage(towardsLeft: false);
        Require(layout.State.RightVisible, "A flick past the last page must change nothing.");
        Click(current);

        // A finger drags the neighbouring page in with it: it stays when carried far enough, and goes back otherwise.
        void Drag(double from, double to)
        {
            var y = window.Bounds.Height / 2;
            var touch = window.TouchBegin(new(from, y), Avalonia.Input.RawInputModifiers.None);
            for (var step = 1; step <= 5; step++) window.TouchMove(touch, new(from + (to - from) * step / 5, y), Avalonia.Input.RawInputModifiers.None);
            // Held long enough that the drag is no flick.
            using (var wait = new CancellationTokenSource(400)) Dispatcher.UIThread.MainLoop(wait.Token);
            window.TouchEnd(touch, new(to, y), Avalonia.Input.RawInputModifiers.None);
        }
        var width = window.Bounds.Width;
        Drag(width * 0.2, width * 0.4);
        Pump(() => !layout.State.LeftVisible && !layout.State.RightVisible, "A page dragged a short way must go back to the one it left.");
        Drag(width * 0.2, width * 0.7);
        Pump(() => layout.State.LeftVisible, "A page dragged most of the way in must stay.");
        Drag(width * 0.8, width * 0.3);
        Pump(() => !layout.State.LeftVisible && !layout.State.RightVisible, "Dragging Projects away must return to the current page.");

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
        Require(Region("left").GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "CloseTab").All(close => close.Opacity == 1) &&
            Region("left").GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "CloseTab"), "A touch workbench must always draw a tab's close button.");
        var group = layout.State.Center.Leaves().First();
        var selected = layout.Selected(group)!.Id;
        for (var index = 0; index < 24; index++)
            layout.Open(new SharpRail.UI.Docking.DockTab("scroll-" + index, "Scrolling tab " + index, "file", "notes.txt"), keep: true, activate: false);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var labels = Region("left").GetLogicalDescendants().OfType<SharpRail.UI.Docking.DockTabButton>().ToArray();
        Require(labels.All(label => !label.IsHitTestVisible), "Compact Projects tab labels must leave touch input to the scroller.");
        var openTab = Region("left").GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "OpenProjectTab" && Equals(button.Tag, selected));
        Require(openTab.Bounds.Width >= 44 && openTab.Bounds.Height >= 44, "Each compact tab needs a readable, reachable open action.");
        var scroller = Region("left").GetLogicalDescendants().OfType<ScrollViewer>().Single(view => view.Content is StackPanel panel && panel.Name == "ProjectsTree");
        var point = labels[1].TranslatePoint(new Point(70, 22), window)!.Value;
        var projectsFinger = window.TouchBegin(point, Avalonia.Input.RawInputModifiers.None);
        for (var step = 1; step <= 6; step++) window.TouchMove(projectsFinger, point - new Vector(0, step * 16), Avalonia.Input.RawInputModifiers.None);
        window.TouchEnd(projectsFinger, point - new Vector(0, 96), Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Require(scroller.Offset.Y > 0 && layout.Selected(group)?.Id == selected && layout.State.LeftVisible,
            "Dragging over tab labels must scroll Projects without selecting a tab or leaving the page.");
        Click(openTab);
        Require(!layout.State.LeftVisible && layout.Selected(group)?.Id == selected,
            "The explicit open action must reveal even an already selected tab.");
        Click(projects);
        var other = Region("left").GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "OpenProjectTab" && Equals(button.Tag, "scroll-0"));
        Click(other);
        Require(!layout.State.LeftVisible && layout.Selected(group)?.Id == "scroll-0",
            "Opening another tab must select it and leave Projects.");
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
        Require(!settings.GetLogicalDescendants().OfType<Control>().Any(control => control.Name is "VerticalCenterTabs" or "VerticalTabsInProjects"),
            "Layout must not offer retired tab modes.");
        Require(Named<Button>(settings, "DefaultPane_horizontal").IsEnabled, "Pane direction must stay available where tabs always live in Projects.");
        settings.Close();
        Click(current);
        var menus = window.GetLogicalDescendants().OfType<Control>().Select(control => control.ContextMenu).OfType<ContextMenu>().Distinct().ToArray();
        Require(menus.Length > 0 && !menus.SelectMany(menu => menu.Items.OfType<MenuItem>()).Any(item => item.Header is string header && header.StartsWith("New split", StringComparison.Ordinal)),
            "A workbench that keeps its tabs in Projects must not offer to split the centre.");

        Click(Named<Button>(window, "PageTools"));
        Drag(width * 0.8, width * 0.3);
        Pump(() => window.OwnedWindows.OfType<SettingsWindow>().Any(), "A sweep past Tools must open Settings, the page after it.");
        var swept = window.OwnedWindows.OfType<SettingsWindow>().Single();
        var finger = swept.TouchBegin(new(40, 200), Avalonia.Input.RawInputModifiers.None);
        swept.TouchMove(finger, new(160, 200), Avalonia.Input.RawInputModifiers.None);
        swept.TouchEnd(finger, new(240, 200), Avalonia.Input.RawInputModifiers.None);
        Pump(() => !window.OwnedWindows.OfType<SettingsWindow>().Any() && layout.State.RightVisible, "A sweep back over Settings must return to Tools.");
        Click(current);
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

    /// <summary>
    /// The three pages are hosts that outlive a switch: the centre keeps its controls and focus while Projects shows,
    /// a held page follows the finger, an interrupted slide ends on the last page asked for, and a page off screen
    /// takes no touch.
    /// </summary>
    internal static void Pages(string root)
    {
        var directory = Path.Combine(root, "drawer-pages");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "notes");
        E2E.IsolatedGit.Run(directory, "init", "-q", "-b", "main");
        var window = new WorkbenchWindow(new ProjectServices(directory), directory, new ProfileStore(directory + "-profile"), E2E.E2eTerminals.Plain, compact: true);
        window.Width = 400; window.Height = 800;
        window.Show();
        Pump(() => window.WorkspaceMounted && window.Layout.Tools.Any(tool => tool.Id == "review"), "The compact Git workspace did not mount.");
        var layout = window.Layout;
        var surface = Named<SharpRail.UI.Docking.DockSurface>(window, "WorkspaceWorkbench");
        var center = Named<Border>(window, "CenterRegion");
        Border Region(string region) => Named<Border>(window, "AuxiliaryRegion_" + region);
        Border Host(int page) => page < 0 ? Region("left") : page > 0 ? Region("right") : center;
        void Frames(int count) { for (var frame = 0; frame < count; frame++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); Thread.Sleep(16); } }
        void Settle(string message)
        {
            for (var frame = 0; frame < 600 && surface.PageSliding; frame++) Frames(1);
            Require(!surface.PageSliding, message);
        }
        double Offset(Border host) => host.RenderTransform is TranslateTransform move ? move.X : 0;
        // At rest exactly one page is visible and nothing is left moved aside.
        void AtRest(int page, string message)
        {
            Settle(message + " (the slide never settled)");
            for (var other = -1; other <= 1; other++)
                Require(Host(other).IsVisible == (other == page) && Host(other).RenderTransform is null, message);
            Require(surface.ShownPage == page, message);
        }

        AtRest(0, "A compact window must start on the centre alone.");
        Require(Region("left").Child is null && Region("right").Child is null, "Side pages must not be built before they are first shown.");

        _ = window.OpenDocumentAsync("notes.txt");
        Pump(() => center.GetVisualDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Any(editor => editor.IsEffectivelyVisible), "The document did not open in the centre.");
        var focusable = center.GetVisualDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().First();
        Require(focusable.Focus(), "The centre's editor must take focus.");
        var group = center.Child;
        Require(Region("left").Child is null && Region("right").Child is null, "Opening a document must not build the hidden side pages.");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var startMemory = GC.GetTotalMemory(true);
        var startVisuals = window.GetVisualDescendants().Count();
        Click(Named<Button>(window, "PageProjects"));
        AtRest(-1, "Projects must slide in and leave the centre hidden.");
        var middle = window.Bounds.Center;
        Require(window.InputHitTest(middle) is Visual hit && Region("left").IsVisualAncestorOf(hit) && !center.IsHitTestVisibleAt(window, middle),
            "Touch on Projects must reach Projects, never the hidden centre under it.");
        Click(Named<Button>(window, "PageCurrent"));
        AtRest(0, "The current page's button must slide the centre back.");
        Require(ReferenceEquals(center.Child, group), "Returning to the centre must keep the same controls, not rebuild them.");
        Require(TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() == focusable, "Returning to the centre must give focus back where it was.");

        // A finger drags Projects in: the pages follow it, and stay apart by exactly one page.
        var width = window.Bounds.Width;
        var y = window.Bounds.Height / 2;
        // Sampled frame by frame, under the finger and after it lets go: every frame must move the pages, and with a
        // transform of its own, since a transform changed in place does not move a page on Android.
        var samples = new List<(ITransform? Transform, double Offset)>();
        void Sample() => samples.Add((Region("left").RenderTransform, Offset(Region("left"))));
        var touch = window.TouchBegin(new(width * 0.2, y), RawInputModifiers.None);
        for (var step = 1; step <= 5; step++) { window.TouchMove(touch, new(width * (0.2 + 0.4 * step / 5), y), RawInputModifiers.None); Frames(1); Sample(); }
        Require(surface.PageSliding && Region("left").IsVisible && center.IsVisible &&
            Math.Abs(Offset(center) - width * 0.4) < 2 && Math.Abs(Offset(Region("left")) - (width * 0.4 - width)) < 2,
            "A held page must follow the finger, the page it leaves beside it.");
        var held = samples.Count;
        using (var wait = new CancellationTokenSource(400)) Dispatcher.UIThread.MainLoop(wait.Token);
        window.TouchEnd(touch, new(width * 0.6, y), RawInputModifiers.None);
        for (var frame = 0; frame < 120 && surface.PageSliding; frame++) { Sample(); Frames(1); }
        var settling = samples.Skip(held).Where(sample => sample.Offset < -1).ToArray();
        Require(samples.Take(held).Select(sample => sample.Offset).Zip(samples.Skip(1).Take(held - 1).Select(sample => sample.Offset)).All(pair => pair.Second > pair.First),
            "Each move of a held finger must carry the page further in.");
        Require(settling.Length >= 3 && settling.Zip(settling.Skip(1)).All(pair => pair.Second.Offset >= pair.First.Offset) &&
            settling.Any(sample => sample.Offset > width * 0.4 - width + 2), "A page let go must slide the rest of the way over several frames.");
        Require(samples.Zip(samples.Skip(1)).All(pair => pair.Second.Offset == pair.First.Offset || !ReferenceEquals(pair.First.Transform, pair.Second.Transform)),
            "Every slide frame must give the page a new transform, or Android leaves it where it was.");
        AtRest(-1, "A page carried past the third must complete.");
        Require(layout.State.LeftVisible, "A completed drag must leave Projects shown.");
        touch = window.TouchBegin(new(width * 0.6, y), RawInputModifiers.None);
        for (var step = 1; step <= 5; step++) window.TouchMove(touch, new(width * 0.6 - width * 0.25 * step / 5, y), RawInputModifiers.None);
        using (var wait = new CancellationTokenSource(400)) Dispatcher.UIThread.MainLoop(wait.Token);
        window.TouchEnd(touch, new(width * 0.35, y), RawInputModifiers.None);
        AtRest(-1, "A page let go short of a third must go back.");
        Require(layout.State.LeftVisible, "A reverted drag must leave Projects shown.");

        // Projects to Tools directly: the centre is not visited on the way.
        Click(Named<Button>(window, "PageTools"));
        Require(surface.PageSliding && Region("left").IsVisible && Region("right").IsVisible && !center.IsVisible, "Projects must slide straight to Tools.");
        AtRest(1, "Tools must end alone on screen.");
        Require(Region("right").GetLogicalDescendants().OfType<Border>().Count(border => border.Name == "DrawerSectionBody") == 1, "Tools must be built when first shown.");
        var sections = Region("right").Child;
        var projectsPage = Region("left").Child;

        // A switch that interrupts a running slide ends on the page asked for last, with nothing stranded aside.
        Click(Named<Button>(window, "PageCurrent"));
        Frames(3);
        Click(Named<Button>(window, "PageProjects"));
        Frames(2);
        Click(Named<Button>(window, "PageTools"));
        AtRest(1, "An interrupted slide must end on the last page asked for.");
        Click(Named<Button>(window, "PageCurrent"));
        Frames(3);
        Click(Named<Button>(window, "PageProjects"));
        AtRest(-1, "A slide interrupted for another page must end there.");
        Require(ReferenceEquals(Region("right").Child, sections) && ReferenceEquals(Region("left").Child, projectsPage) && ReferenceEquals(center.Child, group),
            "Pages must keep their controls across switches.");
        Click(Named<Button>(window, "PageCurrent"));
        AtRest(0, "The centre must come back last.");

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var keptMemory = GC.GetTotalMemory(true);
        var keptVisuals = window.GetVisualDescendants().Count();
        Console.WriteLine($"INFO phone pages kept alive: managed {(keptMemory - startMemory) / 1024.0:+0;-0} KiB, visuals {startVisuals} -> {keptVisuals}");
        window.Close();
        Console.WriteLine("PASS phone page hosts: pages keep their controls and focus, follow the finger, survive interruptions and hide off screen");
    }

    // A tab group on a phone: one member over the centre, chips to choose it, a sideways drag between members. The
    // choice is the phone's own: the shared layout and selection stay as they were, and hidden terminals stay put.
    internal static void TabGroups(string root)
    {
        var directory = Path.Combine(root, "drawer-tab-groups");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "notes");
        E2E.IsolatedGit.Run(directory, "init", "-q", "-b", "main");
        var terminals = new E2E.E2eTerminals();
        var window = new WorkbenchWindow(new ProjectServices(directory), directory, new ProfileStore(directory + "-profile"), terminals.Factory, compact: true);
        window.Width = 400; window.Height = 800;
        window.Show();
        Pump(() => window.WorkspaceMounted && window.Layout.Tools.Any(tool => tool.Id == "review"), "The compact Git workspace did not mount.");
        var layout = window.Layout;
        var surface = Named<SharpRail.UI.Docking.DockSurface>(window, "WorkspaceWorkbench");
        var group = layout.State.Center.Leaves().First();
        layout.NewTerminal(group);
        var first = layout.Selected(group)!.Id;
        _ = window.OpenDocumentAsync("notes.txt", keep: true);
        Pump(() => layout.Tabs(group).Any(tab => tab.Kind != "terminal"), "The document did not open.");
        var notes = layout.Tabs(group).First(tab => tab.Kind != "terminal").Id;
        layout.NewTerminal(group);
        var second = layout.Selected(group)!.Id;
        Require(layout.GroupTabs(group, notes, first, "horizontal") && layout.GroupTabs(group, second, notes, "horizontal") &&
            layout.PaneFor(group, first) is { TabIds.Count: 3 }, "The three tabs must make one group.");
        layout.Select(group, first);
        Pump(() => surface.PhonePanes.ContainsKey(group) && surface.PhonePanes[group].Shown == first, "A phone must show the group as one member under chips.");
        var pane = surface.PhonePanes[group];
        var weights = layout.PaneFor(group, first)!.Weights.ToArray();
        Button[] Chips() => [.. pane.GetLogicalDescendants().OfType<Button>().Where(chip => chip.Name == "PaneChip")];
        Border[] Hosts() => [.. pane.GetLogicalDescendants().OfType<Border>().Where(host => host.Name == "PaneMemberHost")];
        Control? Terminal(string id) => pane.GetLogicalDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().FirstOrDefault(view => view.Name == "TerminalSurface_" + id.Replace(':', '_'));
        bool Dot(string id) => Chips().Single(chip => Equals(chip.Tag, id)).Content is Panel { Children: [StackPanel { Children: [_, Avalonia.Controls.Shapes.Ellipse dot] }, ..] } && dot.IsVisible;
        void Frames(int count) { for (var frame = 0; frame < count; frame++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); Thread.Sleep(16); } }
        void Shows(string id, string message) => Pump(() => pane.Shown == id && Hosts().Length == 1 && Equals(Hosts()[0].Tag, id), message);
        Require(Chips().Length == 3 && Chips().Select(chip => chip.Tag).SequenceEqual([first, notes, second]) && Hosts().Length == 1 &&
            Chips().Single(chip => chip.Classes.Contains("selected")).Tag as string == first && Terminal(first) is not null,
            "A group of three must show one member and three chips, the shown one lit.");
        Pump(() => terminals.Views.Count == 1 && terminals.Views[0].Text.Length > 0, "The shown terminal did not start.");
        var firstView = Terminal(first)!;
        // A member's terminal progress runs along its chip, shown or not, coloured by its state.
        var progressing = SharpRail.UI.Terminal.TerminalLaunch.SessionFor(window.WorkspaceRoot, second);
        SharpRail.UI.Terminal.TerminalProgressStrip? ChipProgress(string id) => Chips().Single(chip => Equals(chip.Tag, id))
            .GetLogicalDescendants().OfType<SharpRail.UI.Terminal.TerminalProgressStrip>().SingleOrDefault();
        window.Workbench.Signals.SetProgress(progressing, SharpRail.Host.Abstractions.TerminalProgressState.Error, 30);
        Pump(() => ChipProgress(second) is { IsVisible: true, State: SharpRail.Host.Abstractions.TerminalProgressState.Error, Percent: 30 } &&
            ChipProgress(first) is { IsVisible: false } && ChipProgress(notes) is null, "A terminal member's chip must show its progress.");
        window.Workbench.Signals.SetProgress(progressing, SharpRail.Host.Abstractions.TerminalProgressState.None, -1);
        Pump(() => ChipProgress(second) is { IsVisible: false }, "Removing the progress must clear the chip's bar.");

        // What the host keeps for the group: its tabs, its selection and the group's members, direction and weights.
        string Shared() => System.Text.Json.JsonSerializer.Serialize(new
        {
            Tabs = layout.Tabs(group).Select(tab => tab.Id),
            Selected = layout.Selected(group)?.Id,
            Panes = layout.View.Panes.GetValueOrDefault(group)
        });
        var shared = Shared();
        Click(Chips().Single(chip => Equals(chip.Tag, second)));
        Shows(second, "Tapping a chip must show that member alone.");
        Require(Terminal(first) is null && Terminal(second) is not null, "Only the shown member's content may stay in the tree.");
        Pump(() => terminals.Views.Count == 2, "The second terminal did not start once shown.");
        var attaches = terminals.Attaches.Count;
        var secondView = Terminal(second);

        // A finger dragging sideways over the member moves to its neighbour, and a short drag goes back.
        var width = window.Bounds.Width;
        var y = window.Bounds.Height / 2;
        void Drag(double from, double to)
        {
            var touch = window.TouchBegin(new(from, y), RawInputModifiers.None);
            for (var step = 1; step <= 5; step++) { window.TouchMove(touch, new(from + (to - from) * step / 5, y), RawInputModifiers.None); Frames(1); }
            using (var wait = new CancellationTokenSource(400)) Dispatcher.UIThread.MainLoop(wait.Token);
            window.TouchEnd(touch, new(to, y), RawInputModifiers.None);
        }
        Drag(width * 0.4, width * 0.6);
        Shows(second, "A member dragged a short way must go back.");
        Drag(width * 0.2, width * 0.8);
        Shows(notes, "A drag to the right must bring the previous member in.");
        Require(!layout.State.LeftVisible && !layout.State.RightVisible, "A drag over the group must stay on the current page while a member lies that way.");
        Drag(width * 0.8, width * 0.2);
        Shows(second, "A drag to the left must bring the next member in.");
        Require(Shared() == shared, "Chips and member drags must leave the shared layout alone.");
        // Past the last member the drag is the page's again.
        Drag(width * 0.8, width * 0.2);
        Pump(() => layout.State.RightVisible, "A drag past the last member must go on to Tools.");
        Click(Named<Button>(window, "PageCurrent"));
        Pump(() => !layout.State.RightVisible && !surface.PageSliding, "The current page must come back.");
        Require(Shared() == shared && layout.Selected(group)!.Id == first && layout.PaneFor(group, first) is { } kept &&
            kept.TabIds.SequenceEqual([first, notes, second]) && kept.Weights.SequenceEqual(weights),
            "Switching members on a phone must leave the shared layout and selection alone.");
        Require(terminals.Attaches.Count == attaches && ReferenceEquals(Terminal(second), secondView) && Terminal(first) is null && terminals.Views.Count == 2,
            "Hidden terminal members must not attach again or be replaced.");

        // News in a hidden member lights its chip; showing it puts the light out.
        Require(!Dot(first), "No member may start marked.");
        ((SharpRail.UI.Terminal.TerminalView)firstView).Write("echo phone-chip-activity\r");
        Pump(() => Dot(first), "Output in a hidden member must mark its chip.");
        Require(!Dot(second) && !Dot(notes), "Only the member with news may be marked.");
        surface.NoteActivity(second);
        Require(!Dot(second), "The member on screen is never marked.");
        Click(Chips().Single(chip => Equals(chip.Tag, first)));
        Shows(first, "The marked member must show when chosen.");
        Require(!Dot(first) && ReferenceEquals(Terminal(first), firstView), "Showing a member must clear its mark and keep its terminal.");
        Require(Shared() == shared, "Chips must never change the shared layout.");

        // A selection made elsewhere (Projects, another client) is followed.
        layout.Select(group, notes);
        Pump(() => surface.PhonePanes[group].Shown == notes, "A new selection in the group must show that member.");
        window.Close();
        Console.WriteLine("PASS phone tab groups: one member under chips, chip and swipe switching, local choice, activity marks");
    }

    private static bool IsHitTestVisibleAt(this Border host, InputElement window, Point point) =>
        host.IsEffectivelyVisible && window.InputHitTest(point) is Visual hit && host.IsVisualAncestorOf(hit);
}