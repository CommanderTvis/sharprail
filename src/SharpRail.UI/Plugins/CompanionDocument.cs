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
    private readonly Grid grid = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), ColumnDefinitions = new ColumnDefinitions("*,Auto,0") };
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
        Grid.SetRow(Accessories, 2);
        SizeChanged += (_, _) => LayoutCompanion();
        splitter.DragCompleted += (_, _) => WidthChanged?.Invoke(grid.ColumnDefinitions[2].ActualWidth);
        Grid.SetColumnSpan(Accessories, 3);
        grid.Children.Add(splitter);
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

    /// <summary>Whether the open companion currently covers the document (the view is narrower than <see cref="Terminal.TerminalView.CompanionOverlayBelow"/>).</summary>
    public bool Overlay { get; private set; }

    /// <summary>Raised with the column's width in pixels after the user drags the splitter or closes a resized companion.</summary>
    public event Action<double>? WidthChanged;

    /// <summary>The companion column's width in pixels when it was resized, else null (the first opening takes 45%). Setting it restores a saved width.</summary>
    public double? CompanionWidth
    {
        get => companion.IsVisible && !Overlay && grid.ColumnDefinitions[2].ActualWidth > 0 ? grid.ColumnDefinitions[2].ActualWidth
            : companionWidth.IsAbsolute ? companionWidth.Value : null;
        set
        {
            if (value is not > 0) return;
            companionWidth = new GridLength(value.Value);
            if (!companion.IsVisible || Overlay) return;
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = companionWidth;
        }
    }

    private bool Narrow => Bounds.Width is > 0 && Bounds.Width < Terminal.TerminalView.CompanionOverlayBelow;

    /// <summary>The embedded companion pane beside the document (over it when narrow); null closes it.</summary>
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
                if (!visible && !Overlay && column.ActualWidth > 0)
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

    // Narrow, the open companion spans the view over the document with its own surface colour; wide, it is the third column again.
    private void LayoutCompanion()
    {
        var overlay = companion.IsVisible && Narrow;
        if (overlay != Overlay)
        {
            Overlay = overlay;
            var columns = grid.ColumnDefinitions;
            Grid.SetColumn(companion, overlay ? 0 : 2);
            Grid.SetColumnSpan(companion, overlay ? 3 : 1);
            companion.Background = overlay ? Ui.Surface : null;
            companion.ZIndex = overlay ? 5 : 0;
            if (overlay)
            {
                if (columns[2].ActualWidth > 0) companionWidth = new(columns[2].ActualWidth);
                columns[2].Width = new GridLength(0);
            }
            else if (companion.IsVisible)
            {
                columns[0].Width = new GridLength(1, GridUnitType.Star);
                columns[2].Width = companionWidth;
            }
        }
        splitter.IsVisible = companion.IsVisible && !overlay;
    }
}