using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The body of a plugin document that hosts companions (a chat tab): the document beside an embedded split for the open
/// companion pane, with the title chips in a strip below, as a terminal's body has. The name keeps the plugin-mount
/// prefix so a plugin that goes away takes the body with it.
/// </summary>
internal sealed class CompanionDocument : ContentControl
{
    private readonly ContentControl companion = new() { Name = "DocumentCompanion", IsVisible = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly GridSplitter splitter = new() { Name = "DocumentCompanionSplitter", Width = 1, IsVisible = false, Background = Ui.BorderBrush, ResizeDirection = GridResizeDirection.Columns };
    private readonly Grid grid = new() { RowDefinitions = new RowDefinitions("*,Auto"), ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
    private GridLength companionWidth = new(45.0 / 55, GridUnitType.Star);

    public CompanionDocument(string pluginId, CompanionHost host, Control content)
    {
        Name = "PluginMount_" + pluginId;
        Host = host;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Accessories = new StackPanel { Name = "DocumentAccessories" };
        grid.Children.Add(content);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(companion, 2);
        Grid.SetRow(Accessories, 1);
        Grid.SetColumnSpan(Accessories, 3);
        grid.Children.Add(splitter);
        grid.Children.Add(companion);
        grid.Children.Add(Accessories);
        Content = grid;
    }

    public CompanionHost Host { get; }

    /// <summary>The strip below the body that holds the companion chips.</summary>
    public StackPanel Accessories { get; }

    /// <summary>The embedded companion pane beside the document; null closes it.</summary>
    public Control? Companion
    {
        get => companion.Content as Control;
        set
        {
            var visible = value is not null;
            if (visible != companion.IsVisible)
            {
                var column = grid.ColumnDefinitions[2];
                // The first opening takes 45%; afterwards the width the user left is restored in pixels.
                if (!visible && column.ActualWidth > 0) companionWidth = new(column.ActualWidth);
                // A splitter drag rescales both star columns; the document's is reset to 1* so it owns the rest.
                grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                column.Width = visible ? companionWidth : new GridLength(0);
            }
            companion.Content = value;
            companion.IsVisible = splitter.IsVisible = visible;
        }
    }
}