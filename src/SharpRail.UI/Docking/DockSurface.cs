using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;


namespace SharpRail.UI.Docking;

/// <summary>The default arrangement when tabs in Projects are shown together.</summary>
public sealed record CenterTabsMode(string DefaultPaneDirection);

internal enum StripStyle
{
    Horizontal,
    Nested
}

public sealed partial class DockSurface : Grid
{
    public LayoutSession Session { get; }
    public event Action? GestureCanceled;
    /// <summary>Requests a shared title change for a terminal tab.</summary>
    public Action<string, DockTab>? RenameTerminal { get; set; }
    /// <summary>The active workspace's root on the host, which the tab menu's path copies resolve against.</summary>
    public Func<string>? WorkspaceRoot { get; set; }
    public Func<DockTab, bool>? IsModified { get; set; }
    /// <summary>Whether a document tab's file was deleted on disk; refreshed with <see cref="RefreshModified"/>.</summary>
    public Func<DockTab, bool>? IsDeleted { get; set; }
    /// <summary>Replaces a tab's icon, given the brush it is drawn in; null keeps the tab kind's own glyph.</summary>
    public Func<DockTab, IBrush, Control?>? TabIcon { get; set; }
    /// <summary>Extra content after a tab's title, such as a badge.</summary>
    public Func<DockTab, Control?>? TabAdornment { get; set; }
    /// <summary>The default arrangement for tabs shown together in Projects.</summary>
    public Func<CenterTabsMode>? CenterTabs { get; set; }
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
    private readonly Dictionary<string, Border> auxiliaryRegions = new[] { "left", "right" }
        .ToDictionary(region => region, region => new Border { Name = "AuxiliaryRegion_" + region });
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    /// <summary>A touch screen has no hover to reveal a tab's close button on, so it is always drawn.</summary>
    public bool CloseAlwaysShown { get; set; }
    internal bool ExplicitProjectTabOpen { get; set; }
    internal bool NestedHoverEnabled { get; set; } = true;

    /// <summary>
    /// On a narrow screen the side regions are pages over the centre instead of columns beside it: a visible
    /// left side shows Projects alone, and a visible right side every other side tool as sections.
    /// </summary>
    public bool Drawers
    {
        get;
        set { if (field == value) return; field = value; if (!value) LeavePages(); Rebuild(); }
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
    public DockSurface(LayoutSession session, Func<DockTab?, Control> renderContent, bool build = true)
    {
        Name = "WorkspaceWorkbench"; Session = session; this.renderContent = renderContent;
        ColumnDefinitions = new ColumnDefinitions($"{RailWidth},*,{RailWidth}");
        Grid.SetColumn(shell, 1); Grid.SetColumnSpan(overlay, 3);
        Children.Add(shell); Children.Add(overlay);
        Grid.SetColumn(centerRegion, 2); shell.Children.Add(centerRegion);
        foreach (var region in auxiliaryRegions.Values) shell.Children.Add(region);
        InstallPointerGestures();
        SizeChanged += (_, _) =>
        {
            if (Drawers) return;
            if (!shell.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.IsActive))
                PreviewSides(new SideGeometry(GeometryState(), ShellWidth).Project());
        };
        Session.Changed += () => { CancelForLayoutChange(); if (!Retarget()) Rebuild(); };
        Session.ToolsChanged += RefreshTools;
        Session.Focused += () => { foreach (var update in activeTabUpdates.Values) update(); };
        Session.SelectionChanged += group =>
        {
            var resizing = CancelForLayoutChange();
            if (!resizing && selectionUpdates.TryGetValue(group, out var update) && CenterTabs?.Invoke() == renderedMode) { update(); RebuildRails(); }
            // A phone's side pages draw no group of their own: only Tools' sections show what a side group selects.
            else if (Drawers && !resizing && Session.Group(group).Region != "center" && CenterTabs?.Invoke() == renderedMode) { RefreshSections(); RebuildRails(); }
            else Rebuild();
        };
        if (build) Rebuild();
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
        renderedTools = Session.Tools;
        if (!Retarget()) Rebuild();
        else
        {
            foreach (var update in catalogUpdates) update();
            RebuildRails(force: true);
        }
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
        if (Drawers) { RebuildDrawers(state); RebuildRails(); return; }
        var geometry = GeometryState();
        var widths = new SideGeometry(geometry, ShellWidth).Project();
        var left = SideGeometry.Shown(geometry, "left");
        var right = SideGeometry.Shown(geometry, "right");
        shell.ColumnDefinitions.Add(new(left ? new GridLength(widths.Left, GridUnitType.Star) : new GridLength(0)));
        shell.ColumnDefinitions.Add(new(new GridLength(left ? 1 : 0)));
        shell.ColumnDefinitions.Add(new(new GridLength(Math.Max(double.Epsilon, widths.Center), GridUnitType.Star)));
        shell.ColumnDefinitions.Add(new(new GridLength(right ? 1 : 0)));
        shell.ColumnDefinitions.Add(new(right ? new GridLength(widths.Right, GridUnitType.Star) : new GridLength(0)));
        shell.RowDefinitions.Add(new(new GridLength(1, GridUnitType.Star)));
        centerRegion.Child = BuildCenter(state.Center);
        if (left) PlaceAuxiliary(Home ? BuildHomePane() : BuildAuxiliary("left"), "left", 0, 1);
        if (right) PlaceAuxiliary(BuildAuxiliary("right"), "right", 4, 1);
        if (left) OuterSeparator("left", 1, 1);
        if (right) OuterSeparator("right", 3, 1);
        RebuildRails();
        renderedStructure = Structure();
        NestedStripsChanged?.Invoke(nestedStrips);
    }

    private bool Side(string region) => (region == "left" ? Session.State.LeftVisible : Session.State.RightVisible) &&
        Session.State.Groups.Any(group => group.Region == region);

    // On compact screens each side is a page over the whole workbench; only the pages on screen are built.
    private void RebuildDrawers(DockState state)
    {
        foreach (var width in new[] { 0d, 0, 1, 0, 0 }) shell.ColumnDefinitions.Add(new(new GridLength(width, width > 0 ? GridUnitType.Star : GridUnitType.Pixel)));
        shell.RowDefinitions.Add(new(new GridLength(1, GridUnitType.Star)));
        centerRegion.Child = BuildCenter(state.Center);
        pageBodies.Clear();
        foreach (var frame in auxiliaryRegions.Values)
        {
            Grid.SetRow(frame, 0); Grid.SetColumn(frame, 0); Grid.SetRowSpan(frame, 1); Grid.SetColumnSpan(frame, 5);
            frame.ZIndex = 11; frame.Background = Ui.Sidebar;
        }
        renderedStructure = Structure();
        SwitchPage();
        NestedStripsChanged?.Invoke(nestedStrips);
    }

    private bool Retarget()
    {
        if (draft is not null || shell.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.IsActive)) return false;
        if (!renderedTools.SequenceEqual(Session.Tools) || CenterTabs?.Invoke() != renderedMode || Structure() != renderedStructure) return false;
        foreach (var (id, built) in builtGroups.ToArray())
            if (GroupSignature(id) == built.Signature) selectionUpdates[id]();
            else RebuildGroup(Session.Group(id), built);
        if (Drawers) { RefreshSections(); SwitchPage(); }
        RebuildRails();
        NestedStripsChanged?.Invoke(nestedStrips);
        return true;
    }

    private string Structure()
    {
        var structure = Session.State.Copy();
        structure.Workspaces = []; structure.ActiveWorkspace = "";
        // Per-project pane bookkeeping: what it changes on screen is already in the groups and visibility above.
        structure.ActiveProject = ""; structure.DefaultProjectPanes = null; structure.ProjectPanes = [];
        // A phone shows one side at a time as a page, which moves without redrawing the others.
        if (Drawers) { structure.LeftVisible = false; structure.RightVisible = false; }
        return (Home ? "home:" : "") + JsonSerializer.Serialize(structure);
    }

    // Selection is left out: an unchanged group applies it through its selection update.
    private string GroupSignature(string id)
    {
        var tabs = Session.Tabs(id);
        return JsonSerializer.Serialize(new { Tabs = tabs, Tools = tabs.Where(tab => tab.IsTool).Select(tab => Session.Tool(tab.Id)), Panes = Session.View.Panes.GetValueOrDefault(id) });
    }

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

    private void OuterSeparator(string region, int column, int span)
    {
        var direction = region == "left" ? 1 : -1;
        SideGeometry Geometry() => new(GeometryState(), ShellWidth);
        double Width((double Left, double Center, double Right) geometry) => region == "left" ? geometry.Left : geometry.Right;
        double Extent() => Math.Max(1, ShellWidth);
        var splitter = Separator(true,
            delta => PreviewSides(Geometry().Resize(region, delta / Extent())),
            delta => CommitSideWidth(region, Width(Geometry().Resize(region, delta / Extent()))), Rebuild);
        splitter.Name = region + "Separator";
        splitter.ConfigureRange(() =>
        {
            var value = Width(Geometry().Project());
            var opposite = region == "left" ? GeometryState().RightWidth : GeometryState().LeftWidth;
            var maximum = Math.Min(Math.Min(.7, 1 - opposite - 1e-6), Width(Geometry().Resize(region, direction)));
            return maximum < .08 ? (value, value, value) : (value, .08, maximum);
        }, value => CommitSideWidth(region, value));
        PlaceSide(splitter, column, span);
    }

    private void CommitSideWidth(string region, double width)
    {
        // At Project Home the left side is its own Projects pane, whose width and visibility are its own.
        var home = Home && region == "left";
        if (width <= double.Epsilon) { if (home) Session.HomeProjects(false); else Session.Visible(region, false); return; }
        var opposite = region == "left" ? GeometryState().RightWidth : GeometryState().LeftWidth;
        var available = Math.Max(double.Epsilon, 1 - opposite);
        var maximum = Math.Max(double.Epsilon, Math.Min(.7, available - Math.Min(1e-6, available / 2)));
        Session.Geometry(state =>
        {
            var value = Math.Clamp(width, Math.Min(.08, maximum), maximum);
            if (home) state.HomeProjectsWidth = value;
            else if (region == "left") state.LeftWidth = value;
            else state.RightWidth = value;
        });
    }

    private void PreviewSides((double Left, double Center, double Right) widths)
    {
        if (shell.ColumnDefinitions.Count != 5 || Drawers) return;
        if (SideGeometry.Shown(GeometryState(), "left"))
            shell.ColumnDefinitions[0].Width = new(widths.Left, GridUnitType.Star);
        shell.ColumnDefinitions[2].Width = new(Math.Max(double.Epsilon, widths.Center), GridUnitType.Star);
        if (SideGeometry.Shown(GeometryState(), "right"))
            shell.ColumnDefinitions[4].Width = new(widths.Right, GridUnitType.Star);
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
        if (railsPending) Dispatcher.UIThread.Post(() => { if (railsPending) RebuildRails(force: true); });
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