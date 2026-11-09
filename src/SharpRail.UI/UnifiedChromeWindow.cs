using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

/// <summary>
/// The window without a top bar (<see cref="UnifiedChrome"/>): the controls the bar carried move into a header on top of the Projects
/// rail, and Settings and About into the footer of the right tool bar. The moved controls keep their names, so
/// every reader of them is unchanged.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private Grid? railLightsRow;

    private void WireUnifiedChrome()
    {
        var rootGrid = this.FindControl<Grid>("WorkbenchRoot")!;
        // The bar is gone from layout; its controls are re-parented below before the first rebuild draws them.
        this.FindControl<Border>("WindowTitleBar")!.IsVisible = false;
        rootGrid.RowDefinitions[0].Height = new GridLength(0);

        var more = new Button
        {
            Name = "AppMenuButton",
            Content = Ui.Icon("moreHorizontal", null, 14),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ((Border)more.Content!).Bind(Border.BackgroundProperty, more.GetObservable(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty));
        ToolTip.SetTip(more, "More"); AutomationProperties.SetName(more, "More");
        // The View menu belongs to this button; a right-click on the header's empty space opens it as well.
        // A context menu opens only on the control it is attached to: the button and the rail header each carry their own.
        viewMenu = more.ContextMenu = NewViewMenu();
        // Opening fires for a right-click only, so the button fills the menu itself.
        more.Click += (_, _) => { FillViewMenu(viewMenu); viewMenu.Open(more); };

        var header = new StackPanel { Name = "RailHeader", Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        if (OperatingSystem.IsMacOS())
        {
            // The traffic lights sit over the top-left of this row, which is the window's draggable title region.
            railLightsRow = new Grid { Name = "RailLightsRow", ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(railLightsRow, more, 0, 1);
            header.Children.Add(railLightsRow);
        }

        var project = this.FindControl<Border>("ProjectSegment")!;
        var branch = this.FindControl<Border>("BranchSegment")!;
        var pullRequest = this.FindControl<Border>("PullRequestSegment")!;
        foreach (var segment in new[] { project, branch, pullRequest })
        {
            ((Panel)segment.Parent!).Children.Remove(segment);
            segment.MaxWidth = double.PositiveInfinity;
            segment.HorizontalAlignment = HorizontalAlignment.Stretch;
            segment.Padding = new Thickness(8, 0, 8, 0);
        }
        if (railLightsRow is null)
        {
            // Without window chrome to clear, the overflow button shares the project's row.
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(line, project); Ui.Place(line, more, 0, 1);
            header.Children.Add(line);
        }
        else header.Children.Add(project);
        // The sync state wraps under the branch pill: a rail is narrower than the bar's cell.
        var remote = this.FindControl<StackPanel>("RemoteRow")!;
        if (remote.Parent is Grid branchLine)
        {
            branchLine.ColumnDefinitions = new ColumnDefinitions("*");
            branchLine.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(remote, 0); Grid.SetRow(remote, 1);
            remote.Margin = new Thickness(8, 2, 0, 0);
        }
        header.Children.Add(branch);
        header.Children.Add(pullRequest);

        // The status marks and the plugins' header items share one wrapping row under the branch block.
        var marks = new WrapPanel { Name = "RailStatus", Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
        var tools = this.FindControl<StackPanel>("HeaderTools")!;
        var settings = this.FindControl<Button>("SettingsButton")!;
        foreach (var control in tools.Children.Where(child => !ReferenceEquals(child, settings)).ToArray())
        {
            tools.Children.Remove(control);
            marks.Children.Add(control);
        }
        header.Children.Add(marks);

        // Settings and About close the right tool bar; Settings keeps its name, chord and tooltip.
        tools.Children.Remove(settings);
        settings.IsVisible = true;
        var about = new Button
        {
            Name = "AboutButton",
            Content = Ui.Icon("alertInfo"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0)
        };
        ((Border)about.Content!).Bind(Border.BackgroundProperty, about.GetObservable(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty));
        ToolTip.SetTip(about, "About"); AutomationProperties.SetName(about, "About");
        about.Click += (_, _) => Dialogs.About(this);
        SuppressButtonMenus(header);
        header.ContextMenu = NewViewMenu();
        surface.LeftHeader = header;
        surface.RightRailFooter = new StackPanel { Name = "RailFooter", Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4), Children = { about, settings } };
        ApplyChromeZoom();
    }

    /// <summary>
    /// The native traffic lights do not zoom, so the lights row grows with the zoom out and keeps its physical height;
    /// without chrome (the browser) there is no such row.
    /// </summary>
    private void ApplyUnifiedChromeZoom(double zoom)
    {
        this.FindControl<Grid>("WorkbenchRoot")!.RowDefinitions[0].Height = new GridLength(0);
        if (!OperatingSystem.IsMacOS()) return;
        var height = Math.Max(LayoutRoles.TopbarRow, LayoutRoles.TopbarRow / zoom);
        ExtendClientAreaTitleBarHeightHint = height * zoom;
        if (railLightsRow is not null) railLightsRow.Height = height;
    }
}