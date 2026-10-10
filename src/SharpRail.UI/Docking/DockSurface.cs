using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;


namespace SharpRail.UI.Docking;

/// <summary>Where centre tabs render: <see cref="Vertical"/> as a column, in Projects when <see cref="Home"/> is <c>projects</c>.</summary>
public sealed record CenterTabsMode(bool Vertical, double Width, string Home, string DefaultPaneDirection);

internal enum StripStyle
{
    Horizontal,
    Vertical,
    Nested
}

public sealed partial class DockSurface : Grid
{
    public LayoutSession Session { get; }
    public event Action? GestureCanceled;
    public Func<DockTab, bool>? IsModified { get; set; }
    /// <summary>Whether a document tab's file was deleted on disk; refreshed with <see cref="RefreshModified"/>.</summary>
    public Func<DockTab, bool>? IsDeleted { get; set; }
    /// <summary>Replaces a tab's icon, given the brush it is drawn in; null keeps the tab kind's own glyph.</summary>
    public Func<DockTab, IBrush, Control?>? TabIcon { get; set; }
    /// <summary>Extra content after a tab's title, such as a badge.</summary>
    public Func<DockTab, Control?>? TabAdornment { get; set; }
    /// <summary>How centre tabs render: as the ordinary strip, a column beside the editor, or nested in Projects.</summary>
    public Func<CenterTabsMode>? CenterTabs { get; set; }
    /// <summary>The vertical column's width after a resize, in pixels.</summary>
    public Action<double>? CenterTabsWidthChanged { get; set; }
    /// <summary>Raised after a rebuild with the centre strips that belong in Projects, by centre group; empty otherwise.</summary>
    public event Action<IReadOnlyDictionary<string, Control>>? NestedStripsChanged;
    private readonly Dictionary<string, Control> nestedStrips = [];
    private readonly Dictionary<string, Action> activeTabUpdates = [];
    /// <summary>Extra strip actions after New terminal in a center group, given the group's id.</summary>
    public Func<string, IEnumerable<Control>>? CenterActions { get; set; }
    /// <summary>Recreates every center group's <see cref="CenterActions"/>, leaving tabs and the rest of the strip in place.</summary>
    public void RefreshCenterActions() { foreach (var update in centerActionUpdates) update(); }
    public void RefreshModified() { foreach (var update in modifiedUpdates) update(); }
    private readonly Func<DockTab?, Control> renderContent;
    private readonly List<(Control Control, string Group, bool Header)> sites = [];
    private readonly Grid shell = new();
    private readonly Border centerRegion = new() { Name = "CenterRegion" };
    private readonly Dictionary<string, Border> auxiliaryRegions = new[] { "left", "right", "bottom" }
        .ToDictionary(region => region, region => new Border { Name = "AuxiliaryRegion_" + region });
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    /// <summary>
    /// On a narrow screen the side regions are pages over the centre instead of columns beside it: a visible
    /// left side shows Projects alone, and a visible right side every other side tool as sections.
    /// </summary>
    public bool Drawers
    {
        get;
        set { if (field == value) return; field = value; Rebuild(); }
    }
    private bool refreshPending;
    private bool rebuildPending;
    private CenterTabsMode? renderedMode;
    private IReadOnlyList<DockToolInfo> renderedTools = [];
    // What the last rebuild drew, so a change that keeps the layout's structure, such as a workspace switch,
    // rebuilds only the groups whose tabs changed and leaves the rest of the chrome mounted.
    private string renderedStructure = "";
    private readonly Dictionary<string, BuiltGroup> builtGroups = [];

    private sealed record BuiltGroup(Control Control, string Signature, Border[] Hosts, (Control, string, bool)[] Sites,
        Action[] Modified, Action[] Catalog, Action[] CenterActions);
    private long previewGesture;
    internal void InvalidatePreviewKeep() => previewGesture++;
    public DockSurface(LayoutSession session, Func<DockTab?, Control> renderContent)
    {
        Name = "WorkspaceWorkbench"; Session = session; this.renderContent = renderContent;
        Children.Add(shell); Children.Add(overlay);
        Grid.SetColumn(centerRegion, 2); shell.Children.Add(centerRegion);
        foreach (var region in auxiliaryRegions.Values) shell.Children.Add(region);
        InstallPointerGestures();
        SizeChanged += (_, _) =>
        {
            if (Drawers) return;
            if (!shell.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.IsActive))
                PreviewSides(new SideGeometry(Session.State, Bounds.Width).Project());
        };
        Session.Changed += () => { CancelForLayoutChange(); if (!Retarget()) Rebuild(); };
        Session.ToolsChanged += RefreshTools;
        Session.Focused += () => { foreach (var update in activeTabUpdates.Values) update(); };
        Session.SelectionChanged += group =>
        {
            var resizing = CancelForLayoutChange();
            // Selecting another tool can hide Projects, which moves nested centre strips back to the column.
            if (!resizing && selectionUpdates.TryGetValue(group, out var update) && CenterTabs?.Invoke() == renderedMode) update();
            else Rebuild();
        };
        Rebuild();
    }

    private bool CancelForLayoutChange()
    {
        var active = dragging;
        var resizing = false;
        CancelDrag();
        foreach (var handle in shell.GetLogicalDescendants().OfType<ResizeHandle>())
            resizing |= handle.AbortGesture();
        if (active || resizing) GestureCanceled?.Invoke();
        return resizing;
    }

    private void RefreshTools()
    {
        var previous = renderedTools;
        renderedTools = Session.Tools;
        if (Session.State.Groups.SelectMany(group => Session.Tabs(group.Id)).Where(tab => tab.IsTool)
            .Any(tab => !Equals(previous.FirstOrDefault(tool => tool.Id == tab.Id), Session.Tool(tab.Id)))) Rebuild();
        else foreach (var update in catalogUpdates) update();
    }

    public void Rebuild()
    {
        if (draft is not null || shell.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.IsActive))
        { rebuildPending = true; return; }
        rebuildPending = false;
        renderedTools = Session.Tools;
        refreshPending = false;
        CancelDrag();
        foreach (var host in contentHosts) host.Child = null;
        contentHosts.Clear(); builtGroups.Clear(); tabSites.Clear(); groupHeaders.Clear(); selectionUpdates.Clear();
        activeTabUpdates.Clear();
        modifiedUpdates.Clear(); catalogUpdates.Clear(); centerActionUpdates.Clear(); sites.Clear(); nestedStrips.Clear(); stripLayouts.Clear();
        renderedMode = CenterTabs?.Invoke();
        foreach (var control in shell.Children.Where(control => control != centerRegion && !auxiliaryRegions.Values.Contains(control)).ToArray())
            shell.Children.Remove(control);
        centerRegion.Child = null;
        foreach (var region in auxiliaryRegions.Values)
        {
            region.Child = null; region.IsVisible = false;
            region.Width = double.NaN; region.HorizontalAlignment = HorizontalAlignment.Stretch; region.ZIndex = 0; region.Background = null;
            Grid.SetColumnSpan(region, 1);
        }
        shell.RowDefinitions.Clear(); shell.ColumnDefinitions.Clear();
        var state = Session.State;
        if (Drawers) { RebuildDrawers(state); return; }
        var widths = new SideGeometry(state, Bounds.Width).Project();
        var left = state.LeftVisible && state.Groups.Any(group => group.Region == "left");
        var right = state.RightVisible && state.Groups.Any(group => group.Region == "right");
        var bottom = state.BottomVisible && state.Groups.Any(group => group.Region == "bottom");
        shell.ColumnDefinitions.Add(new(left ? new GridLength(widths.Left, GridUnitType.Star) : new GridLength(28)));
        shell.ColumnDefinitions.Add(new(new GridLength(left ? 1 : 0)));
        shell.ColumnDefinitions.Add(new(new GridLength(Math.Max(double.Epsilon, widths.Center), GridUnitType.Star)));
        shell.ColumnDefinitions.Add(new(new GridLength(right ? 1 : 0)));
        shell.ColumnDefinitions.Add(new(right ? new GridLength(widths.Right, GridUnitType.Star) : new GridLength(28)));
        shell.RowDefinitions.Add(new(new GridLength(bottom ? 1 - state.BottomHeight : 1, GridUnitType.Star)));
        shell.RowDefinitions.Add(new(new GridLength(bottom ? 1 : 0)));
        shell.RowDefinitions.Add(new(new GridLength(bottom ? state.BottomHeight : 0, GridUnitType.Star)));
        var includeLeft = state.BottomAlignment is "full" or "center-left";
        var includeRight = state.BottomAlignment is "full" or "center-right";
        var start = includeLeft && left ? 0 : 2;
        var end = includeRight && right ? 4 : 2;
        centerRegion.Child = BuildCenter(state.Center);
        if (left) PlaceAuxiliary(BuildAuxiliary("left"), "left", 0, bottom && includeLeft ? 1 : 3);
        else PlaceRail("left", 0);
        if (right) PlaceAuxiliary(BuildAuxiliary("right"), "right", 4, bottom && includeRight ? 1 : 3);
        else PlaceRail("right", 4);
        if (left) OuterSeparator("left", 1, bottom && includeLeft ? 1 : 3);
        if (right) OuterSeparator("right", 3, bottom && includeRight ? 1 : 3);
        if (bottom)
        {
            var contents = BuildAuxiliary("bottom");
            var region = auxiliaryRegions["bottom"];
            region.Child = contents; region.IsVisible = true;
            Grid.SetRow(region, 2); Grid.SetColumn(region, start); Grid.SetColumnSpan(region, end - start + 1);
            var original = state.BottomHeight;
            double MinimumHeight() => Math.Min(.7, 147 / Math.Max(1, Bounds.Height));
            double Height(double delta)
            {
                var requested = Math.Round(original - delta / Math.Max(1, Bounds.Height), 12);
                var minimum = MinimumHeight();
                return requested < Math.Round(minimum / 2, 12) ? 0 : Math.Clamp(requested, minimum, .7);
            }
            var splitter = Separator(false,
                delta => PreviewBottom(Height(delta)),
                delta =>
                {
                    var height = Height(delta);
                    if (height == 0) Session.Visible("bottom", false);
                    else Session.Geometry(next => next.BottomHeight = height);
                },
                Rebuild);
            splitter.ConfigureRange(() => (Session.State.BottomHeight, MinimumHeight(), .7),
                value => Session.Geometry(next => next.BottomHeight = value));
            splitter.Name = "bottomSeparator";
            Ui.Place(shell, splitter, 1, start); Grid.SetColumnSpan(splitter, end - start + 1);
        }
        renderedStructure = Structure();
        NestedStripsChanged?.Invoke(nestedStrips);
    }

    private bool Side(string region) => (region == "left" ? Session.State.LeftVisible : Session.State.RightVisible) &&
        Session.State.Groups.Any(group => group.Region == region);

    // The centre and bottom keep the whole width; a visible side lies over them at the edge it belongs to.
    private void RebuildDrawers(DockState state)
    {
        var bottom = state.BottomVisible && state.Groups.Any(group => group.Region == "bottom");
        foreach (var width in new[] { 0d, 0, 1, 0, 0 }) shell.ColumnDefinitions.Add(new(new GridLength(width, width > 0 ? GridUnitType.Star : GridUnitType.Pixel)));
        shell.RowDefinitions.Add(new(new GridLength(bottom ? 1 - state.BottomHeight : 1, GridUnitType.Star)));
        shell.RowDefinitions.Add(new(new GridLength(bottom ? 1 : 0)));
        shell.RowDefinitions.Add(new(new GridLength(bottom ? state.BottomHeight : 0, GridUnitType.Star)));
        centerRegion.Child = BuildCenter(state.Center);
        foreach (var region in new[] { "left", "right" })
        {
            if (!Side(region)) continue;
            PlaceAuxiliary(region == "right" ? BuildSections() : BuildProjects(), region, 0, 3);
            var frame = auxiliaryRegions[region];
            Grid.SetColumnSpan(frame, 5);
            frame.ZIndex = 11; frame.Background = Ui.Sidebar;
        }
        if (bottom)
        {
            var region = auxiliaryRegions["bottom"];
            region.Child = BuildAuxiliary("bottom"); region.IsVisible = true;
            Grid.SetRow(region, 2); Grid.SetColumn(region, 2);
            // A resize handle draws above its neighbours, and would above a side page too; a plain rule does not.
            Ui.Place(shell, new Border { Name = "bottomRule", Background = Ui.BorderBrush }, 1, 2);
        }
        renderedStructure = Structure();
        NestedStripsChanged?.Invoke(nestedStrips);
    }

    private bool Retarget()
    {
        if (draft is not null || shell.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.IsActive)) return false;
        // A drawer's sections are drawn from every group of the region at once, so they are redrawn whole.
        if (Drawers && Side("right")) return false;
        if (!renderedTools.SequenceEqual(Session.Tools) || CenterTabs?.Invoke() != renderedMode || Structure() != renderedStructure) return false;
        foreach (var (id, built) in builtGroups.ToArray())
            if (GroupSignature(id) == built.Signature) selectionUpdates[id]();
            else RebuildGroup(Session.Group(id), built);
        NestedStripsChanged?.Invoke(nestedStrips);
        return true;
    }

    private string Structure()
    {
        var structure = Session.State.Copy();
        structure.Workspaces = []; structure.ActiveWorkspace = "";
        return JsonSerializer.Serialize(structure);
    }

    // Selection is left out: an unchanged group applies it through its selection update.
    private string GroupSignature(string id) =>
        JsonSerializer.Serialize(new { Tabs = Session.Tabs(id), Panes = Session.View.Panes.GetValueOrDefault(id) });

    private Control BuildTrackedGroup(DockGroup group)
    {
        activeTabUpdates.Remove(group.Id);
        int hosts = contentHosts.Count, placed = sites.Count, modified = modifiedUpdates.Count, catalog = catalogUpdates.Count, actions = centerActionUpdates.Count;
        var control = BuildGroup(group);
        builtGroups[group.Id] = new(control, GroupSignature(group.Id), [.. contentHosts.Skip(hosts)], [.. sites.Skip(placed)],
            [.. modifiedUpdates.Skip(modified)], [.. catalogUpdates.Skip(catalog)], [.. centerActionUpdates.Skip(actions)]);
        return control;
    }

    private void RebuildGroup(DockGroup group, BuiltGroup built)
    {
        foreach (var host in built.Hosts) { host.Child = null; contentHosts.Remove(host); }
        foreach (var site in built.Sites) sites.Remove(site);
        foreach (var update in built.Modified) modifiedUpdates.Remove(update);
        foreach (var update in built.Catalog) catalogUpdates.Remove(update);
        foreach (var update in built.CenterActions) centerActionUpdates.Remove(update);
        var old = built.Control;
        var parent = old.Parent;
        var control = BuildTrackedGroup(group);
        control.ClipToBounds = old.ClipToBounds;
        Grid.SetRow(control, Grid.GetRow(old)); Grid.SetColumn(control, Grid.GetColumn(old));
        Grid.SetRowSpan(control, Grid.GetRowSpan(old)); Grid.SetColumnSpan(control, Grid.GetColumnSpan(old));
        if (parent is Panel panel) panel.Children[panel.Children.IndexOf(old)] = control;
        else if (parent is Decorator decorator) decorator.Child = control;
    }

    private void PlaceSide(Control control, int column, int span)
    {
        Ui.Place(shell, control, 0, column); Grid.SetRowSpan(control, span);
    }

    private void PlaceAuxiliary(Control control, string region, int column, int span)
    {
        var frame = auxiliaryRegions[region];
        frame.Child = control; frame.IsVisible = true;
        Grid.SetRow(frame, 0); Grid.SetColumn(frame, column); Grid.SetRowSpan(frame, span);
    }

    private void PlaceRail(string region, int column)
    {
        var button = Ui.IconButton(region == "left" ? "layoutLeft" : "layoutRight", $"Show {region} side", () => Session.Visible(region, true));
        button.Name = region + "RestoreRail";
        button.Width = 24; button.Height = 24; button.Padding = new(0); button.CornerRadius = new(4);
        button.VerticalAlignment = VerticalAlignment.Top;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        ((Border)button.Content!).Width = 14; ((Border)button.Content!).Height = 14;
        button.IsEnabled = Session.State.Groups.Any(group => group.Region == region);
        var rail = new Border
        {
            Name = region + "HiddenSideRail",
            Background = Ui.Sidebar,
            Padding = new Thickness(0, 4),
            BorderBrush = Ui.BorderBrush,
            BorderThickness = region == "left" ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0),
            Child = button
        };
        ToolTip.SetTip(button, button.IsEnabled ? $"Show {region} side" : $"No {region} groups to show");
        PlaceAuxiliary(rail, region, column, 3);
        sites.Add((rail, "restore:" + region, false));
    }

    private void OuterSeparator(string region, int column, int span)
    {
        var direction = region == "left" ? 1 : -1;
        SideGeometry Geometry() => new(Session.State, Bounds.Width);
        double Width((double Left, double Center, double Right) geometry) => region == "left" ? geometry.Left : geometry.Right;
        double Extent() => Math.Max(1, Bounds.Width -
            (Session.State.LeftVisible && Session.State.Groups.Any(group => group.Region == "left") ? 0 : 28) -
            (Session.State.RightVisible && Session.State.Groups.Any(group => group.Region == "right") ? 0 : 28));
        var splitter = Separator(true,
            delta => PreviewSides(Geometry().Resize(region, delta / Extent())),
            delta => CommitSideWidth(region, Width(Geometry().Resize(region, delta / Extent()))), Rebuild);
        splitter.Name = region + "Separator";
        splitter.ConfigureRange(() =>
        {
            var value = Width(Geometry().Project());
            var opposite = region == "left" ? Session.State.RightWidth : Session.State.LeftWidth;
            var maximum = Math.Min(Math.Min(.7, 1 - opposite - 1e-6), Width(Geometry().Resize(region, direction)));
            return maximum < .08 ? (value, value, value) : (value, .08, maximum);
        }, value => CommitSideWidth(region, value));
        PlaceSide(splitter, column, span);
    }

    private void CommitSideWidth(string region, double width)
    {
        if (width <= double.Epsilon) { Session.Visible(region, false); return; }
        var opposite = region == "left" ? Session.State.RightWidth : Session.State.LeftWidth;
        var available = Math.Max(double.Epsilon, 1 - opposite);
        var maximum = Math.Max(double.Epsilon, Math.Min(.7, available - Math.Min(1e-6, available / 2)));
        Session.Geometry(state =>
        {
            var value = Math.Clamp(width, Math.Min(.08, maximum), maximum);
            if (region == "left") state.LeftWidth = value; else state.RightWidth = value;
        });
    }

    private void PreviewSides((double Left, double Center, double Right) widths)
    {
        if (shell.ColumnDefinitions.Count != 5 || Drawers) return;
        if (Session.State.LeftVisible && Session.State.Groups.Any(group => group.Region == "left"))
            shell.ColumnDefinitions[0].Width = new(widths.Left, GridUnitType.Star);
        shell.ColumnDefinitions[2].Width = new(Math.Max(double.Epsilon, widths.Center), GridUnitType.Star);
        if (Session.State.RightVisible && Session.State.Groups.Any(group => group.Region == "right"))
            shell.ColumnDefinitions[4].Width = new(widths.Right, GridUnitType.Star);
    }

    private void PreviewBottom(double ratio)
    {
        shell.RowDefinitions[0].Height = new(1 - ratio, GridUnitType.Star);
        shell.RowDefinitions[2].Height = new(ratio, GridUnitType.Star);
    }

    private Control BuildCenter(CenterNode node)
    {
        if (node.IsLeaf) return BuildTrackedGroup(Session.Group(node.GroupId));
        var horizontal = node.Axis == "horizontal";
        var grid = new Grid();
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new(new GridLength(node.Ratio, GridUnitType.Star)));
            grid.ColumnDefinitions.Add(new(new GridLength(1)));
            grid.ColumnDefinitions.Add(new(new GridLength(1 - node.Ratio, GridUnitType.Star)));
        }
        else
        {
            grid.RowDefinitions.Add(new(new GridLength(node.Ratio, GridUnitType.Star)));
            grid.RowDefinitions.Add(new(new GridLength(1)));
            grid.RowDefinitions.Add(new(new GridLength(1 - node.Ratio, GridUnitType.Star)));
        }
        var first = BuildCenter(node.First!); var second = BuildCenter(node.Second!);
        Ui.Place(grid, first); Ui.Place(grid, second, horizontal ? 0 : 2, horizontal ? 2 : 0);
        var signature = node.First!.Leaves().First();
        var ratio = node.Ratio;
        double Value(double delta)
        {
            var extent = horizontal ? grid.Bounds.Width : grid.Bounds.Height;
            var minimum = horizontal ? 320 : 180;
            return extent < minimum * 2 ? ratio : Math.Clamp(ratio + delta / extent, minimum / extent, 1 - minimum / extent);
        }
        void Preview(double value)
        {
            if (horizontal) { grid.ColumnDefinitions[0].Width = new(value, GridUnitType.Star); grid.ColumnDefinitions[2].Width = new(1 - value, GridUnitType.Star); }
            else { grid.RowDefinitions[0].Height = new(value, GridUnitType.Star); grid.RowDefinitions[2].Height = new(1 - value, GridUnitType.Star); }
        }
        var splitter = Separator(horizontal, delta => Preview(Value(delta)),
            delta => Session.Geometry(state => FindSplit(state.Center, signature, node.Axis)!.Ratio = Value(delta)), Rebuild);
        splitter.Name = "CenterSeparator_" + signature + "_" + node.Second!.Leaves().First();
        splitter.ConfigureRange(() =>
        {
            var extent = horizontal ? grid.Bounds.Width : grid.Bounds.Height;
            var minimum = horizontal ? 320 : 180;
            var current = FindSplit(Session.State.Center, signature, node.Axis)!.Ratio;
            return extent < minimum * 2 ? (current, current, current) : (current, minimum / extent, 1 - minimum / extent);
        }, value => Session.Geometry(state => FindSplit(state.Center, signature, node.Axis)!.Ratio = value));
        grid.SizeChanged += (_, _) => splitter.IsEnabled = (horizontal ? grid.Bounds.Width : grid.Bounds.Height) >= (horizontal ? 640 : 360);
        Ui.Place(grid, splitter, horizontal ? 0 : 1, horizontal ? 1 : 0);
        return grid;
    }

    private static CenterNode? FindSplit(CenterNode node, string first, string axis)
    {
        if (node.IsLeaf) return null;
        if (node.Axis == axis && node.First!.Leaves().First() == first) return node;
        return FindSplit(node.First!, first, axis) ?? FindSplit(node.Second!, first, axis);
    }

    public void RefreshContents()
    {
        if (draft is not null || shell.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.IsActive))
        { refreshPending = true; return; }
        refreshPending = false;
        RefreshContents(Array.Empty<string>());
    }

    private void FlushRefresh()
    {
        if (rebuildPending) Dispatcher.UIThread.Post(() =>
        {
            if (!rebuildPending) return;
            var top = TopLevel.GetTopLevel(this);
            var focused = top?.FocusManager?.GetFocusedElement() as Control;
            if (focused is not null && TopLevel.GetTopLevel(focused) != top) focused = null;
            Rebuild();
            if (focused is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                var target = TopLevel.GetTopLevel(focused) == top ? focused : focused.Name is { } name
                    ? this.GetLogicalDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name && Equals(control.Tag, focused.Tag)) : null;
                target?.Focus();
            }, DispatcherPriority.Loaded);
        });
        else if (refreshPending) Dispatcher.UIThread.Post(() => { if (refreshPending) RefreshContents(); });
    }

    private ResizeHandle Separator(bool horizontal, Action<double> preview, Action<double> commit, Action cancel)
    {
        var handle = new ResizeHandle(horizontal, preview, commit, cancel);
        handle.GestureEnded += FlushRefresh;
        return handle;
    }
}