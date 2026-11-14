using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Tereditor.UI;

/// <summary>
/// The pill at the top or bottom edge of the Projects rail that points to the focused row while it is scrolled away: the focused
/// tab row of the open workspace when the rail marks one current, otherwise the open workspace's row. It floats over the list.
/// </summary>
public sealed partial class WorkbenchWindow
{
    // At least this share of the focused row's height inside the viewport counts as visible and hides the pill.
    internal const double RailEdgePillVisibleShare = 0.5;
    private Button? railEdgePill;
    private TextBlock? railEdgePillText;
    private Control? railEdgeTarget;
    private bool railEdgeQueued;

    private Button NewRailEdgePill(ScrollViewer scroller)
    {
        var chevron = new ContentControl { Name = "RailEdgePillChevron", Content = Ui.Icon("arrowDown", Ui.OnPrimary, 12), Margin = new Thickness(0, 0, 4, 0) };
        chevron.Classes.Add("rail-edge-chevron");
        DockPanel.SetDock(chevron, Dock.Left);
        railEdgePillText = new TextBlock { Name = "RailEdgePillText", TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Button { Name = "RailEdgePill", IsVisible = false, ZIndex = 1, Content = new DockPanel { Children = { chevron, railEdgePillText } } };
        pill.Classes.Add("primary");
        pill.Classes.Add("rail-edge-pill");
        pill.Click += (_, _) => RevealRailEdgeTarget();
        scroller.ScrollChanged += (_, _) => ShowRailEdgePill();
        // The panel is rebuilt with the rail; one subscription serves whichever pill is current.
        surface.DraggingChanged -= ShowRailEdgePill;
        surface.DraggingChanged += ShowRailEdgePill;
        railEdgePill = pill;
        return pill;
    }

    /// <summary>Looks for the focused row again once layout has settled: after a rebuild, a switch or a focus change.</summary>
    private void QueueRailEdgePill()
    {
        if (railEdgePill is null || railEdgeQueued) return;
        railEdgeQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            railEdgeQueued = false;
            railEdgeTarget = null;
            ShowRailEdgePill();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The focused rail row: the current tab row of the open workspace, else the open workspace's row; null when neither is shown.</summary>
    private Control? RailEdgeTarget()
    {
        if (atHome || !WorkspaceMounted || projectsTree is null) return null;
        if (workspaceTabHosts.GetValueOrDefault(workspaceRoot) is { IsEffectivelyVisible: true } host &&
            host.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "ActiveWorkspaceTab" && border.IsEffectivelyVisible)?.Parent is Control tab)
            return tab;
        return projectRailRows.Values.Select(entry => entry.Rows.GetValueOrDefault(workspaceRoot).Row).FirstOrDefault(row => row is { IsEffectivelyVisible: true });
    }

    private static string RailEdgeName(Control row) =>
        row.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "WorkspaceName" || text.Classes.Contains("dock-tab-title"))?.Text ?? "";

    /// <summary>Shows, points or hides the pill from the focused row's place in the viewport; it reads layout only.</summary>
    private void ShowRailEdgePill()
    {
        if (railEdgePill is not { } pill || railScroller is not { } scroller) return;
        if (railEdgeTarget is not { IsEffectivelyVisible: true } || TopLevel.GetTopLevel(railEdgeTarget) is null || !railEdgeTarget.GetVisualAncestors().Contains(scroller))
            railEdgeTarget = RailEdgeTarget();
        var place = railEdgeTarget is { } target && !surface.IsDragging ? RailEdgePlace(target, scroller) : 0;
        if (place == 0) { if (pill.IsVisible) pill.IsVisible = false; return; }
        var name = RailEdgeName(railEdgeTarget!);
        var above = place < 0;
        if (railEdgePillText!.Text != name || pill.Classes.Contains("above") != above)
        {
            railEdgePillText.Text = name;
            pill.Classes.Set("above", above);
            ToolTip.SetTip(pill, name);
            AutomationProperties.SetName(pill, (above ? "Scroll up to " : "Scroll down to ") + name);
        }
        var vertical = above ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        if (pill.VerticalAlignment != vertical) pill.VerticalAlignment = vertical;
        // Centred over the rows, which keep a 12px gutter on the right, and never wider than the rail.
        var room = Math.Max(0, scroller.Viewport.Width - 28);
        if (Math.Abs(pill.MaxWidth - room) > 0.5) pill.MaxWidth = room;
        if (!pill.IsVisible) pill.IsVisible = true;
    }

    /// <summary>-1 when the row is scrolled above the viewport, 1 when below, 0 when at least <see cref="RailEdgePillVisibleShare"/> of it shows.</summary>
    private static int RailEdgePlace(Control row, ScrollViewer scroller)
    {
        if (row.TranslatePoint(default, scroller) is not { } at || row.Bounds.Height <= 0) return 0;
        var shown = Math.Max(0, Math.Min(at.Y + row.Bounds.Height, scroller.Viewport.Height) - Math.Max(at.Y, 0));
        if (shown >= row.Bounds.Height * RailEdgePillVisibleShare) return 0;
        return at.Y + row.Bounds.Height / 2 < scroller.Viewport.Height / 2 ? -1 : 1;
    }

    /// <summary>Scrolls the rail just enough to show the focused row whole, as a reconcile moves the offset: at once.</summary>
    private void RevealRailEdgeTarget()
    {
        if (railScroller is not { } scroller || railEdgeTarget is not { } row || row.TranslatePoint(default, scroller) is not { } at) return;
        var offset = at.Y < 0 ? scroller.Offset.Y + at.Y - 4 : scroller.Offset.Y + at.Y + row.Bounds.Height - scroller.Viewport.Height + 4;
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(offset, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
        // The pill hides under the keyboard focus that chose it, so focus moves to the row it pointed to.
        row.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEffectivelyEnabled && (button.Name == "WorkspaceSelect" || button.Name?.StartsWith("Tab_", StringComparison.Ordinal) == true))?.Focus();
    }
}