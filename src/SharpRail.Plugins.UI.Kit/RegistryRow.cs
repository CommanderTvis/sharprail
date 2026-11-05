using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// One row of a registry-style list, laid out like a row of Settings › Code Intelligence: [status][icon][title][settings][action] on
/// fixed columns, so status, icon and action line up across rows, with the details under the title, a divider under the row and an
/// accent bar at the left edge while selected. Plugins list their own things (agents, servers) in the same shape.
/// </summary>
public static class RegistryRow
{
    public const double StatusWidth = 20;
    public const double IconWidth = 28;
    public const double SettingsWidth = 28;

    /// <summary>Builds the row. Any part may be null, and its column stays empty.</summary>
    public static Border Create(string name, Control? status, Control? icon, Control title, Control? settings, Control? action, Control? details)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions { new(new GridLength(StatusWidth)), new(new GridLength(IconWidth)), new(new GridLength(1, GridUnitType.Star)), new(new GridLength(SettingsWidth)), new(GridLength.Auto) }
        };
        Place(grid, status, 0);
        Place(grid, icon, 1);
        Place(grid, title, 2);
        Place(grid, settings, 3);
        Place(grid, action, 4);
        if (details is not null)
        {
            Grid.SetRow(details, 1);
            Grid.SetColumn(details, 2);
            Grid.SetColumnSpan(details, 3);
            grid.Children.Add(details);
        }
        var surface = new Border { Name = name + "_Surface", BorderBrush = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 4, 0, 4), Child = grid };
        return new Border { Name = name, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = surface };
    }

    private static void Place(Grid grid, Control? part, int column)
    {
        if (part is null) return;
        part.VerticalAlignment = VerticalAlignment.Center;
        if (column < 2) part.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(part, column);
        grid.Children.Add(part);
    }
}