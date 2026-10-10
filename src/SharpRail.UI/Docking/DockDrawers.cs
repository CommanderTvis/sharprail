using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SharpRail.UI.Docking;

// A phone's side pages have no room for two groups stacked or two tools side by side. The left page is Projects
// alone, without a tab strip; the right page lists every other side tool as a section header and shows one of
// them, at the height the others leave.
public sealed partial class DockSurface
{
    private const string ProjectsTool = "projects";
    private string? drawerSection;
    private int shownPage, slideRun;
    private (RenderTargetBitmap Picture, Image Leaving, int Direction)? slide;
    private double slideProgress;
    private bool slideHeld, slideSkipped;
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(260);

    // Projects, the centre and Tools lie side by side. Moving between them carries the page being left out and
    // the next one in together: under the finger while it drags, and the rest of the way once it lets go.
    private Action? PageSlide()
    {
        var next = Side("left") ? -1 : Side("right") ? 1 : 0;
        var direction = Math.Sign(next - shownPage);
        shownPage = next;
        if (direction == 0) return null;
        EndSlide();
        if (slideSkipped) { slideSkipped = false; return null; }
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || TopLevel.GetTopLevel(this) is not { } top) return null;
        var scaling = top.RenderScaling;
        // Rendering a visual into a bitmap applies the bitmap's density twice, so its root gives the screen's pixels.
        var density = 96 * Math.Sqrt(scaling);
        var picture = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(Bounds.Width * scaling), (int)Math.Ceiling(Bounds.Height * scaling)), new Vector(density, density));
        picture.Render(shell);
        return () =>
        {
            var leaving = new Image { Source = picture, Width = Bounds.Width, Height = Bounds.Height, Stretch = Stretch.Fill };
            overlay.Children.Add(leaving);
            slide = (picture, leaving, direction);
            slideProgress = 0;
            PlaceSlide();
            if (!slideHeld) SettleSlide(true, null);
        };
    }

    private void PlaceSlide()
    {
        if (slide is not { } pages) return;
        shell.RenderTransform = new TranslateTransform(pages.Direction * Bounds.Width * (1 - slideProgress), 0);
        pages.Leaving.RenderTransform = new TranslateTransform(-pages.Direction * Bounds.Width * slideProgress, 0);
    }

    private void EndSlide()
    {
        slideRun++;
        if (slide is { } pages)
        {
            overlay.Children.Remove(pages.Leaving);
            pages.Picture.Dispose();
            slide = null;
        }
        shell.RenderTransform = null;
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
            if (run != slideRun) return;
            var elapsed = Math.Min(1, started.Elapsed / time);
            if (elapsed < 1 && TopLevel.GetTopLevel(this) is { } top)
            {
                slideProgress = from + (to - from) * (1 - Math.Pow(1 - elapsed, 3));
                PlaceSlide();
                top.RequestAnimationFrame(Step);
            }
            else if (complete) EndSlide();
            else
            {
                // The page that was left comes back under its picture, which goes once it is rebuilt.
                slideProgress = 0;
                PlaceSlide();
                slideSkipped = true;
                revert?.Invoke();
                if (slideSkipped) { slideSkipped = false; EndSlide(); }
            }
        }
        Step(default);
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
        slideProgress = Math.Clamp(-pages.Direction * travelled / Math.Max(1, Bounds.Width), 0, 1);
        PlaceSlide();
    }

    /// <summary>Lets the held pages go: on to the new page, or back to the one left, which <paramref name="revert"/> then shows again.</summary>
    internal void ReleasePage(bool complete, Action revert)
    {
        if (!slideHeld) return;
        slideHeld = false;
        if (slide is not null) SettleSlide(complete, revert);
        else if (!complete)
        {
            slideSkipped = true;
            revert();
            slideSkipped = false;
        }
    }

    private Control BuildProjects()
    {
        var body = new Border { Name = "ProjectsPage", ClipToBounds = true };
        var projects = Session.State.Groups.SelectMany(group => Session.Tabs(group.Id)).FirstOrDefault(tab => tab.Id == ProjectsTool) ?? DockState.Tool(ProjectsTool);
        body.Child = Adopt(renderContent(projects));
        contentHosts.Add(body);
        return body;
    }

    private Control BuildSections()
    {
        // Every tool the workbench has, in a side group or not: plugins' tools that no group holds yet included.
        var placed = Session.State.Groups.SelectMany(group => Session.Tabs(group.Id).Where(tab => tab.IsTool).Select(tab => (Group: (DockGroup?)group, Tab: tab)))
            .ToDictionary(tool => tool.Tab.Id);
        var sections = Session.Tools.Where(tool => tool.Id != ProjectsTool)
            .Select(tool => placed.TryGetValue(tool.Id, out var seat) ? seat : (Group: null, Tab: DockState.Tool(tool.Id))).ToArray();
        var grid = new Grid { Name = "DrawerSections" };
        if (sections.Length == 0) return grid;
        var open = sections.FirstOrDefault(section => section.Tab.Id == drawerSection);
        if (open.Tab is null) open = sections.FirstOrDefault(section => section.Group is { Region: "right" } seat && Session.Selected(seat.Id)?.Id == section.Tab.Id);
        if (open.Tab is null) open = sections[0];
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
                if (group is null || Session.Selected(group.Id)?.Id == tab.Id) Rebuild();
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