using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;


namespace SharpRail.UI.Docking;

public sealed partial class DockSurface
{
    private readonly List<Border> contentHosts = [];
    private readonly Dictionary<string, List<(Control Control, string Tab)>> tabSites = [];
    private readonly Dictionary<string, Control> groupHeaders = [];
    private readonly Dictionary<string, Action> selectionUpdates = [];
    private readonly List<Action> modifiedUpdates = [];
    private readonly List<Action> catalogUpdates = [];
    private readonly List<Action> centerActionUpdates = [];
    // Per group, how its strip was drawn and the scroller holding its tabs, for drop geometry.
    private readonly Dictionary<string, (StripStyle Style, ScrollViewer Scroller)> stripLayouts = [];

    private Control BuildGroup(DockGroup group)
    {
        var panel = new Grid
        {
            Name = group.Region == "center" ? "CenterPanel" : "DockGroup",
            RowDefinitions = new RowDefinitions(group.Folded ? "27,*" : "32,*")
        };
        panel.AddHandler(PointerPressedEvent, (_, _) => Session.Focus(group.Id), RoutingStrategies.Bubble, handledEventsToo: true);
        panel.GotFocus += (_, _) => Session.Focus(group.Id);
        var selected = Session.Selected(group.Id);
        var updates = new List<Action>();
        var style = group.Region == "center" ? StripStyle.Nested : StripStyle.Horizontal;
        var headerFrame = group.Region is "left" or "right" && group.Tools.Count > 0 && selected is { IsTool: true }
            ? BuildToolHeader(group, panel, updates) : BuildStrip(group, panel, selected, updates, style);
        var header = groupHeaders[group.Id];
        PlaceStrip(group, panel, headerFrame, style);
        if (!group.Folded) sites.Add((header, group.Id, true));
        if (!group.Folded)
        {
            var body = new DockPanel
            {
                Name = "DockBody_" + group.Id,
                Background = group.Region == "center" ? Ui.Surface : Ui.Sidebar
            };
            body.Mount(() => selected is null ? Empty(group) : BodyFor(group, selected));
            void LabelBody()
            {
                var label = headerFrame.GetLogicalDescendants().OfType<DockTabButton>().FirstOrDefault(button => button.IsSelected);
                if (label is null) body.ClearValue(AutomationProperties.LabeledByProperty);
                else AutomationProperties.SetLabeledBy(body, label);
            }
            LabelBody();
            if (selected is null) body.ContextMenu = GroupMenu(group, panel);
            contentHosts.Add(body);
            updates.Add(() =>
            {
                var active = Session.Selected(group.Id);
                var current = Session.Group(group.Id);
                body.Mount(() =>
                {
                    if (active is null) return Empty(current);
                    if (Session.PaneFor(group.Id, active.Id) is null)
                    {
                        var content = renderContent(active);
                        return ReferenceEquals(body.Child, content) ? content : Adopt(content);
                    }
                    return TryRefreshPane(body, current, active) ? body.Child! : BodyFor(current, active);
                }, keep: true);
                LabelBody();
            });
            PlaceBody(panel, body, style);
            sites.Add((body, group.Id, false));
        }
        else sites.Add((panel, group.Id, false));
        return panel;
    }

    private Border BuildStrip(DockGroup group, Grid panel, DockTab? selected, List<Action> updates, StripStyle style)
    {
        var vertical = style != StripStyle.Horizontal;
        var nested = style == StripStyle.Nested;
        var header = new Grid
        {
            Name = "GroupHeader_" + group.Id,
            Background = nested ? Brushes.Transparent : Ui.Elevated,
            Focusable = true
        };
        if (vertical) header.RowDefinitions = new RowDefinitions(nested ? "Auto,Auto" : "*,Auto");
        else header.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        if (nested)
            header.GotFocus += (_, e) =>
            {
                Session.Focus(group.Id);
                e.Handled = true;
            };
        header.ContextMenu = GroupMenu(group, panel);
        AutomationProperties.SetName(header, GroupLabel(group) + " pane");
        var tabs = new DockTabStrip(Session, group.Id) { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = nested ? 2 : 0 };
        var scroll = new ScrollViewer
        {
            Name = "TabScroller_" + group.Id,
            Content = tabs,
            HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            // Nested in Projects the strip is sized by its rows and the tree scrolls instead.
            VerticalScrollBarVisibility = vertical && !nested ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };
        var subtitles = vertical ? AmbiguousSubtitles(Session.Tabs(group.Id)) : new Dictionary<string, string>();
        stripLayouts[group.Id] = (style, scroll);
        Ui.Place(header, scroll);
        tabSites[group.Id] = [];
        groupHeaders[group.Id] = header;
        selectionUpdates[group.Id] = () =>
        {
            foreach (var update in updates) update();
            AutomationProperties.SetName(header, GroupLabel(Session.Group(group.Id)) + " pane");
        };
        StackPanel? bubble = null;
        string? bubblePane = null;
        var activeUpdates = new List<Action>();
        activeTabUpdates[group.Id] = () => { foreach (var update in activeUpdates) update(); };
        foreach (var tab in Session.Tabs(group.Id))
        {
            var subtitle = subtitles.GetValueOrDefault(tab.Id);
            var pane = group.Region == "center" ? Session.PaneFor(group.Id, tab.Id) : null;
            var contents = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(nested && ExplicitProjectTabOpen ? "*,Auto,Auto" : "*,Auto"),
                Height = nested && ExplicitProjectTabOpen ? 48 : vertical ? subtitle is null ? 36 : 44 : group.Folded ? 26 : 31,
                Margin = new Thickness(0, 0, tab.IsTool ? 0 : 4, 0)
            };
            var label = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*,Auto,Auto") };
            var foreground = tab.Id == selected?.Id ? Ui.TextBrush : Ui.Muted;
            var icon = TabIcon?.Invoke(tab, foreground) ?? Ui.Icon(ToolIcon(tab), foreground, 14);
            Ui.Place(label, icon);
            if (TabAdornment?.Invoke(tab) is { } adornment)
            {
                adornment.Margin = new Thickness(4, 0, 0, 0);
                adornment.VerticalAlignment = VerticalAlignment.Center;
                Ui.Place(label, adornment, 0, 4);
            }
            var title = Ui.Text(Session.ToolTitle(tab), foreground, 14);
            title.LineHeight = 20;
            title.Classes.Add("dock-tab-title");
            if (tab.Preview)
            {
                title.FontStyle = FontStyle.Italic;
                title.Padding = new Thickness(0, 0, 3, 0);
            }
            if (subtitle is null) Ui.Place(label, title, 0, 2);
            else
            {
                // A basename shared by two open tabs gets a second, dimmed line naming its folder.
                var folder = Ui.Text(subtitle, Ui.Hint, 12);
                folder.Name = "TabSubtitle";
                folder.TextTrimming = TextTrimming.CharacterEllipsis;
                var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, folder } };
                Ui.Place(label, lines, 0, 2);
            }
            Button? close = null;
            Border? modifiedDot = null;
            if (!tab.IsTool)
            {
                var deleted = Ui.Text("deleted", Ui.Danger, 11);
                deleted.Name = "DeletedTab"; deleted.Margin = new Thickness(4, 0, 0, 0);
                deleted.VerticalAlignment = VerticalAlignment.Center;
                ToolTip.SetTip(deleted, "Deleted on disk");
                void ShowDeleted() => deleted.IsVisible = IsDeleted?.Invoke(tab) == true;
                ShowDeleted();
                modifiedUpdates.Add(ShowDeleted);
                Ui.Place(label, deleted, 0, 3);
                modifiedDot = new Border
                {
                    Name = "ModifiedTab",
                    Width = 8,
                    Height = 8,
                    Margin = new Thickness(6, 0, 0, 0),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                Ui.Place(contents, modifiedDot, 0, 1);
                close = Ui.IconButton("close", "Close", () => Session.Close(group.Id, tab.Id));
                close.Name = "CloseTab";
                close.Width = 18; close.Height = 18; close.MinWidth = close.MinHeight = 0; close.Padding = new Thickness(2); close.Margin = new Thickness(6, 0, 0, 0);
                ((Border)close.Content!).Width = ((Border)close.Content).Height = 14;
                close.Opacity = 0; close.IsTabStop = false;
                Ui.Place(contents, close, 0, 1);
            }
            var chrome = new Grid
            {
                Name = "DockTab_" + tab.Id.Replace(':', '_').Replace('/', '_'),
                MinWidth = vertical ? 0 : 96,
                MaxWidth = vertical ? double.PositiveInfinity : 192,
                Tag = pane is null ? null : "paned"
            };
            var inBubble = nested && pane is not null;
            var tabFrame = new Border
            {
                BorderBrush = nested ? Brushes.Transparent : Ui.BorderBrush,
                BorderThickness = nested ? new Thickness(inBubble ? 0 : 1) : vertical ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0),
                CornerRadius = nested && !inBubble ? new CornerRadius(6) : default,
                Child = contents,
                Background = nested ? Brushes.Transparent : tab.Id == selected?.Id ? Ui.Hover : Ui.Elevated
            };
            Ui.Place(chrome, tabFrame);
            // Nested rows mark the last-focused center tab separately from each pane's selection.
            var underline = new Border
            {
                Name = nested ? "ActiveWorkspaceTab" : null,
                Background = Ui.Accent,
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false,
                IsVisible = tab.Id == selected?.Id && (!nested || Session.View.FocusedCenter == group.Id)
            };
            if (vertical) { underline.Width = 2; underline.HorizontalAlignment = nested ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
            else { underline.Height = 2; underline.VerticalAlignment = VerticalAlignment.Bottom; }
            Ui.Place(chrome, underline);
            void UpdateActiveMarker() => underline.IsVisible = Session.Selected(group.Id)?.Id == tab.Id &&
                (!nested || Session.View.FocusedCenter == group.Id);
            activeUpdates.Add(UpdateActiveMarker);
            void SelectTab() { Session.Select(group.Id, tab.Id); FocusGroup(group.Id, focusContent: true); }
            var button = new DockTabButton(tabs, tab.Id, SelectTab)
            {
                Name = "Tab_" + tab.Id.Replace(':', '_').Replace('/', '_'),
                Content = label,
                Padding = new Thickness(8, group.Folded ? 2 : vertical ? 8 : 4, tab.IsTool ? 8 : 0, group.Folded ? 2 : vertical ? 8 : 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Height = vertical ? double.NaN : group.Folded ? 26 : 28,
                MinWidth = 0,
                MinHeight = 0,
                // The theme left-aligns buttons; the whole row selects, not just its icon and title.
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new(0),
                CornerRadius = new(0)
            };
            button.Classes.Add("dock-tab-button");
            Ui.Place(contents, button);
            if (nested && ExplicitProjectTabOpen)
            {
                // Leave the label to the Projects scroller; only an explicit action opens the tab.
                button.IsHitTestVisible = false;
                var open = Ui.IconButton("arrowRight", "Open " + Session.ToolTitle(tab), () =>
                {
                    SelectTab();
                    if (Drawers) Session.Visible("left", false);
                });
                open.Name = "OpenProjectTab";
                open.Tag = tab.Id;
                open.Classes.Add("project-tab-open");
                Ui.Place(contents, open, 0, 2);
            }
            // Pointer events can arrive after a rebuild removed this group; a stale row is simply inactive.
            bool IsActive() => Session.State.Groups.Any(item => item.Id == group.Id) && Session.Selected(group.Id)?.Id == tab.Id;
            void UpdateBackground()
            {
                if (!nested) { tabFrame.Background = IsActive() || chrome.IsPointerOver ? Ui.Hover : Ui.Elevated; return; }
                // Nested rows react to hover lightly; only the selected one draws a rounded, bordered box. A pane's
                // bubble does that for all its members together.
                if (inBubble) return;
                tabFrame.Background = IsActive() || NestedHoverEnabled && chrome.IsPointerOver ? Ui.Hover : Brushes.Transparent;
                tabFrame.BorderBrush = IsActive() ? Ui.BorderBrush : Brushes.Transparent;
            }
            UpdateBackground();
            chrome.PointerEntered += (_, _) => UpdateBackground();
            chrome.PointerExited += (_, _) => UpdateBackground();
            updates.Add(() =>
            {
                var active = Session.Selected(group.Id)?.Id == tab.Id;
                button.IsTabStop = active;
                title.Foreground = active ? Ui.TextBrush : Ui.Muted;
                if (icon is Border glyph) glyph.Background = title.Foreground;
                UpdateActiveMarker();
                UpdateBackground();
                if (active) chrome.BringIntoView();
            });
            if (close is not null)
            {
                // Like VS Code, an unsaved tab shows a dot that turns into the close button on hover.
                void ShowClose(bool shown)
                {
                    shown |= CloseAlwaysShown;
                    close.Opacity = shown ? 1 : 0;
                    modifiedDot!.IsVisible = !shown && IsModified?.Invoke(tab) == true;
                    modifiedDot.Background = title.Foreground;
                }
                if (CloseAlwaysShown) ShowClose(true);
                chrome.PointerEntered += (_, _) => ShowClose(true);
                chrome.PointerExited += (_, _) => ShowClose(button.IsKeyboardFocusWithin);
                button.GotFocus += (_, _) => ShowClose(true);
                button.LostFocus += (_, _) => ShowClose(chrome.IsPointerOver);
                updates.Add(() => ShowClose(close.Opacity == 1));
                modifiedUpdates.Add(() => ShowClose(close.Opacity == 1));
                ShowClose(false);
            }
            ToolTip.SetTip(button, new ToolTip { Content = tab.Path.Length > 0 ? tab.Path : Session.ToolTitle(tab) });
            ToolTip.SetPlacement(button, vertical ? PlacementMode.Right : PlacementMode.Bottom);
            ToolTip.SetVerticalOffset(button, 4);
            AutomationProperties.SetName(button, Session.ToolTitle(tab));
            button.IsTabStop = tab.Id == selected?.Id;
            button.Click += (_, _) =>
            {
                var keep = !tab.IsTool && Session.Selected(group.Id)?.Id == tab.Id &&
                    Session.Tabs(group.Id).Any(item => item.Id == tab.Id && item.Preview);
                SelectTab();
                if (!keep) return;
                var gesture = previewGesture; var epoch = Session.Epoch;
                DispatcherTimer.Run(() =>
                {
                    if (gesture == previewGesture && epoch == Session.Epoch) Session.Keep(group.Id, tab.Id);
                    return false;
                }, TimeSpan.FromMilliseconds(250));
            };
            button.AddHandler(PointerPressedEvent, (_, e) =>
            {
                var properties = e.GetCurrentPoint(button).Properties;
                if (properties.IsLeftButtonPressed && e.ClickCount == 2 && !tab.IsTool && tab.Kind != "terminal")
                { Session.Keep(group.Id, tab.Id); e.Handled = true; return; }
                if (properties.IsLeftButtonPressed)
                    ArmDrag(e, tab.Id, group.Id);
            }, RoutingStrategies.Tunnel);
            chrome.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.GetCurrentPoint(chrome).Properties.IsMiddleButtonPressed)
                { Session.Close(group.Id, tab.Id); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
            button.KeyDown += (_, e) =>
            {
                // A column's tabs answer Up and Down as well as Left and Right.
                var key = vertical ? e.Key switch { Key.Up => Key.Left, Key.Down => Key.Right, _ => e.Key } : e.Key;
                if (key is Key.Left or Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    var index = Session.Tabs(group.Id).ToList().FindIndex(item => item.Id == tab.Id);
                    Session.Move(tab.Id, group.Id, group.Id, index + (key == Key.Left ? -1 : 2));
                    FocusGroup(group.Id); e.Handled = true;
                }
                // Alt with an arrow is Back and Forward where Windows and Linux conventions apply; the window answers it.
                else if (!OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Alt) { }
                else if (key is Key.Left or Key.Right or Key.Home or Key.End)
                {
                    var members = Session.Tabs(group.Id);
                    var index = members.ToList().FindIndex(item => item.Id == tab.Id);
                    index = key switch
                    {
                        Key.Home => 0,
                        Key.End => members.Count - 1,
                        Key.Left => (index + members.Count - 1) % members.Count,
                        _ => (index + 1) % members.Count
                    };
                    Session.Select(group.Id, members[index].Id); FocusGroup(group.Id); e.Handled = true;
                }
                else if (e.Key is Key.Delete)
                { Session.Close(group.Id, tab.Id); FocusGroup(group.Id); e.Handled = true; }
            };
            button.ContextMenu = TabMenu(group, tab, panel);
            chrome.ContextMenu = button.ContextMenu;
            if (inBubble && bubblePane == pane!.Id) bubble!.Children.Add(chrome);
            else if (inBubble)
            {
                // A pane nested in Projects is one bubble around its members with a continuous left accent; it highlights
                // as a whole on hover and wears the selected box when any member is selected.
                bubble = new StackPanel { Children = { chrome } };
                bubblePane = pane!.Id;
                var memberIds = pane.TabIds.ToArray();
                // The accent is part of the box: clipped by its rounded corners, it curves with them like a CSS left border.
                var accent = new Border { Name = "TabPaneAccent", Width = 2, Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
                var frame = new Border
                {
                    Name = "TabPaneGroup",
                    Child = new Grid { Children = { bubble, accent } },
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Ui.BorderBrush,
                    Background = Brushes.Transparent,
                    ClipToBounds = true
                };
                void Paint()
                {
                    var active = Session.State.Groups.Any(item => item.Id == group.Id) && memberIds.Contains(Session.Selected(group.Id)?.Id);
                    frame.Background = active || NestedHoverEnabled && frame.IsPointerOver ? Ui.Hover : Brushes.Transparent;
                    frame.BorderBrush = active ? Ui.BorderBrush : Brushes.Transparent;
                    frame.Tag = active ? "active" : null;
                }
                frame.PointerEntered += (_, _) => Paint();
                frame.PointerExited += (_, _) => Paint();
                updates.Add(Paint);
                Paint();
                tabs.Children.Add(frame);
            }
            else { bubble = null; bubblePane = null; tabs.Children.Add(chrome); }
            tabSites[group.Id].Add((chrome, tab.Id));
            if (tab.Id == selected?.Id) Dispatcher.UIThread.Post(() => chrome.BringIntoView(), DispatcherPriority.Loaded);
        }
        // A column's start actions wrap in a row under its tabs instead of trailing the strip.
        Panel actions = vertical ? new WrapPanel { Name = "TabStripActions_" + group.Id, Orientation = Orientation.Horizontal, Margin = nested ? new Thickness(0, 4, 0, 0) : default }
            : new StackPanel { Orientation = Orientation.Horizontal };
        Border Fade(bool left) => new()
        {
            Name = (left ? "TabOverflowBefore_" : "TabOverflowAfter_") + group.Id,
            Width = 16,
            IsHitTestVisible = false,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Background = left ? Ui.FadeFromElevated : Ui.FadeToElevated
        };
        var leftFade = Fade(true); var rightFade = Fade(false);
        if (!vertical) { Ui.Place(header, leftFade); Ui.Place(header, rightFade); }
        var overflow = Ui.IconButton("search", "Search open tabs", () => ShowOverflow(group.Id));
        overflow.Content = Ui.Icon("search", size: 14);
        overflow.BorderThickness = new Thickness(1, 0, 0, 0);
        overflow.BorderBrush = Ui.BorderBrush;
        overflow.Name = "TabOverflow_" + group.Id;
        overflow.IsVisible = false;
        void Overflow()
        {
            // A column scrolls like any list; the search affordance belongs to a strip that hides tabs sideways.
            overflow.IsVisible = !vertical && scroll.Extent.Width > scroll.Viewport.Width + 1;
            if (!overflow.IsVisible) (overflow.Flyout as Flyout)?.Hide();
            leftFade.IsVisible = scroll.Offset.X > 1;
            rightFade.IsVisible = scroll.Offset.X + scroll.Viewport.Width < scroll.Extent.Width - 1;
        }
        scroll.ScrollChanged += (_, _) => Overflow();
        header.SizeChanged += (_, _) => Dispatcher.UIThread.Post(Overflow, DispatcherPriority.Loaded);
        scroll.PointerWheelChanged += (_, e) =>
        {
            if (!vertical && Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X))
            { scroll.Offset = new(scroll.Offset.X - e.Delta.Y * 45, 0); e.Handled = true; }
        };
        actions.Children.Add(overflow);
        if (!group.Folded)
        {
            var terminal = Ui.IconButton("terminal", "New terminal in this group", () => { Session.NewTerminal(group.Id); FocusGroup(group.Id, focusContent: true); });
            terminal.Name = "NewTerminal_" + group.Id;
            terminal.IsVisible = selected?.IsTool != true;
            updates.Add(() => terminal.IsVisible = Session.Selected(group.Id)?.IsTool != true);
            actions.Children.Add(terminal);
            if (group.Region == "center")
            {
                var extra = new StackPanel { Orientation = Orientation.Horizontal };
                actions.Children.Add(extra);
                void Fill()
                {
                    var controls = CenterActions?.Invoke(group.Id).ToArray() ?? [];
                    foreach (var old in extra.Children.Where(control => !controls.Contains(control)).ToArray()) extra.Children.Remove(old);
                    for (var index = 0; index < controls.Length; index++)
                    {
                        var current = extra.Children.IndexOf(controls[index]);
                        if (current < 0) extra.Children.Insert(index, Adopt(controls[index]));
                        else if (current != index) extra.Children.Move(current, index);
                    }
                }
                centerActionUpdates.Add(Fill);
                Fill();
            }
            if (group.Region is "left" or "right")
            {
                var menu = AddMenu(group);
                var add = Ui.IconButton("add", "Show a hidden tool in this group", () => { });
                add.Name = "AddToGroup_" + group.Id;
                add.ContextMenu = menu;
                add.Click += (_, _) => menu.Open(add);
                actions.Children.Add(add);
                void Available() => add.IsVisible = HiddenTools(group).Any();
                catalogUpdates.Add(Available);
                Available();
            }
        }
        // A side pane hides onto its rail.
        if (group.Region is "left" or "right") actions.Children.Add(HideButton("HidePane_" + group.Id, () => HidePane(group.Id)));
        // Like the reference, the strip offers Remove group only where it can act; the menu keeps the disabled reason.
        if (Session.Tabs(group.Id).Count == 0 && RemoveGroupMenuItem(group).IsEnabled)
        {
            var remove = Ui.IconButton("close", "Remove group", () => RemoveGroup(group.Id));
            remove.Name = "RemoveGroup_" + group.Id;
            actions.Children.Add(remove);
        }
        if (vertical) Ui.Place(header, actions, 1);
        else Ui.Place(header, actions, 0, 1);
        var headerFrame = Ui.Frame(header, nested ? Brushes.Transparent : Ui.Elevated);
        headerFrame.BorderThickness = nested ? default : new Thickness(0, 0, 0, 1);
        headerFrame.Name = "TabStrip_" + group.Id;
        return headerFrame;
    }
    // Only a basename shared by two open tabs earns a second line naming its folder; a folder on every row is noise.
    internal static Dictionary<string, string> AmbiguousSubtitles(IReadOnlyList<DockTab> tabs)
    {
        var subtitles = new Dictionary<string, string>();
        foreach (var sharing in tabs.GroupBy(tab => tab.Title).Where(group => group.Count() > 1))
            foreach (var tab in sharing.Where(tab => tab.Path.Length > 0))
                // Paths are workspace-relative, so a file at the workspace's root names that root as "./".
                subtitles[tab.Id] = Path.GetDirectoryName(tab.Path) is { Length: > 0 } folder ? ScopedSetting.AbbreviateHomePath(folder) : "./";
        return subtitles;
    }

    // Tool strips stay above their body; workspace strips live only in Projects.
    private void PlaceStrip(DockGroup group, Grid panel, Border strip, StripStyle style)
    {
        switch (style)
        {
            case StripStyle.Horizontal:
                Ui.Place(panel, strip);
                break;
            case StripStyle.Nested:
                panel.RowDefinitions = new RowDefinitions("*");
                nestedStrips[group.Id] = strip;
                break;
        }
    }

    private static void PlaceBody(Grid panel, Control body, StripStyle style)
    {
        if (style == StripStyle.Horizontal) Ui.Place(panel, body, 1);
        else Ui.Place(panel, body);
    }

    // Selecting a member shows its whole pane in the group's chosen orientation.
    private Control BodyFor(DockGroup group, DockTab selected)
    {
        if (group.Region != "center" || Session.PaneFor(group.Id, selected.Id) is not { } pane) return Adopt(renderContent(selected));
        var members = pane.TabIds.Select(id => Session.Tabs(group.Id).FirstOrDefault(tab => tab.Id == id)).OfType<DockTab>().ToArray();
        if (members.Length < DockPane.MinMembers) return Adopt(renderContent(selected));
        var columns = pane.Direction == "horizontal";
        var grid = new Grid { Name = "TabPane_" + group.Id, Tag = (pane.Id, pane.Direction) };
        var definitions = string.Join(",", members.Select((_, index) => $"{pane.Weights.ElementAtOrDefault(index) * 1000}*"));
        definitions = string.Join(",0,", definitions.Split(','));
        if (columns) grid.ColumnDefinitions = new ColumnDefinitions(definitions);
        else grid.RowDefinitions = new RowDefinitions(definitions);
        for (var index = 0; index < members.Length; index++)
        {
            var host = new Border { Name = "PaneMember", Tag = members[index].Id, ClipToBounds = true, Child = Adopt(renderContent(members[index])) };
            if (columns) Ui.Place(grid, host, 0, index * 2);
            else Ui.Place(grid, host, index * 2);
            if (index == 0) continue;
            var before = index - 1;
            double Extent() => columns ? grid.Bounds.Width : grid.Bounds.Height;
            DefinitionBase Definition(int member) => columns ? grid.ColumnDefinitions[member * 2] : grid.RowDefinitions[member * 2];
            double Actual(int member) => columns ? ((ColumnDefinition)Definition(member)).ActualWidth : ((RowDefinition)Definition(member)).ActualHeight;
            void Set(int member, double star)
            {
                if (columns) ((ColumnDefinition)Definition(member)).Width = new GridLength(star, GridUnitType.Star);
                else ((RowDefinition)Definition(member)).Height = new GridLength(star, GridUnitType.Star);
            }
            double[]? start = null;
            double[] Sizes() => [.. Enumerable.Range(0, members.Length).Select(Actual)];
            double[] Moved(double delta)
            {
                start ??= Sizes();
                var sizes = start.ToArray();
                var pair = sizes[before] + sizes[before + 1];
                var minimum = Math.Min(pair / 2, Extent() * .1);
                sizes[before] = Math.Clamp(start[before] + delta, minimum, pair - minimum);
                sizes[before + 1] = pair - sizes[before];
                return sizes;
            }
            void Show(double[] sizes) { for (var member = 0; member < sizes.Length; member++) Set(member, Math.Max(1, sizes[member])); }
            var handle = Separator(columns, delta => Show(Moved(delta)), delta =>
            {
                var sizes = Moved(delta); start = null; Show(sizes);
                var total = sizes.Sum();
                if (total > 0) Session.SetPaneWeights(group.Id, pane.Id, [.. sizes.Select(size => size / total)]);
            }, () => { if (start is not null) Show(start); start = null; });
            handle.Name = "PaneResize_" + group.Id;
            if (columns) Ui.Place(grid, handle, 0, index * 2 - 1);
            else Ui.Place(grid, handle, index * 2 - 1);
        }
        return grid;
    }

    // Document controls are cached per tab, so one may still sit in a pane member it was last shown in.
    private static Control Adopt(Control content)
    {
        switch (content.Parent)
        {
            case Border border: border.Child = null; break;
            case ContentControl host: host.Content = null; break;
            case Panel panel: panel.Children.Remove(content); break;
        }
        return content;
    }

    private Control Empty(DockGroup group)
    {
        if (group.Region == "center") return renderContent(null);
        var empty = Ui.Text("Empty group", Ui.Muted, 12);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;
        return empty;
    }

    private string ToolIcon(DockTab tab) => tab.Kind == "terminal" ? "terminal" : tab.Kind is "diff" or "changes" ? "fileDiff" :
        tab.IsTool ? Session.Tool(tab.Id)?.Icon ?? "puzzle" : "fileText";

    // Tools and terminals never share a pane, so a pane showing terminals offers no tools.
    private IEnumerable<DockToolInfo> HiddenTools(DockGroup group) => (Session.View.Documents.GetValueOrDefault(group.Id)?.Count ?? 0) > 0 ? [] :
        Session.Tools.Where(tool => tool.Region == group.Region && !Session.State.Groups.Any(item => item.Tools.Any(tab => tab.Id == tool.Id)));

    private ContextMenu AddMenu(DockGroup group)
    {
        var menu = new ContextMenu();
        void Fill()
        {
            menu.Items.Clear();
            foreach (var tool in HiddenTools(group))
            {
                var item = Ui.Menu("Show " + tool.Title, () => Session.RestoreTool(tool.Id, group.Id));
                item.Name = "ShowTool_" + tool.Id.Replace(':', '_');
                menu.Items.Add(item);
            }
            if (Session.State.Groups.FirstOrDefault(item => item.Id == group.Id) is { Tools.Count: > 0 } tools)
            {
                var below = Ui.Menu("New terminal pane below", () => { Session.NewTerminal(tools.Id); FocusGroup(Session.View.FocusedGroup, focusContent: true); }, CanCreate(tools));
                below.Name = "NewTerminalBelow_" + group.Id;
                menu.Items.Add(below);
            }
        }
        Fill();
        menu.Opening += (_, _) => Fill();
        return menu;
    }

    private ContextMenu TabMenu(DockGroup group, DockTab tab, Control geometry)
    {
        var menu = new ContextMenu();
        if (!tab.IsTool && tab.Kind != "terminal") menu.Items.Add(Ui.Menu("Keep open", () => Session.Keep(group.Id, tab.Id), tab.Preview));
        if (tab.Kind == "terminal" && RenameTerminal is not null)
        {
            var workspace = Session.State.ActiveWorkspace;
            var rename = Ui.Menu("Rename", () => RenameTerminal(workspace, tab));
            rename.Name = "TerminalRename";
            menu.Items.Add(rename);
        }
        menu.Items.Add(Ui.Menu("Close", () => Session.Close(group.Id, tab.Id)));
        menu.Items.Add(RemoveGroupMenuItem(group));
        var members = Session.Tabs(group.Id);
        var position = members.ToList().FindIndex(item => item.Id == tab.Id);
        var vertical = group.Region == "center";
        var pane = group.Region == "center" ? Session.PaneFor(group.Id, tab.Id) : null;
        if (pane is not null)
        {
            // Inside a pane the strip order and the split order are one order: the menu moves the member, and only a
            // placement outside the run takes a tab out.
            var member = pane.TabIds.IndexOf(tab.Id);
            var (back, forward) = pane.Direction == "vertical" ? ("up", "down") : ("left", "right");
            if (member > 0) menu.Items.Add(Ui.Menu($"Move {back} in this group", () => Session.ReorderPaneMember(group.Id, tab.Id, -1)));
            if (member < pane.TabIds.Count - 1) menu.Items.Add(Ui.Menu($"Move {forward} in this group", () => Session.ReorderPaneMember(group.Id, tab.Id, 1)));
        }
        else
        {
            var (back, forward) = vertical ? ("up", "down") : ("left", "right");
            menu.Items.Add(Ui.Menu($"Move {back}", () => Session.Move(tab.Id, group.Id, group.Id, position - 1), position > 0));
            menu.Items.Add(Ui.Menu($"Move {forward}", () => Session.Move(tab.Id, group.Id, group.Id, position + 2), position < members.Count - 1));
        }
        // Making a pane is a vertical-strip gesture; managing one works in either orientation.
        if (group.Region == "center" && !tab.IsTool && (vertical || pane is not null))
        {
            menu.Items.Add(new Separator());
            if (vertical)
            {
                // The neighbours rather than every tab: twenty "show beside" rows is a menu nobody reads.
                var defaultDirection = CenterTabs?.Invoke().DefaultPaneDirection ?? "horizontal";
                foreach (var neighbour in new[] { position - 1, position + 1 }.Where(index => index >= 0 && index < members.Count).Select(index => members[index]))
                {
                    if (neighbour.IsTool || pane?.TabIds.Contains(neighbour.Id) == true) continue;
                    var direction = Session.PaneFor(group.Id, neighbour.Id)?.Direction ?? defaultDirection;
                    var item = Ui.Menu($"{(direction == "vertical" ? "Show under" : "Show beside")} {Session.ToolTitle(neighbour)}",
                        () => Session.GroupTabs(group.Id, tab.Id, neighbour.Id, direction));
                    item.Name = "ShowWith_" + neighbour.Id.Replace(':', '_').Replace('/', '_');
                    menu.Items.Add(item);
                }
            }
            if (pane is not null)
            {
                menu.Items.Add(Ui.Menu(pane.Direction == "horizontal" ? "Stack this group" : "Put this group in columns",
                    () => Session.SetPaneDirection(group.Id, pane.Id, pane.Direction == "horizontal" ? "vertical" : "horizontal")));
                menu.Items.Add(Ui.Menu("Show on its own", () => Session.Ungroup(group.Id, tab.Id)));
            }
        }
        var move = new MenuItem { Header = "Move to pane" };
        foreach (var destination in Session.State.Groups.Where(item => (tab.Kind == "terminal" || (item.Region == "center") != tab.IsTool) && item.Id != group.Id))
        {
            var target = Ui.Menu(PaneName(destination), () => Session.Move(tab.Id, group.Id, destination.Id, Session.Tabs(destination.Id).Count));
            // Panes are named by their contents, and a pane that cannot take this tab is not offered.
            menu.Opening += (_, _) =>
            {
                var live = Session.State.Groups.FirstOrDefault(item => item.Id == destination.Id);
                target.IsVisible = live is not null && Session.CanMove(tab.Id, group.Id, live.Id, Session.Tabs(live.Id).Count);
                if (live is not null) target.Header = PaneName(live);
            };
            move.Items.Add(target);
        }
        menu.Items.Add(move);
        if (group.Region == "center")
        {
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                var split = Ui.Menu("Split " + (edge switch { "top" => "up", "bottom" => "down", _ => edge }), () => Session.Move(tab.Id, group.Id, group.Id, 0, edge));
                menu.Opening += (_, _) => split.IsEnabled = CanCreate(group) &&
                    (edge is "left" or "right" ? geometry.Bounds.Width >= 640 : geometry.Bounds.Height >= 360);
                menu.Items.Add(split);
            }
        }
        else
        {
            foreach (var edge in new[] { "before", "after" })
            {
                var direction = edge == "before" ? "above" : "below";
                var split = Ui.Menu("New group " + direction, () => Session.Move(tab.Id, group.Id, group.Id, 0, edge));
                menu.Opening += (_, _) => split.IsEnabled = Session.CanMove(tab.Id, group.Id, group.Id, 0, edge);
                menu.Items.Add(split);
            }
        }
        if (tab.IsTool || tab.Kind == "terminal")
            foreach (var region in new[] { "left", "right" })
                foreach (var atStart in new[] { true, false })
                {
                    var direction = atStart ? "top" : "bottom";
                    var title = $"New {region} group at {direction}";
                    var create = Ui.Menu(title, () => Session.MoveToNewRegionGroup(tab.Id, group.Id, region, atStart));
                    menu.Opening += (_, _) =>
                    {
                        var limit = Session.State.SideLimit;
                        create.IsEnabled = Session.State.Groups.Count(item => item.Region == region) < limit;
                        create.Header = title + (create.IsEnabled ? "" : $" — limited to {limit}");
                    };
                    menu.Items.Add(create);
                }
        return menu;
    }

    // "Move to right pane (Changes, Review)": at most three names, then +N; "(empty)" when it has none.
    private string PaneName(DockGroup group)
    {
        var names = Session.Tabs(group.Id).Select(Session.ToolTitle).ToArray();
        var contents = names.Length == 0 ? "empty" : string.Join(", ", names.Take(3)) + (names.Length > 3 ? $" +{names.Length - 3}" : "");
        return $"{group.Region} pane ({contents})";
    }

    private string GroupLabel(DockGroup group) => $"{group.Region}: {TitleOf(Session.Selected(group.Id)) ?? "Empty"}";
    private string? TitleOf(DockTab? tab) => tab is null ? null : Session.ToolTitle(tab);
    private bool CanCreate(DockGroup group) => Session.State.Groups.Count(item => item.Region == group.Region) <
        (group.Region == "center" ? 4 : Session.State.SideLimit);

    private ContextMenu GroupMenu(DockGroup group, Control geometry)
    {
        var menu = new ContextMenu();
        if (group.Region == "center")
        {
            var right = Ui.Menu("New split right", () => Session.NewGroup(group.Id, "right"));
            var below = Ui.Menu("New split below", () => Session.NewGroup(group.Id, "bottom"));
            menu.Opening += (_, _) =>
            {
                right.IsEnabled = CanCreate(group) && geometry.Bounds.Width >= 640;
                below.IsEnabled = CanCreate(group) && geometry.Bounds.Height >= 360;
            };
            menu.Items.Add(right); menu.Items.Add(below);
        }
        else
        {
            menu.Items.Add(Ui.Menu("New pane before", () => Session.NewGroup(group.Id, "before"), CanCreate(group)));
            menu.Items.Add(Ui.Menu("New pane after", () => Session.NewGroup(group.Id, "after"), CanCreate(group)));
            if (!group.Folded) menu.Items.Add(Ui.Menu("Hide pane", () => HidePane(group.Id)));
            else menu.Items.Add(Ui.Menu("Show pane", () => ActivateEntry(group.Id, null)));
            menu.Items.Add(Ui.Menu("Hide " + group.Region + " region", () => Session.Visible(group.Region, false)));
        }
        menu.Items.Add(RemoveGroupMenuItem(group));
        return menu;
    }

    private MenuItem RemoveGroupMenuItem(DockGroup group) => Ui.Menu("Remove group", () => RemoveGroup(group.Id),
        group.Region == "center" ? Session.State.Center.Leaves().Count() > 1 : Session.CanRemoveGroup(group.Id));

    private void RemoveGroup(string id)
    {
        var center = Session.State.Groups.Any(group => group.Id == id && group.Region == "center");
        if (Session.RemoveGroup(id)) FocusGroup(center ? Session.View.FocusedCenter : Session.View.FocusedGroup);
    }

    private void ToggleFold(string id)
    {
        Session.Fold(id);
        FocusGroup(id);
    }

    /// <summary>The window's close command. A tab closed while a tab button holds keyboard focus keeps focus on its group, as Delete does.</summary>
    internal void RequestClose()
    {
        var view = Session.View;
        var id = Session.State.Groups.Any(group => group.Id == view.FocusedGroup) ? view.FocusedGroup : view.FocusedCenter;
        var fromTab = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is DockTabButton;
        if (Session.RequestClose() && fromTab) FocusGroup(id);
    }

    private void FocusGroup(string id, bool focusContent = false) => Dispatcher.UIThread.Post(() =>
    {
        if (!Session.State.Groups.Any(group => group.Id == id)) return;
        if (Session.View.FocusedGroup != id) Session.Focus(id);
        // A folded pane, or one on a hidden side, is reached through its rail entry.
        var group = Session.Group(id);
        if (group.Region is "left" or "right" && (group.Folded || !Session.RegionVisible(group.Region)) && railEntries.TryGetValue(id, out var entry))
        { entry.Focus(); return; }
        if (group.Folded) { groupHeaders.GetValueOrDefault(id)?.Focus(); return; }
        var selected = Session.Selected(id);
        if (focusContent && selected?.Kind == "terminal" &&
            contentHosts.FirstOrDefault(body => body.Name == "DockBody_" + id)?.Child is Terminal.TerminalView terminal)
        {
            terminal.FocusTerminal();
            return;
        }
        if (selected is not null && tabSites.TryGetValue(id, out var tabs))
        {
            var target = tabs.FirstOrDefault(item => item.Tab == selected.Id).Control;
            target?.BringIntoView();
            // A tool pane's header has no tabs; it takes focus itself.
            if (target is null) groupHeaders.GetValueOrDefault(id)?.Focus();
            else target.GetLogicalDescendants().OfType<DockTabButton>().Single().Focus();
        }
        else groupHeaders.GetValueOrDefault(id)?.Focus();
    }, DispatcherPriority.Loaded);

    private void ShowOverflow(string id)
    {
        var trigger = this.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TabOverflow_" + id);
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = 6 };
        flyout.FlyoutPresenterClasses.Add("tab-search");
        var selected = false;
        flyout.Content = new TabSearch(() => Session.Tabs(id), ToolIcon, tab =>
        {
            selected = true;
            Session.Select(id, tab.Id); flyout.Hide(); FocusGroup(id, focusContent: true);
        }, flyout.Hide);
        flyout.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!selected && trigger.IsAttachedToVisualTree()) trigger.Focus();
        }, DispatcherPriority.Loaded);
        trigger.Flyout = flyout;
        flyout.ShowAt(trigger);
    }
}