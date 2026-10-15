using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace SharpRail.UI.Docking;

// The side regions' persistent edge rails. A rail mirrors its region's panes: one cluster per group in order, separated
// by a hairline, then the region's unplaced tools dimmed behind a last separator. A tool pane has an entry per tool, a
// terminal or empty pane one entry. Exactly one entry per expanded pane is pressed; choosing an unpressed entry reveals,
// unfolds and selects in one transition, and choosing the pressed one folds its pane. Project Home composes the same
// rail around its own Projects pane.
public sealed partial class DockSurface
{
    internal const double RailWidth = 40;
    private readonly Dictionary<string, Border> rails = [];
    // Per group, the rail entry that is its focus endpoint while it is folded or its side is hidden.
    private readonly Dictionary<string, Button> railEntries = [];
    private readonly List<RailSite> railSites = [];

    /// <summary>A rail drop site: an entry's half (insert into its pane) or a cluster boundary (a new pane there).</summary>
    private sealed record RailSite(Control Control, string Region, string Group = "", int Index = 0, int Boundary = -1, bool Half = false, bool After = false);

    /// <summary>Whether this window shows a project's home, which has its own Projects pane instead of the frame.</summary>
    internal bool Home => !Drawers && Session.State.ActiveWorkspace.StartsWith("home:", StringComparison.Ordinal);

    private double ShellWidth => Math.Max(0, Bounds.Width - (Drawers ? 0 : 2 * RailWidth));

    private string railSignature = "";

    // What the rails draw; an unchanged projection keeps their controls, so pointer and keyboard targets survive.
    private string RailSignature()
    {
        var state = Session.State;
        var groups = state.Groups.Where(group => group.Region is "left" or "right").Select(group =>
        {
            var tabs = Session.Tabs(group.Id);
            return $"{group.Id}|{group.Region}|{group.Folded}|{string.Join(",", group.Tools.Select(tool => tool.Id + "=" + Session.ToolTitle(tool)))}|" +
                $"{Session.Selected(group.Id)?.Id}|{string.Join(",", tabs.Where(tab => tab.Kind == "terminal").Select(tab => tab.Title))}|{tabs.Count}";
        });
        var tools = string.Join(",", Session.Tools.Select(tool => tool.Id + "=" + tool.Title + "=" + tool.Icon + "=" + tool.Region));
        return $"{Home}|{Drawers}|{state.LeftVisible}|{state.RightVisible}|{state.HomeProjectsVisible}|{SideGeometry.Shown(state, "left")}|" +
            $"{SideGeometry.Shown(state, "right")}|{tools}|{string.Join(";", groups)}";
    }

    private bool railsPending;

    private void RebuildRails(bool force = false)
    {
        // A pressed entry stays alive until its pointer is released, so a catalog refresh cannot swallow its click.
        if (rails.Values.Any(rail => rail.GetLogicalDescendants().OfType<Button>().Any(button => button.IsPressed)))
        {
            railsPending = true;
            return;
        }
        railsPending = false;
        var signature = RailSignature();
        if (!force && signature == railSignature && rails.Count > 0)
        {
            // The rails stay; only a hidden side's broad drop target is registered again for this projection.
            foreach (var (region, rail) in rails)
                if (!(Home && region == "left") && !SideGeometry.Shown(Session.State, region) && !sites.Any(site => ReferenceEquals(site.Control, rail)))
                    sites.Add((rail, "restore:" + region, false));
            return;
        }
        railSignature = signature;
        var top = TopLevel.GetTopLevel(this);
        var focused = top?.FocusManager?.GetFocusedElement() as Control;
        var refocus = focused is not null && rails.Values.Any(rail => rail.IsLogicalAncestorOf(focused)) ? focused.Name : null;
        foreach (var rail in rails.Values) Children.Remove(rail);
        rails.Clear(); railEntries.Clear(); railSites.Clear();
        sites.RemoveAll(site => site.Group.StartsWith("restore:", StringComparison.Ordinal) && site.Control.Name?.EndsWith("Rail", StringComparison.Ordinal) == true);
        var home = Home;
        ColumnDefinitions[0].Width = new GridLength(Drawers ? 0 : RailWidth);
        ColumnDefinitions[2].Width = new GridLength(Drawers ? 0 : RailWidth);
        if (Drawers) return;
        foreach (var region in new[] { "left", "right" })
        {
            var homeRail = home && region == "left";
            var rail = homeRail ? BuildHomeRail() : BuildRail(region);
            Grid.SetColumn(rail, region == "left" ? 0 : 2);
            Children.Insert(Children.IndexOf(overlay), rail);
            rails[region] = rail;
            // A hidden side keeps a broad target on its rail; a drop there reveals it.
            if (!homeRail && !SideGeometry.Shown(Session.State, region)) sites.Add((rail, "restore:" + region, false));
        }
        if (refocus is not null)
            Dispatcher.UIThread.Post(() => this.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == refocus)?.Focus(),
                DispatcherPriority.Loaded);
    }

    private Border RailFrame(string region, StackPanel entries) => new()
    {
        Name = region + "Rail",
        Width = RailWidth,
        Background = Ui.Sidebar,
        BorderBrush = Ui.BorderBrush,
        BorderThickness = region == "left" ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0),
        Child = entries
    };

    private Border BuildRail(string region)
    {
        var state = Session.State;
        var entries = new StackPanel { Name = region + "RailEntries", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2) };
        AutomationProperties.SetName(entries, region == "left" ? "Left tools" : "Right tools");
        var buttons = new List<Button>();
        var groups = state.Groups.Where(group => group.Region == region).ToArray();
        void Boundary(int index, bool line)
        {
            var hairline = new Border { Height = 1, Width = 24, Background = line ? Ui.BorderBrush : Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            var zone = new Border { Name = $"RailBoundary_{region}_{index}", Height = 9, Background = Brushes.Transparent, Child = hairline };
            entries.Children.Add(zone);
            railSites.Add(new(zone, region, Boundary: index));
        }
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            Boundary(index, index > 0);
            var cluster = new StackPanel { Name = "RailCluster_" + group.Id, Spacing = 2 };
            entries.Children.Add(cluster);
            if (group.Tools.Count > 0)
                for (var position = 0; position < group.Tools.Count; position++)
                    cluster.Children.Add(Entry(group, group.Tools[position], position, buttons));
            else cluster.Children.Add(Entry(group, null, 0, buttons));
        }
        Boundary(groups.Length, false);
        var unplaced = Session.Tools.Where(tool => tool.Region == region && !state.Groups.Any(group => group.Tools.Any(tab => tab.Id == tool.Id))).ToArray();
        if (unplaced.Length > 0)
        {
            entries.Children.Add(new Border { Height = 1, Width = 24, Margin = new Thickness(0, 4), Background = Ui.BorderBrush });
            foreach (var tool in unplaced)
            {
                // Dimmed: drawn in the hint role rather than the muted one of placed entries.
                var button = RailButton("RailUnplaced_" + tool.Id.Replace(':', '_'), Ui.Icon(tool.Icon, Ui.Hint, 16), tool.Title + " (not placed)", false, region);
                button.Click += (_, _) =>
                {
                    Session.RestoreTool(tool.Id);
                    if (Session.State.Groups.FirstOrDefault(group => group.Tools.Any(tab => tab.Id == tool.Id)) is { } placed) FocusGroup(placed.Id);
                };
                entries.Children.Add(Wrap(button, false, region));
                buttons.Add(button);
            }
        }
        Rove(buttons);
        return RailFrame(region, entries);
    }

    private Control Entry(DockGroup group, DockTab? tool, int position, List<Button> buttons)
    {
        var visible = Session.RegionVisible(group.Region);
        var selected = Session.Selected(group.Id);
        var pressed = visible && !group.Folded && (tool is null || selected?.Id == tool.Id);
        var terminals = Session.Tabs(group.Id).Where(tab => tab.Kind == "terminal").ToArray();
        var title = tool is not null ? Session.ToolTitle(tool) : terminals.Length == 0 ? "Empty pane" :
            terminals.Length == 1 ? terminals[0].Title : $"Terminals ({terminals.Length})";
        var name = tool is null ? "RailPane_" + group.Id : "Tab_" + tool.Id.Replace(':', '_').Replace('/', '_');
        var icon = tool is not null ? TabIcon?.Invoke(tool, pressed ? Ui.TextBrush : Ui.Muted) ?? Ui.Icon(ToolIcon(tool), pressed ? Ui.TextBrush : Ui.Muted, 16)
            : Ui.Icon(terminals.Length == 0 ? "layout" : "terminal", pressed ? Ui.TextBrush : Ui.Muted, 16);
        var button = RailButton(name, icon, title, pressed, group.Region);
        button.Click += (_, _) => ActivateEntry(group.Id, tool?.Id);
        if (tool is not null)
        {
            button.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) ArmDrag(e, tool.Id, group.Id);
            }, RoutingStrategies.Tunnel);
            button.ContextMenu = TabMenu(group, tool, button);
            // Delete removes the tool from its pane, as its menu's Close does; the entry stays navigation otherwise.
            button.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None) return;
                Session.Close(group.Id, tool.Id); e.Handled = true;
            };
            railSites.Add(new(button, group.Region, group.Id, position, Half: true));
            railSites.Add(new(button, group.Region, group.Id, position + 1, Half: true, After: true));
        }
        else
        {
            button.ContextMenu = GroupMenu(group, button);
            railSites.Add(new(button, group.Region, group.Id, Session.Tabs(group.Id).Count));
        }
        // A folded or hidden pane's focus endpoint is the entry of what it shows.
        if (tool is null || selected?.Id == tool.Id || !railEntries.ContainsKey(group.Id)) railEntries[group.Id] = button;
        buttons.Add(button);
        return Wrap(button, pressed, group.Region);
    }

    private static Button RailButton(string name, Control icon, string title, bool pressed, string region)
    {
        var button = new RailEntryButton(pressed)
        {
            Name = name,
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Content = icon,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Tag = pressed ? "pressed" : null
        };
        button.Classes.Add("rail-entry");
        // Like a tab, the frame paints selection; pressing an entry (to drag it) neither tints nor shrinks it.
        button.Classes.Add("dock-tab-button");
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, pressed ? "Shown; activate to fold" : "Activate to show");
        ToolTip.SetTip(button, title);
        ToolTip.SetPlacement(button, region == "right" ? PlacementMode.Left : PlacementMode.Right);
        return button;
    }

    // The selected fill and the accent sit on the entry's frame, the accent at the edge facing the window's border;
    // hover, focus and pressing belong to the button itself.
    private static Grid Wrap(Button button, bool pressed, string region)
    {
        var frame = new Grid { Width = RailWidth - 2 };
        frame.Children.Add(new Border
        {
            Name = "RailEntryFrame",
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            Background = pressed ? Ui.Hover : Brushes.Transparent,
            Child = button
        });
        frame.Children.Add(new Border
        {
            Name = "RailAccent",
            Width = 2,
            Height = 16,
            CornerRadius = new CornerRadius(1),
            Background = Ui.Accent,
            IsVisible = pressed,
            IsHitTestVisible = false,
            HorizontalAlignment = region == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        });
        return frame;
    }

    // A rail is one tab stop; arrows move between its entries and Enter or Space activates.
    private static void Rove(List<Button> buttons)
    {
        if (buttons.Count == 0) return;
        var stop = buttons.FirstOrDefault(button => Equals(button.Tag, "pressed")) ?? buttons[0];
        foreach (var button in buttons)
        {
            button.IsTabStop = button == stop;
            button.AddHandler(KeyDownEvent, (_, e) =>
            {
                var index = buttons.IndexOf(button);
                var next = e.Key switch
                {
                    Key.Up or Key.Left => (index + buttons.Count - 1) % buttons.Count,
                    Key.Down or Key.Right => (index + 1) % buttons.Count,
                    Key.Home => 0,
                    Key.End => buttons.Count - 1,
                    _ => -1
                };
                if (next < 0 || e.KeyModifiers != KeyModifiers.None) return;
                foreach (var other in buttons) other.IsTabStop = other == buttons[next];
                buttons[next].Focus(NavigationMethod.Directional);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
        }
    }

    // Revealing or unfolding a pane moves focus into it and folding leaves it on the entry; choosing another tool of a
    // pane already shown keeps focus on the rail, as a toolbar does.
    private void ActivateEntry(string groupId, string? tabId)
    {
        if (!Session.State.Groups.Any(group => group.Id == groupId)) return;
        var group = Session.Group(groupId);
        var shown = Session.RegionVisible(group.Region) && !group.Folded;
        if (Session.ActivateEntry(groupId, tabId) || !shown) FocusGroup(groupId);
    }

    /// <summary>Mod+B: shows or hides the left side, or Project Home's Projects pane, moving focus into what shows or onto its rail.</summary>
    public void ToggleLeft()
    {
        if (Home)
        {
            Session.HomeProjects(!Session.State.HomeProjectsVisible);
            FocusHomeProjects();
            return;
        }
        Session.Visible("left", !Session.State.LeftVisible);
        var view = Session.View;
        var group = Session.State.Groups.FirstOrDefault(item => item.Region == "left" && item.Id == view.FocusedAuxiliary.GetValueOrDefault("left"))
            ?? Session.State.Groups.FirstOrDefault(item => item.Region == "left" && !item.Folded)
            ?? Session.State.Groups.FirstOrDefault(item => item.Region == "left");
        if (group is not null) FocusGroup(group.Id);
    }

    private void FocusHomeProjects() => Dispatcher.UIThread.Post(() =>
    {
        if (!Home) return;
        var target = Session.State.HomeProjectsVisible
            ? this.GetLogicalDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "HomeProjectsHeader")
            : railEntries.GetValueOrDefault("home");
        target?.Focus();
    }, DispatcherPriority.Loaded);

    private Border BuildHomeRail()
    {
        var entries = new StackPanel { Name = "leftRailEntries", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12) };
        AutomationProperties.SetName(entries, "Left tools");
        var pressed = Session.State.HomeProjectsVisible;
        var tool = Session.Tool("projects");
        var button = RailButton("Tab_projects", Ui.Icon(tool?.Icon ?? "folderTab", pressed ? Ui.TextBrush : Ui.Muted, 16), tool?.Title ?? "Projects", pressed, "left");
        button.Click += (_, _) => { Session.HomeProjects(!Session.State.HomeProjectsVisible); FocusHomeProjects(); };
        entries.Children.Add(Wrap(button, pressed, "left"));
        railEntries["home"] = button;
        Rove([button]);
        return RailFrame("left", entries);
    }

    /// <summary>
    /// The frame as the side geometry sees it. At Project Home the left side is Home's own Projects pane, with its
    /// own visibility and width, and there is no bottom; the right side and centre are the frame's.
    /// </summary>
    private DockState GeometryState()
    {
        if (!Home) return Session.State;
        var state = Session.State.Copy();
        state.Workspaces = [];
        state.Groups.RemoveAll(group => group.Region is "left" or "bottom");
        if (state.HomeProjectsVisible) state.Groups.Add(new DockGroup { Region = "left" });
        state.LeftVisible = state.HomeProjectsVisible;
        state.LeftWidth = state.HomeProjectsWidth;
        return state;
    }

    // Project Home's Projects pane: the Projects tool under the plain pane header and its Hide.
    private Control BuildHomePane()
    {
        var pane = new Grid { Name = "HomeProjectsPane", RowDefinitions = new RowDefinitions("32,*") };
        var tool = DockState.Tool("projects");
        var header = PaneHeader("HomeProjectsHeader", Ui.Icon(Session.Tool("projects")?.Icon ?? "folderTab", Ui.TextBrush, 14),
            Session.Tool("projects")?.Title ?? "Projects", out _, out var actions);
        actions.Children.Add(HideButton("HidePane_home", () => { Session.HomeProjects(false); FocusHomeProjects(); }));
        Ui.Place(pane, header);
        var body = new DockPanel { Name = "DockBody_home", Background = Ui.Sidebar };
        body.Mount(() => Adopt(renderContent(tool)));
        contentHosts.Add(body);
        Ui.Place(pane, body, 1);
        return pane;
    }

    // The plain pane header a tool pane shows instead of a tab strip: its icon and title, then its actions.
    private Border PaneHeader(string name, Control icon, string title, out TextBlock label, out StackPanel actions)
    {
        var header = new Grid { Name = name, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Ui.Elevated, Focusable = true };
        label = Ui.Text(title, Ui.TextBrush, 14);
        label.Name = "PaneTitle";
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, Children = { icon, label } };
        caption.Name = "PaneCaption";
        Ui.Place(header, caption);
        actions = new StackPanel { Orientation = Orientation.Horizontal };
        Ui.Place(header, actions, 0, 1);
        AutomationProperties.SetName(header, title + " pane");
        var frame = Ui.Frame(header, Ui.Elevated);
        frame.BorderThickness = new Thickness(0, 0, 0, 1);
        frame.Name = "PaneHeader_" + name;
        return frame;
    }

    private static Button HideButton(string name, Action hide)
    {
        var button = Ui.IconButton("subtract", "Hide", hide);
        button.Name = name;
        AutomationProperties.SetName(button, "Hide");
        return button;
    }

    // A side tool pane: the selected tool's title is a drag handle for that tool, as its rail entry is.
    private Border BuildToolHeader(DockGroup group, Grid panel, List<Action> updates)
    {
        var selected = Session.Selected(group.Id)!;
        var icon = new ContentControl { Width = 14, Height = 14, Content = Ui.Icon(ToolIcon(selected), Ui.TextBrush, 14) };
        var frame = PaneHeader("GroupHeader_" + group.Id, icon, Session.ToolTitle(selected), out var label, out var actions);
        frame.Name = "TabStrip_" + group.Id;
        var header = (Grid)frame.Child!;
        header.ContextMenu = GroupMenu(group, panel);
        var caption = (Control)header.Children[0];
        caption.ContextMenu = TabMenu(group, selected, panel);
        caption.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(caption).Properties.IsLeftButtonPressed && Session.State.Groups.Any(item => item.Id == group.Id) &&
                Session.Selected(group.Id) is { IsTool: true } tool) ArmDrag(e, tool.Id, group.Id);
        }, RoutingStrategies.Tunnel);
        groupHeaders[group.Id] = header;
        tabSites[group.Id] = [];
        selectionUpdates[group.Id] = () => { foreach (var update in updates) update(); };
        updates.Add(() =>
        {
            if (!Session.State.Groups.Any(item => item.Id == group.Id) || Session.Selected(group.Id) is not { } current) return;
            label.Text = Session.ToolTitle(current);
            icon.Content = Ui.Icon(ToolIcon(current), Ui.TextBrush, 14);
            caption.ContextMenu = TabMenu(Session.Group(group.Id), current, panel);
            AutomationProperties.SetName(header, Session.ToolTitle(current) + " pane");
        });
        var menu = AddMenu(group);
        var add = Ui.IconButton("add", "Add to this pane", () => { });
        add.Name = "AddToGroup_" + group.Id;
        add.ContextMenu = menu;
        add.Click += (_, _) => menu.Open(add);
        actions.Children.Add(add);
        actions.Children.Add(HideButton("HidePane_" + group.Id, () => HidePane(group.Id)));
        return frame;
    }

    private void HidePane(string id)
    {
        Session.HidePane(id);
        FocusGroup(id);
    }
}

/// <summary>A rail entry: a pressed (toggle) button in its rail's toolbar; pressed while its pane shows it.</summary>
internal sealed class RailEntryButton(bool pressed) : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    internal bool IsPressedEntry => pressed;
    protected override AutomationPeer OnCreateAutomationPeer() => new EntryPeer(this);

    private sealed class EntryPeer(RailEntryButton owner) : ButtonAutomationPeer(owner), IToggleProvider
    {
        public ToggleState ToggleState => owner.IsPressedEntry ? ToggleState.On : ToggleState.Off;
        public void Toggle() => owner.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(ClickEvent));
    }
}