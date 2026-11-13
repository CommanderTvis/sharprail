using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;

using Tereditor.Plugins.UI.Kit;
using Tereditor.UI.Panels;
using Tereditor.UI.Rendering;

namespace Tereditor.UI;

/// <summary>
/// The window without a top bar (<see cref="UnifiedChrome"/>): the controls the bar carried move into a header on top of the Projects
/// rail, and Settings leaves the window's corner for the overflow menu. The moved controls keep their names, so
/// every reader of them is unchanged.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private Grid? railLightsRow;

    /// <summary>The room above the rail's header where there is no title row for the window's buttons.</summary>
    internal const double RailTopInset = 8;

    private void WireUnifiedChrome()
    {
        var rootGrid = this.FindControl<Grid>("WorkbenchRoot")!;
        // The bar is gone from layout; its controls are re-parented below before the first rebuild draws them.
        this.FindControl<Border>("WindowTitleBar")!.IsVisible = false;
        rootGrid.RowDefinitions[0].Height = new GridLength(0);

        // A Mac has the native application menu (About, Settings), so only the browser draws the overflow button.
        Button? more = null;
        if (!OperatingSystem.IsMacOS())
        {
            more = new Button
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
        }

        // The add-project button leaves the rail's caption row (there is none) and sits beside the overflow button where there is one.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { NewAddProjectButton() } };
        if (more is not null) actions.Children.Add(more);

        var header = new StackPanel { Name = "RailHeader", Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        if (OperatingSystem.IsMacOS())
        {
            // The traffic lights sit over the top-left of this row, which is the window's draggable title region.
            railLightsRow = new Grid { Name = "RailLightsRow", ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(railLightsRow, actions, 0, 1);
            header.Children.Add(railLightsRow);
        }

        var project = this.FindControl<Border>("ProjectSegment")!;
        var branch = this.FindControl<Border>("BranchSegment")!;
        foreach (var segment in new[] { project, branch })
        {
            ((Panel)segment.Parent!).Children.Remove(segment);
            segment.MaxWidth = double.PositiveInfinity;
            // The top bar gives each segment a fixed width; in the rail it follows the rail's width.
            segment.Width = double.NaN;
            segment.HorizontalAlignment = HorizontalAlignment.Stretch;
            segment.Padding = new Thickness(8, 0, 8, 0);
        }
        if (railLightsRow is null)
        {
            // Without window chrome to clear, the overflow button shares the project's row, and the header's own top inset
            // stands in for the title row the traffic lights leave on a Mac: the browser's page edge is not a window frame.
            header.Margin = new Thickness(0, RailTopInset, 0, 8);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(line, project); Ui.Place(line, actions, 0, 1);
            header.Children.Add(line);
        }
        else header.Children.Add(project);
        // Every block is one pattern: a caption over a value row with the same insets, icon column and height. The value pill fills the
        // rail's width, and the branch's sync state sits at the right end of its own row.
        foreach (var pill in new[] { "ScopeProject", "ScopeBranch" }) this.FindControl<Button>(pill)!.HorizontalAlignment = HorizontalAlignment.Stretch;
        // The branch and remote blocks are equal columns with the usual 8 gap; without a remote status the remote block is gone and
        // the branch block takes the whole row.
        var columns = this.FindControl<Grid>("BranchColumns")!;
        var remoteBlock = this.FindControl<Grid>("RemoteBlock")!;
        remoteBlock.Margin = default; remoteBlock.MinWidth = 0;
        this.FindControl<Button>("ScopeBranch")!.MinWidth = 0;
        // The grid always has three columns, so the remote block's column stays valid (a block placed past the last column is
        // clamped to the first and the grid keeps drawing it there); the unused two collapse to nothing.
        void Split() => columns.ColumnDefinitions = new ColumnDefinitions(remoteBlock.IsVisible ? "*,8,*" : "*,0,0");
        Split();
        Grid.SetColumn(remoteBlock, 2);
        remoteBlock.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Split(); };
        // Where there is no branch to show (Project Home, a folder that is not a repository) the segment's room stays, empty, so the
        // blocks below it keep their place across every project.
        var reserve = new Border { Name = "BranchReserve", Height = 34, IsVisible = !branch.IsVisible, IsHitTestVisible = false };
        branch.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) reserve.IsVisible = !branch.IsVisible; };
        header.Children.Add(reserve);
        header.Children.Add(branch);

        // The plugins' header items (the credits) take the same captioned block under the branch.
        var tools = this.FindControl<StackPanel>("HeaderTools")!;
        var items = tools.Children.OfType<Plugins.PluginHeaderItems>().Single();
        tools.Children.Remove(items);
        var credits = new Border { Name = "CreditsSegment", Padding = new Thickness(8, 0, 8, 0), IsVisible = false };
        var creditsCaption = new TextBlock { Text = "Credits" };
        creditsCaption.Classes.Add("location-caption"); creditsCaption.Classes.Add("tr-text-metadata");
        var creditsRows = new Grid { RowDefinitions = new RowDefinitions("14,20"), VerticalAlignment = VerticalAlignment.Center };
        Ui.Place(creditsRows, creditsCaption); creditsRows.Children.Add(items); Grid.SetRow(items, 1);
        items.Orientation = Orientation.Vertical; items.HorizontalAlignment = HorizontalAlignment.Stretch;
        credits.Child = creditsRows;
        var adopted = new HashSet<Button>();
        items.LayoutUpdated += (_, _) =>
        {
            var buttons = items.GetLogicalDescendants().OfType<Button>().ToArray();
            credits.IsVisible = buttons.Any(button => button.IsVisible);
            foreach (var button in buttons.Where(adopted.Add))
            {
                // The value is a pill like the project's: the same inset, icon column and text role, filling the row.
                button.Padding = new Thickness(8, 0); button.Height = 20; button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Classes.Add("location-pill"); button.Classes.Add("tr-title-section");
                foreach (var text in button.GetLogicalDescendants().OfType<TextBlock>()) Typography.Apply(text, Typography.TitleSection);
            }
        };
        header.Children.Add(credits);

        // The Host block (the listener's mark and the connection state) is the last captioned block, with the segments' inset; it is
        // gone while it has nothing to show, so only the project list below it moves.
        var host = this.FindControl<Grid>("HostSegment")!;
        tools.Children.Remove(host);
        host.Margin = new Thickness(8, 0, 8, 0);
        header.Children.Add(host);

        // Settings is not drawn in the window: the application menu and the overflow menu open it. The button stays in the
        // hidden title bar, which the window's Settings wiring and the other layouts share.
        SuppressButtonMenus(header);
        header.ContextMenu = NewViewMenu();
        if (more is null) viewMenu = header.ContextMenu;
        surface.LeftHeader = header;
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