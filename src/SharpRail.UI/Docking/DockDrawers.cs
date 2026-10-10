using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpRail.UI.Docking;

// A drawer has no room for two groups stacked or two tools side by side. The right drawer lists every tool of
// its region as a section header and shows one of them, at the height the others leave.
public sealed partial class DockSurface
{
    private string? drawerSection;

    private Control BuildSections(string region)
    {
        var sections = Session.State.Groups.Where(group => group.Region == region)
            .SelectMany(group => Session.Tabs(group.Id).Select(tab => (Group: group, Tab: tab))).ToArray();
        var grid = new Grid { Name = "DrawerSections_" + region };
        if (sections.Length == 0) return grid;
        var open = sections.FirstOrDefault(section => section.Tab.Id == drawerSection);
        if (open.Tab is null) open = sections.FirstOrDefault(section => Session.Selected(section.Group.Id)?.Id == section.Tab.Id);
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
                // Selecting tells the tool it is on screen; an already selected one changes nothing, so redraw.
                if (Session.Selected(group.Id)?.Id == tab.Id) Rebuild();
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