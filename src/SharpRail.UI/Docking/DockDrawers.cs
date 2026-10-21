using System.Text.Json;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SharpRail.UI.Docking;

// A phone's side pages have no room for two groups stacked or two tools side by side. The left page is Projects
// alone, without a tab strip; the right page lists every other side tool as a section header and shows one of
// them, at the height the others leave.
public sealed partial class DockSurface
{
    private const string ProjectsTool = "projects";
    private string? drawerSection, sectionsBuilt;
    private int shownPage, slideRun;
    // The page a running slide carries out and the one it carries in: both live hosts, side by side in the shell.
    private (int From, int To)? slide;
    private double slideProgress;
    private bool slideHeld;
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(260);
    private readonly PageSlide pageSlide = new(SlideTime);
    private readonly Dictionary<string, Border[]> pageBodies = [];
    private readonly Dictionary<int, IInputElement> pageFocus = [];

    // Projects, the centre and Tools are three hosts that live as long as the surface: a page switch moves them and
    // leaves their contents, scroll positions and focus where they were. A side page is built the first time it shows.
    private Border PageHost(int page) => page < 0 ? auxiliaryRegions["left"] : page > 0 ? auxiliaryRegions["right"] : centerRegion;

    private bool PageOnScreen(int page) => page == shownPage || slide is { } pages && (pages.From == page || pages.To == page);

    /// <summary>The page shown at rest or being slid to; -1 is Projects, 0 the centre and 1 Tools.</summary>
    internal int ShownPage => shownPage;

    /// <summary>Whether a page slide is running or held under a finger.</summary>
    internal bool PageSliding => slide is not null;

    // Projects, the centre and Tools lie side by side. Moving between them carries the page being left out and
    // the next one in together: under the finger while it drags, and the rest of the way once it lets go.
    private void SwitchPage()
    {
        var next = Side("left") ? -1 : Side("right") ? 1 : 0;
        if (next == shownPage) { ShowPages(); return; }
        var from = shownPage;
        var progress = 0.0;
        if (slide is { } running)
        {
            // A switch that interrupts a slide turns it round when it goes back, and otherwise starts from the page
            // that is most of the way on screen.
            if (next == running.From) progress = 1 - slideProgress;
            else from = slideProgress < .5 ? running.From : running.To;
        }
        else RememberFocus(from);
        EndSlide();
        shownPage = next;
        if (from == next || Bounds.Width <= 0 || TopLevel.GetTopLevel(this) is null)
        {
            ShowPages();
            RestoreFocus(next);
            return;
        }
        slide = (from, next);
        slideProgress = progress;
        // The page on its way out takes no new touch: what is under a finger is the page coming in, or nothing. A
        // finger already dragging keeps the page it pressed, or its touch would lose its target mid-drag.
        if (!slideHeld) PageHost(from).IsHitTestVisible = false;
        ShowPages();
        PlaceSlide();
        if (!slideHeld) SettleSlide(true, null);
    }

    // Only the page on screen, and the one sliding with it, is visible: the others take no input, focus or frames.
    private void ShowPages()
    {
        for (var page = -1; page <= 1; page++)
        {
            var host = PageHost(page);
            var shown = PageOnScreen(page);
            if (shown) BuildPage(page);
            else pageSlide.Reset(host);
            host.IsVisible = shown && (page == 0 || host.Child is not null);
        }
    }

    private void BuildPage(int page)
    {
        if (page == 0) return;
        var region = page < 0 ? "left" : "right";
        var frame = auxiliaryRegions[region];
        if (frame.Child is not null || !Side(region)) return;
        var hosts = contentHosts.Count;
        frame.Child = page < 0 ? BuildProjects() : BuildSections();
        pageBodies[region] = [.. contentHosts.Skip(hosts)];
    }

    private void DropPage(string region)
    {
        foreach (var body in pageBodies.GetValueOrDefault(region) ?? []) { body.Child = null; contentHosts.Remove(body); }
        pageBodies.Remove(region);
        auxiliaryRegions[region].Child = null;
    }

    // The sections are drawn from every side group at once: redrawn when what they show changed, on screen now or
    // the next time Tools shows.
    private void RefreshSections()
    {
        if (auxiliaryRegions["right"].Child is null || SectionsSignature() == sectionsBuilt) return;
        DropPage("right");
        if (PageOnScreen(1)) ShowPages();
    }

    private void PlaceSlide()
    {
        if (slide is not { } pages) return;
        // PageSlide moves a page by changing its TranslateTransform in place, which on Android never moved the page on
        // screen; a new transform each frame, which PageSlide then sets, does.
        PageHost(pages.From).RenderTransform = new TranslateTransform();
        PageHost(pages.To).RenderTransform = new TranslateTransform();
        pageSlide.Update(slideProgress, PageHost(pages.From), PageHost(pages.To), pages.To > pages.From, shell.Bounds.Width, []);
    }

    private void EndSlide()
    {
        slideRun++;
        if (slide is not { } pages) return;
        pageSlide.Reset(PageHost(pages.From));
        pageSlide.Reset(PageHost(pages.To));
        PageHost(pages.From).IsHitTestVisible = true;
        slide = null;
    }

    private void SettleSlide(bool complete, Action? revert)
    {
        var run = ++slideRun;
        var from = slideProgress;
        var to = complete ? 1.0 : 0;
        var time = SlideTime * Math.Max(0.4, Math.Abs(to - from));
        var started = System.Diagnostics.Stopwatch.StartNew();
        void Step(TimeSpan _)
        {
            if (run != slideRun || slide is not { } pages) return;
            var elapsed = Math.Min(1, started.Elapsed / time);
            if (elapsed < 1 && TopLevel.GetTopLevel(this) is { } top)
            {
                slideProgress = from + (to - from) * (1 - Math.Pow(1 - elapsed, 3));
                PlaceSlide();
                top.RequestAnimationFrame(Step);
                return;
            }
            // The page that was left is back at rest before the state follows it, so that change has nothing to move.
            if (!complete) shownPage = pages.From;
            EndSlide();
            ShowPages();
            RestoreFocus(shownPage);
            if (!complete) revert?.Invoke();
        }
        Step(default);
    }

    private void RememberFocus(int page)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused && PageHost(page).IsVisualAncestorOf(focused))
            pageFocus[page] = (IInputElement)focused;
    }

    private void RestoreFocus(int page)
    {
        if (pageFocus.Remove(page, out var focused) && focused is Visual visual && PageHost(page).IsVisualAncestorOf(visual))
            focused.Focus();
    }

    // Leaving the phone layout puts the centre back for the columns, with nothing left mid-slide.
    private void LeavePages()
    {
        EndSlide();
        shownPage = 0;
        pageFocus.Clear();
        pageBodies.Clear();
        centerRegion.IsVisible = true;
    }

    /// <summary>Changes the page for a finger that is dragging it in: the pages wait for <see cref="DragPage"/> rather than slide by themselves.</summary>
    internal void HoldPage(Action change)
    {
        slideHeld = true;
        change();
    }

    /// <summary>Places the held pages for a finger that has travelled this far sideways.</summary>
    internal void DragPage(double travelled)
    {
        if (!slideHeld || slide is not { } pages) return;
        slideProgress = Math.Clamp(-Math.Sign(pages.To - pages.From) * travelled / Math.Max(1, Bounds.Width), 0, 1);
        PlaceSlide();
    }

    /// <summary>Lets the held pages go: on to the new page, or back to the one left, which <paramref name="revert"/> then shows again.</summary>
    internal void ReleasePage(bool complete, Action revert)
    {
        if (!slideHeld) return;
        slideHeld = false;
        if (slide is not null) SettleSlide(complete, revert);
        else if (!complete) revert();
    }

    private Control BuildProjects()
    {
        var body = new Border { Name = "ProjectsPage", ClipToBounds = true };
        var projects = Session.State.Groups.SelectMany(group => Session.Tabs(group.Id)).FirstOrDefault(tab => tab.Id == ProjectsTool) ?? DockState.Tool(ProjectsTool);
        body.Child = Adopt(renderContent(projects));
        contentHosts.Add(body);
        return body;
    }

    // Every tool the workbench has, in a side group or not: plugins' tools that no group holds yet included.
    private ((DockGroup? Group, DockTab Tab)[] Sections, (DockGroup? Group, DockTab Tab) Open) Sections()
    {
        var placed = Session.State.Groups.SelectMany(group => Session.Tabs(group.Id).Where(tab => tab.IsTool).Select(tab => (Group: (DockGroup?)group, Tab: tab)))
            .ToDictionary(tool => tool.Tab.Id);
        var sections = Session.Tools.Where(tool => tool.Id != ProjectsTool)
            .Select(tool => placed.TryGetValue(tool.Id, out var seat) ? seat : (Group: null, Tab: DockState.Tool(tool.Id))).ToArray();
        if (sections.Length == 0) return (sections, default);
        var open = sections.FirstOrDefault(section => section.Tab.Id == drawerSection);
        if (open.Tab is null) open = sections.FirstOrDefault(section => section.Group is { Region: "right" } seat && Session.Selected(seat.Id)?.Id == section.Tab.Id);
        if (open.Tab is null) open = sections[0];
        return (sections, open);
    }

    private string SectionsSignature()
    {
        var (sections, open) = Sections();
        return JsonSerializer.Serialize(new
        {
            Open = open.Tab?.Id,
            Sections = sections.Select(section => new { section.Tab.Id, Group = section.Group?.Id, Title = Session.ToolTitle(section.Tab) })
        });
    }

    private Control BuildSections()
    {
        var (sections, open) = Sections();
        sectionsBuilt = SectionsSignature();
        var grid = new Grid { Name = "DrawerSections" };
        if (sections.Length == 0) return grid;
        drawerSection = open.Tab.Id;
        foreach (var (group, tab) in sections)
        {
            var expanded = tab.Id == open.Tab.Id;
            var foreground = expanded ? Ui.TextBrush : Ui.Muted;
            var label = new Grid { ColumnDefinitions = new ColumnDefinitions("14,8,14,8,*,Auto"), Height = 40 };
            Ui.Place(label, Ui.Icon(expanded ? "arrowDown" : "arrowRight", foreground, 14));
            Ui.Place(label, TabIcon?.Invoke(tab, foreground) ?? Ui.Icon(ToolIcon(tab), foreground, 14), 0, 2);
            var title = Ui.Text(Session.ToolTitle(tab), foreground, 14);
            title.VerticalAlignment = VerticalAlignment.Center;
            Ui.Place(label, title, 0, 4);
            if (TabAdornment?.Invoke(tab) is { } adornment)
            {
                adornment.VerticalAlignment = VerticalAlignment.Center;
                Ui.Place(label, adornment, 0, 5);
            }
            var header = new Button
            {
                Name = "DrawerSection_" + tab.Id,
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 0),
                Background = Brushes.Transparent,
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(0, grid.RowDefinitions.Count == 0 ? 0 : 1, 0, expanded ? 1 : 0),
                CornerRadius = default
            };
            AutomationProperties.SetName(header, Session.ToolTitle(tab));
            header.Click += (_, _) =>
            {
                if (drawerSection == tab.Id) return;
                drawerSection = tab.Id;
                // Selecting tells a seated tool it is on screen; one already selected, or seated nowhere, only redraws.
                if (group is null || Session.Selected(group.Id)?.Id == tab.Id) RefreshSections();
                else Session.Select(group.Id, tab.Id);
            };
            grid.RowDefinitions.Add(new(GridLength.Auto));
            Ui.Place(grid, header, grid.RowDefinitions.Count - 1);
            if (!expanded) continue;
            var body = new Border { Name = "DrawerSectionBody", ClipToBounds = true, Child = Adopt(renderContent(tab)) };
            contentHosts.Add(body);
            grid.RowDefinitions.Add(new(new GridLength(1, GridUnitType.Star)));
            Ui.Place(grid, body, grid.RowDefinitions.Count - 1);
        }
        return grid;
    }
}