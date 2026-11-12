using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The body of a plugin document that hosts companions (a chat tab): the document beside an embedded split for the open
/// companion pane, with the companion strips in a row above it and the plugin accessories in a strip below, as a terminal's body has. The name keeps the plugin-mount
/// prefix so a plugin that goes away takes the body with it.
/// </summary>
internal sealed class CompanionDocument : ContentControl
{
    private readonly ContentControl companion = new() { Name = "DocumentCompanion", IsVisible = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly GridSplitter splitter = new() { Name = "DocumentCompanionSplitter", Width = 1, IsVisible = false, Background = Ui.BorderBrush, ResizeDirection = GridResizeDirection.Columns };
    private readonly Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,0,Auto"), ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
    private readonly GridSplitter stackSplitter = new() { Name = "DocumentCompanionStackSplitter", Height = 1, IsVisible = false, Background = Ui.BorderBrush, ResizeDirection = GridResizeDirection.Rows };
    private GridLength companionWidth = new(45.0 / 55, GridUnitType.Star);

    public CompanionDocument(string pluginId, CompanionHost host, Control content)
    {
        Name = "PluginMount_" + pluginId;
        Host = host;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Accessories = new StackPanel { Name = "DocumentAccessories" };
        Strips = new StackPanel { Name = "DocumentCompanionStrips" };
        Grid.SetColumnSpan(Strips, 3);
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(companion, 1);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(companion, 2);
        Grid.SetRow(Accessories, 4);
        Grid.SetRow(stackSplitter, 2);
        Grid.SetColumnSpan(stackSplitter, 3);
        SizeChanged += (_, _) => LayoutCompanion();
        splitter.DragCompleted += (_, _) => WidthChanged?.Invoke(grid.ColumnDefinitions[2].ActualWidth);
        Grid.SetColumnSpan(Accessories, 3);
        grid.Children.Add(splitter);
        grid.Children.Add(stackSplitter);
        grid.Children.Add(companion);
        grid.Children.Add(Accessories);
        grid.Children.Add(Strips);
        Content = grid;
    }

    public CompanionHost Host { get; }

    /// <summary>The rows above the body that stand for the document's companions; empty when it has none.</summary>
    public StackPanel Strips { get; }

    /// <summary>The strip below the body that holds the companion chips.</summary>
    public StackPanel Accessories { get; }

    /// <summary>Whether the open companion currently is stacked below the document (the view is narrower than <see cref="Terminal.TerminalView.CompanionStackBelow"/>).</summary>
    public bool Stacked { get; private set; }

    /// <summary>Raised with the column's width in pixels after the user drags the splitter or closes a resized companion.</summary>
    public event Action<double>? WidthChanged;

    /// <summary>The companion column's width in pixels when it was resized, else null (the first opening takes 45%). Setting it restores a saved width.</summary>
    public double? CompanionWidth
    {
        get => companion.IsVisible && !Stacked && grid.ColumnDefinitions[2].ActualWidth > 0 ? grid.ColumnDefinitions[2].ActualWidth
            : companionWidth.IsAbsolute ? companionWidth.Value : null;
        set
        {
            if (value is not > 0) return;
            companionWidth = new GridLength(value.Value);
            if (!companion.IsVisible || Stacked) return;
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = companionWidth;
        }
    }

    private bool Narrow => Bounds.Width is > 0 && Bounds.Width < Terminal.TerminalView.CompanionStackBelow;

    /// <summary>The embedded companion pane beside the document (below it when narrow); null closes it.</summary>
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
                if (!visible && !Stacked && column.ActualWidth > 0)
                {
                    companionWidth = new(column.ActualWidth);
                    WidthChanged?.Invoke(column.ActualWidth);
                }
                // A splitter drag rescales both star columns; the document's is reset to 1* so it owns the rest.
                grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                column.Width = visible && !Narrow ? companionWidth : new GridLength(0);
            }
            companion.Content = value;
            companion.IsVisible = visible;
            LayoutCompanion();
        }
    }

    // Narrow, the open companion spans the view below the document, split by a draggable splitter; wide, it is the third column again.
    private void LayoutCompanion()
    {
        var stacked = companion.IsVisible && Narrow;
        var rows = grid.RowDefinitions;
        if (stacked != Stacked)
        {
            Stacked = stacked;
            var columns = grid.ColumnDefinitions;
            Grid.SetRow(companion, stacked ? 3 : 1);
            Grid.SetColumn(companion, stacked ? 0 : 2);
            Grid.SetColumnSpan(companion, stacked ? 3 : 1);
            rows[1].Height = new GridLength(stacked ? 3 : 1, GridUnitType.Star);
            rows[3].Height = stacked ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
            if (stacked)
            {
                if (columns[2].ActualWidth > 0) companionWidth = new(columns[2].ActualWidth);
                columns[2].Width = new GridLength(0);
            }
            else
            {
                rows[1].MinHeight = rows[3].MinHeight = 0;
                if (companion.IsVisible)
                {
                    columns[0].Width = new GridLength(1, GridUnitType.Star);
                    columns[2].Width = companionWidth;
                }
            }
        }
        if (stacked && Bounds.Height > 0)
        {
            rows[1].MinHeight = Math.Max(Terminal.TerminalView.StackedSurfaceMinHeight, Bounds.Height * (1 - Terminal.TerminalView.CompanionStackMaxShare));
            rows[3].MinHeight = Terminal.TerminalView.StackedCompanionMinHeight;
        }
        splitter.IsVisible = companion.IsVisible && !stacked;
        stackSplitter.IsVisible = stacked;
    }
}
