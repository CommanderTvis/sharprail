using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The slim row at the top of a tab's body that stands for its companion: the icon, the drawing's title and a count, in
/// muted text on the tab's own background with a divider below. Pressing it opens or closes the companion pane; with
/// several items a chevron at the end lists them. The same row on terminal and chat tabs.
/// </summary>
internal static class CompanionStrip
{
    public const double Height = LayoutRoles.PanelHeaderRow;

    public static Border Create(string name, Control icon, string title, string noun, IReadOnlyList<string> items, bool open, Action toggle, Action<int>? select)
    {
        var count = Math.Max(items.Count, 1);
        var summary = Ui.Text("· " + count + " " + noun + (count == 1 ? "" : "s"), Ui.Muted, 12);
        var label = Ui.Text(title, Ui.Muted, 12);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(summary);
        var toggler = new Button
        {
            Name = name + "Toggle",
            Content = row,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(LayoutRoles.PanelInset, 0),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Chips.Tip(toggler, open ? "Close " + title : "Open " + title);
        toggler.Click += (_, _) => toggle();
        var dock = new DockPanel { LastChildFill = true };
        if (items.Count > 1)
        {
            var menu = new MenuFlyout();
            for (var index = 0; index < items.Count; index++)
            {
                var chosen = index;
                var item = new MenuItem { Header = items[index] };
                item.Click += (_, _) => select?.Invoke(chosen);
                menu.Items.Add(item);
            }
            var chevron = new Button
            {
                Name = name + "Items",
                Content = Ui.Icon("arrowDown", Ui.Muted, 14),
                Width = LayoutRoles.PanelHeaderRow,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(0),
                Flyout = menu
            };
            Chips.Tip(chevron, "Choose a " + noun);
            DockPanel.SetDock(chevron, Dock.Right);
            dock.Children.Add(chevron);
        }
        dock.Children.Add(toggler);
        return new Border
        {
            Name = name,
            Height = Height,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Ui.BorderBrush,
            Child = dock
        };
    }
}