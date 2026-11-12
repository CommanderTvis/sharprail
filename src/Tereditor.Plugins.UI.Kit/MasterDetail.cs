using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>
/// A narrow list on the left and the selected item's settings on the right, as on Settings › Code Intelligence: the list has a divider at its
/// right edge, a highlighted selected row and a small red dot on a row with a problem. Plugins fill the list with <see cref="Row"/> items and put
/// the selected item's pane in <see cref="Detail"/>.
/// </summary>
public sealed class MasterDetail : Grid
{
    private bool setting;

    public MasterDetail(string name, double railWidth = 200)
    {
        Name = name;
        ColumnDefinitions = new ColumnDefinitions { new(new GridLength(railWidth)), new(new GridLength(1, GridUnitType.Star)) };
        Rail = new ListBox { Name = name + "Rail", Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, AutoScrollToSelectedItem = false };
        ScrollViewer.SetVerticalScrollBarVisibility(Rail, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(Rail, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Rail.SelectionChanged += (_, _) =>
        {
            if (!setting) Selected?.Invoke(SelectedId);
        };
        var divider = new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 8, 0), Child = new StackPanel { Spacing = 8, Children = { Header, Rail, Footer } } };
        Detail = new ContentControl { Name = name + "Detail", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        SetColumn(Detail, 1);
        Children.Add(divider);
        Children.Add(Detail);
    }

    public ListBox Rail { get; }

    /// <summary>Above the list: actions that are not items of it, and a heading for it.</summary>
    public StackPanel Header { get; } = new() { Spacing = 4 };

    /// <summary>Under the list: buttons that act on the list itself, such as adding an item.</summary>
    public StackPanel Footer { get; } = new() { Spacing = 4 };

    /// <summary>The right-hand pane: whatever the selected row configures.</summary>
    public ContentControl Detail { get; }

    /// <summary>Raised with the id of the row the user selected (not when the list is filled by <see cref="SetRows"/>).</summary>
    public event Action<string?>? Selected;

    public string? SelectedId => (Rail.SelectedItem as Control)?.Tag as string;

    /// <summary>Replaces the rows and keeps <paramref name="select"/> selected (the first row when it is absent, none when <paramref name="none"/>). Does not raise <see cref="Selected"/>.</summary>
    public void SetRows(IReadOnlyList<Control> rows, string? select, bool none = false)
    {
        setting = true;
        try
        {
            Rail.ItemsSource = rows;
            Rail.SelectedItem = none ? null : rows.FirstOrDefault(row => row.Tag as string == select) ?? rows.FirstOrDefault();
        }
        finally { setting = false; }
    }

    /// <summary>One row of the list: an icon, the name with a small line under it, and a red dot (with the problem as its tooltip) when something is wrong.</summary>
    public static Control Row(string id, Control? icon, string name, string? subtitle, string? problem)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.TextBrush });
        if (!string.IsNullOrEmpty(subtitle)) text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Muted });
        var row = new Grid { Tag = id, Name = "MasterRow_" + id, ColumnDefinitions = new ColumnDefinitions { new(new GridLength(24)), new(new GridLength(1, GridUnitType.Star)), new(GridLength.Auto) } };
        if (icon is not null) { icon.VerticalAlignment = VerticalAlignment.Center; icon.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(icon); }
        SetColumn(text, 1);
        row.Children.Add(text);
        if (problem is not null)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Name = "MasterProblem_" + id, Width = 6, Height = 6, Fill = Ui.Danger, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            ToolTip.SetTip(dot, problem);
            SetColumn(dot, 2);
            row.Children.Add(dot);
        }
        return row;
    }
}