using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Docking;

public sealed partial class DockSurface
{
    private readonly List<Border> contentHosts = [];
    private readonly Dictionary<string, List<(Control Control, string Tab)>> tabSites = [];
    private readonly Dictionary<string, Control> groupHeaders = [];
    private readonly Dictionary<string, Action> selectionUpdates = [];
    private readonly List<Action> modifiedUpdates = [];

    private Control BuildGroup(DockGroup group)
    {
        var panel = new Grid
        {
            Name = group.Region == "center" ? "CenterPanel" : "DockGroup",
            RowDefinitions = new RowDefinitions(group.Folded ? "27,*" : "32,*")
        };
        panel.AddHandler(PointerPressedEvent, (_, _) => Session.Focus(group.Id), RoutingStrategies.Bubble, handledEventsToo: true);
        panel.GotFocus += (_, _) => Session.Focus(group.Id);
        var header = new Grid { Name = "GroupHeader_" + group.Id, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Ui.Elevated, Focusable = true };
        header.ContextMenu = GroupMenu(group, panel);
        AutomationProperties.SetName(header, GroupLabel(group) + " pane");
        var tabs = new DockTabStrip(Session, group.Id) { Orientation = Orientation.Horizontal };
        var scroll = new ScrollViewer
        {
            Content = tabs,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Ui.Place(header, scroll);
        tabSites[group.Id] = [];
        groupHeaders[group.Id] = header;
        var selected = Session.Selected(group.Id);
        var updates = new List<Action>();
        selectionUpdates[group.Id] = () =>
        {
            foreach (var update in updates) update();
            AutomationProperties.SetName(header, GroupLabel(Session.Group(group.Id)) + " pane");
        };
        foreach (var tab in Session.Tabs(group.Id))
        {
            var contents = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Height = group.Folded ? 26 : 31,
                Margin = new Thickness(0, 0, tab.IsTool ? 0 : 4, 0)
            };
            var label = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*") };
            var foreground = tab.Id == selected?.Id ? Ui.TextBrush : Ui.Muted;
            var icon = (Border)Ui.Icon(ToolIcon(tab), foreground, 14);
            Ui.Place(label, icon);
            var title = Ui.Text(tab.Title, foreground, 14);
            title.LineHeight = 20;
            title.Classes.Add("dock-tab-title");
            if (tab.Preview)
            {
                title.FontStyle = FontStyle.Italic;
                title.Padding = new Thickness(0, 0, 3, 0);
            }
            Ui.Place(label, title, 0, 2);
            Button? close = null;
            Border? modifiedDot = null;
            if (!tab.IsTool)
            {
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
            var chrome = new Grid { Name = "DockTab_" + tab.Id.Replace(':', '_').Replace('/', '_'), MinWidth = 96, MaxWidth = 192 };
            var tabFrame = new Border
            {
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = contents,
                Background = tab.Id == selected?.Id ? Ui.Hover : Ui.Elevated
            };
            Ui.Place(chrome, tabFrame);
            var underline = new Border
            {
                Background = Ui.Accent,
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false,
                IsVisible = tab.Id == selected?.Id
            };
            Ui.Place(chrome, underline);
            void SelectTab() { Session.Select(group.Id, tab.Id); FocusGroup(group.Id, focusContent: true); }
            var button = new DockTabButton(tabs, tab.Id, SelectTab)
            {
                Name = "Tab_" + tab.Id.Replace(':', '_').Replace('/', '_'),
                Content = label,
                Padding = new Thickness(8, group.Folded ? 2 : 4, tab.IsTool ? 8 : 0, group.Folded ? 2 : 4),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Height = group.Folded ? 26 : 28,
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new(0),
                CornerRadius = new(0)
            };
            button.Classes.Add("dock-tab-button");
            Ui.Place(contents, button);
            void UpdateBackground() => tabFrame.Background =
                underline.IsVisible || chrome.IsPointerOver ? Ui.Hover : Ui.Elevated;
            chrome.PointerEntered += (_, _) => UpdateBackground();
            chrome.PointerExited += (_, _) => UpdateBackground();
            updates.Add(() =>
            {
                var active = Session.Selected(group.Id)?.Id == tab.Id;
                button.IsTabStop = active;
                title.Foreground = icon.Background = active ? Ui.TextBrush : Ui.Muted;
                underline.IsVisible = active;
                UpdateBackground();
                if (active) chrome.BringIntoView();
            });
            if (close is not null)
            {
                // Like VS Code, an unsaved tab shows a dot that turns into the close button on hover.
                void ShowClose(bool shown)
                {
                    close.Opacity = shown ? 1 : 0;
                    modifiedDot!.IsVisible = !shown && IsModified?.Invoke(tab) == true;
                    modifiedDot.Background = title.Foreground;
                }
                chrome.PointerEntered += (_, _) => ShowClose(true);
                chrome.PointerExited += (_, _) => ShowClose(button.IsKeyboardFocusWithin);
                button.GotFocus += (_, _) => ShowClose(true);
                button.LostFocus += (_, _) => ShowClose(chrome.IsPointerOver);
                updates.Add(() => ShowClose(close.Opacity == 1));
                modifiedUpdates.Add(() => ShowClose(close.Opacity == 1));
                ShowClose(false);
            }
            ToolTip.SetTip(button, new ToolTip { Content = tab.Path.Length > 0 ? tab.Path : tab.Title });
            ToolTip.SetPlacement(button, PlacementMode.Bottom);
            ToolTip.SetVerticalOffset(button, 4);
            AutomationProperties.SetName(button, tab.Title);
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
                if (e.Key is Key.Left or Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    var index = Session.Tabs(group.Id).ToList().FindIndex(item => item.Id == tab.Id);
                    Session.Move(tab.Id, group.Id, group.Id, index + (e.Key == Key.Left ? -1 : 2));
                    FocusGroup(group.Id); e.Handled = true;
                }
                else if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
                {
                    var members = Session.Tabs(group.Id);
                    var index = members.ToList().FindIndex(item => item.Id == tab.Id);
                    index = e.Key switch
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
            tabs.Children.Add(chrome); tabSites[group.Id].Add((chrome, tab.Id));
            if (tab.Id == selected?.Id) Dispatcher.UIThread.Post(() => chrome.BringIntoView(), DispatcherPriority.Loaded);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Border Fade(bool left) => new()
        {
            Name = (left ? "TabOverflowBefore_" : "TabOverflowAfter_") + group.Id,
            Width = 16,
            IsHitTestVisible = false,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Background = left ? Ui.FadeFromElevated : Ui.FadeToElevated
        };
        var leftFade = Fade(true); var rightFade = Fade(false);
        Ui.Place(header, leftFade); Ui.Place(header, rightFade);
        var overflow = Ui.IconButton("search", "Search open tabs", () => ShowOverflow(group.Id));
        overflow.Content = Ui.Icon("search", size: 14);
        overflow.BorderThickness = new Thickness(1, 0, 0, 0);
        overflow.BorderBrush = Ui.BorderBrush;
        overflow.Name = "TabOverflow_" + group.Id;
        overflow.IsVisible = false;
        void Overflow()
        {
            overflow.IsVisible = scroll.Extent.Width > scroll.Viewport.Width + 1;
            if (!overflow.IsVisible) (overflow.Flyout as Flyout)?.Hide();
            leftFade.IsVisible = scroll.Offset.X > 1;
            rightFade.IsVisible = scroll.Offset.X + scroll.Viewport.Width < scroll.Extent.Width - 1;
        }
        scroll.ScrollChanged += (_, _) => Overflow();
        header.SizeChanged += (_, _) => Dispatcher.UIThread.Post(Overflow, DispatcherPriority.Loaded);
        scroll.PointerWheelChanged += (_, e) =>
        {
            if (Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X))
            { scroll.Offset = new(scroll.Offset.X - e.Delta.Y * 45, 0); e.Handled = true; }
        };
        actions.Children.Add(overflow);
        if (!group.Folded && OwnsBottomAlignmentMenu(group)) actions.Children.Add(BottomAlignmentButton());
        if (!group.Folded)
        {
            var terminal = Ui.IconButton("terminal", "New terminal in this group", () => { Session.NewTerminal(group.Id); FocusGroup(group.Id, focusContent: true); });
            terminal.Name = "NewTerminal_" + group.Id;
            terminal.IsVisible = selected?.IsTool != true;
            updates.Add(() => terminal.IsVisible = Session.Selected(group.Id)?.IsTool != true);
            actions.Children.Add(terminal);
            if (AddMenu(group) is { } menu)
            {
                var add = Ui.IconButton("add", "Show a hidden tool in this group", () => { });
                add.Name = "AddToGroup_" + group.Id;
                add.ContextMenu = menu;
                add.Click += (_, _) => menu.Open(add);
                actions.Children.Add(add);
            }
        }
        if (group.Region != "center" && (group.Folded || Session.State.Groups.Count(item => item.Region == group.Region) > 1))
        {
            var fold = Ui.IconButton(group.Folded ? "expandVertical" : "collapseVertical", group.Folded ? "Expand pane" : "Fold pane", () => ToggleFold(group.Id));
            fold.Name = "FoldRestore_" + group.Id;
            actions.Children.Add(fold);
            if (group.Folded) groupHeaders[group.Id] = fold;
        }
        if (Session.Tabs(group.Id).Count == 0)
        {
            var remove = Ui.IconButton("close", "Remove group", () => RemoveGroup(group.Id));
            remove.Name = "RemoveGroup_" + group.Id;
            remove.IsEnabled = RemoveGroupMenuItem(group).IsEnabled;
            actions.Children.Add(remove);
        }
        Ui.Place(header, actions, 0, 1);
        var headerFrame = Ui.Frame(header, Ui.Elevated); headerFrame.BorderThickness = new Thickness(0, 0, 0, 1);
        headerFrame.Name = "TabStrip_" + group.Id;
        Ui.Place(panel, headerFrame);
        if (!group.Folded) sites.Add((header, group.Id, true));
        if (!group.Folded)
        {
            var body = new DockPanel
            {
                Name = "DockBody_" + group.Id,
                Background = group.Region == "center" ? Ui.Surface : Ui.Sidebar
            };
            body.Mount(() => selected is null ? Empty(group) : renderContent(selected));
            void LabelBody()
            {
                var label = tabs.GetLogicalDescendants().OfType<DockTabButton>().FirstOrDefault(button => button.IsSelected);
                if (label is null) body.ClearValue(AutomationProperties.LabeledByProperty);
                else AutomationProperties.SetLabeledBy(body, label);
            }
            LabelBody();
            if (selected is null) body.ContextMenu = GroupMenu(group, panel);
            contentHosts.Add(body);
            updates.Add(() =>
            {
                var active = Session.Selected(group.Id);
                body.Mount(() => active is null ? Empty(Session.Group(group.Id)) : renderContent(active));
                LabelBody();
            });
            Ui.Place(panel, body, 1);
            sites.Add((body, group.Id, false));
        }
        else if (group.Region == "bottom")
        {
            var rail = Ui.Button(selected?.Title ?? "Empty group", () => ToggleFold(group.Id));
            rail.Name = "FoldRestore_" + group.Id;
            rail.Height = 27; rail.ContextMenu = GroupMenu(group, panel);
            rail.HorizontalAlignment = HorizontalAlignment.Stretch;
            rail.HorizontalContentAlignment = HorizontalAlignment.Center;
            rail.Padding = new Thickness(0);
            rail.Background = Brushes.Transparent;
            rail.BorderThickness = new Thickness(0);
            rail.CornerRadius = new CornerRadius(0);
            groupHeaders[group.Id] = rail;
            selectionUpdates[group.Id] = () =>
            {
                var title = Session.Selected(group.Id)?.Title ?? "Empty group";
                ((TextBlock)rail.Content!).Text = title;
                AutomationProperties.SetName(rail, title);
            };
            var rotated = new LayoutTransformControl { Child = rail, LayoutTransform = new RotateTransform(90), UseLayoutRounding = true };
            Control foldedContent = rotated;
            if (OwnsBottomAlignmentMenu(group))
            {
                var folded = new Grid { RowDefinitions = new RowDefinitions("32,*") };
                Ui.Place(folded, BottomAlignmentButton());
                Ui.Place(folded, rotated, 1);
                foldedContent = folded;
            }
            var frame = Ui.Frame(foldedContent);
            frame.Name = "FoldedDockGroup_" + group.Id;
            sites.Add((frame, group.Id, false));
            return frame;
        }
        else sites.Add((panel, group.Id, false));
        return panel;
    }

    private Control Empty(DockGroup group)
    {
        if (group.Region == "center") return renderContent(null);
        var empty = Ui.Text("Empty group", Ui.Muted, 12);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;
        return empty;
    }

    private static string ToolIcon(DockTab tab) => tab.Kind == "terminal" ? "terminal" : tab.Kind == "diff" ? "fileDiff" : tab.IsTool ? tab.Id switch
    {
        "projects" => "folderTab",
        "specs" => "bookFill",
        "files" => "file",
        "changes" => "fileDiff",
        _ => "discuss"
    } : "fileText";

    private bool OwnsBottomAlignmentMenu(DockGroup group)
    {
        var bottom = Session.State.Groups.Where(item => item.Region == "bottom").ToArray();
        return group.Region == "bottom" && group.Id == (bottom.FirstOrDefault(item => !item.Folded) ?? bottom.FirstOrDefault())?.Id;
    }

    private Button BottomAlignmentButton()
    {
        var button = Ui.IconButton("moreHorizontal", "Bottom panel alignment", () => { });
        button.Name = "BottomAlignment";
        button.BorderBrush = Ui.Hover;
        button.BorderThickness = new Thickness(1, 0, 0, 0);
        var menu = new ContextMenu
        {
            Width = 224,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            VerticalOffset = 4,
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            FontFamily = Ui.InterfaceFont,
            FontSize = 14,
            FontWeight = Ui.InterfaceWeight,
            Foreground = Ui.TextBrush
        };
        menu.Resources["MenuFlyoutScrollerMargin"] = new Thickness(0);
        menu.Resources["MenuFlyoutItemBackgroundPointerOver"] = Ui.Hover;
        menu.Resources["MenuFlyoutItemBackgroundPressed"] = Ui.Hover;
        menu.Resources["MenuFlyoutItemForegroundPointerOver"] = Ui.TextBrush;
        var rows = new Style(selector => selector.OfType<MenuItem>());
        rows.Setters.Add(new Setter(Layoutable.HeightProperty, 28d));
        rows.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 4)));
        rows.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)));
        rows.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Ui.TextBrush));
        menu.Styles.Add(rows);
        var check = new Style(selector => selector.OfType<MenuItem>().Class(":checked").Class(":radio")
            .Template().OfType<ContentControl>().Name("PART_ToggleIconPresenter"));
        check.Setters.Add(new Setter(Grid.ColumnProperty, 4));
        check.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(0)));
        check.Setters.Add(new Setter(Layoutable.WidthProperty, 14d));
        check.Setters.Add(new Setter(Layoutable.HeightProperty, 14d));
        check.Setters.Add(new Setter(ContentControl.ContentProperty,
            new Avalonia.Controls.Templates.FuncTemplate<Control>(() => Ui.Icon("check", Ui.Accent, 14))));
        menu.Styles.Add(check);
        foreach (var (value, label) in new[]
        {
            ("center", "Below center"), ("center-left", "Below center and left"),
            ("center-right", "Below center and right"), ("full", "Full width")
        })
        {
            var item = Ui.Menu(label, () => Session.Geometry(state => state.BottomAlignment = value));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = Session.State.BottomAlignment == value;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator
        {
            Height = 1,
            MinHeight = 1,
            Margin = new Thickness(-4, 4),
            Background = Ui.BorderBrush,
            Template = new Avalonia.Controls.Templates.FuncControlTemplate<Separator>((_, _) => new Border { Background = Ui.BorderBrush })
        });
        menu.Items.Add(Ui.Menu("Hide bottom panel", () => Session.Visible("bottom", false)));
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.Open(button);
        // Keyboard users land on the current alignment, as the reference's radio menu does.
        menu.Opened += (_, _) => Dispatcher.UIThread.Post(() => menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsChecked)?.Focus(),
            DispatcherPriority.Loaded);
        return button;
    }

    private ContextMenu? AddMenu(DockGroup group)
    {
        if (group.Region is not ("left" or "right")) return null;
        var hidden = DockState.ToolNames.Where(id => DockState.ToolRegion(id) == group.Region && !Session.State.Groups.Any(item => item.Tools.Any(tab => tab.Id == id))).ToArray();
        if (hidden.Length == 0) return null;
        var menu = new ContextMenu();
        foreach (var tool in hidden)
            menu.Items.Add(Ui.Menu("Show " + DockState.Tool(tool).Title, () => Session.RestoreTool(tool, group.Id)));
        return menu;
    }

    private ContextMenu TabMenu(DockGroup group, DockTab tab, Control geometry)
    {
        var menu = new ContextMenu();
        if (!tab.IsTool && tab.Kind != "terminal") menu.Items.Add(Ui.Menu("Keep open", () => Session.Keep(group.Id, tab.Id), tab.Preview));
        menu.Items.Add(Ui.Menu("Close", () => Session.Close(group.Id, tab.Id)));
        menu.Items.Add(RemoveGroupMenuItem(group));
        var members = Session.Tabs(group.Id);
        var position = members.ToList().FindIndex(item => item.Id == tab.Id);
        menu.Items.Add(Ui.Menu("Move left", () => Session.Move(tab.Id, group.Id, group.Id, position - 1), position > 0));
        menu.Items.Add(Ui.Menu("Move right", () => Session.Move(tab.Id, group.Id, group.Id, position + 2), position < members.Count - 1));
        var move = new MenuItem { Header = "Move to pane" };
        foreach (var destination in Session.State.Groups.Where(item => (tab.Kind == "terminal" || (item.Region == "center") != tab.IsTool) && item.Id != group.Id))
        {
            var target = Ui.Menu(GroupLabel(destination), () => Session.Move(tab.Id, group.Id, destination.Id, Session.Tabs(destination.Id).Count));
            menu.Opening += (_, _) => target.Header = GroupLabel(Session.Group(destination.Id));
            move.Items.Add(target);
        }
        menu.Items.Add(move);
        if (group.Region == "center")
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                var split = Ui.Menu("Split " + (edge switch { "top" => "up", "bottom" => "down", _ => edge }), () => Session.Move(tab.Id, group.Id, group.Id, 0, edge));
                menu.Opening += (_, _) => split.IsEnabled = CanCreate(group) &&
                    (edge is "left" or "right" ? geometry.Bounds.Width >= 640 : geometry.Bounds.Height >= 360);
                menu.Items.Add(split);
            }
        else
        {
            foreach (var edge in new[] { "before", "after" })
            {
                var direction = group.Region == "bottom" ? edge == "before" ? "left" : "right" : edge == "before" ? "above" : "below";
                var split = Ui.Menu("New group " + direction, () => Session.Move(tab.Id, group.Id, group.Id, 0, edge));
                menu.Opening += (_, _) => split.IsEnabled = Session.CanMove(tab.Id, group.Id, group.Id, 0, edge);
                menu.Items.Add(split);
            }
        }
        if (tab.IsTool || tab.Kind == "terminal")
            foreach (var region in new[] { "left", "right", "bottom" })
                foreach (var atStart in new[] { true, false })
                {
                    var direction = region == "bottom" ? atStart ? "left" : "right" : atStart ? "top" : "bottom";
                    var title = $"New {region} group at {direction}";
                    var create = Ui.Menu(title, () => Session.MoveToNewRegionGroup(tab.Id, group.Id, region, atStart));
                    menu.Opening += (_, _) =>
                    {
                        var limit = region == "bottom" ? Session.State.BottomLimit : Session.State.SideLimit;
                        create.IsEnabled = Session.State.Groups.Count(item => item.Region == region) < limit;
                        create.Header = title + (create.IsEnabled ? "" : $" — limited to {limit}");
                    };
                    menu.Items.Add(create);
                }
        return menu;
    }

    private string GroupLabel(DockGroup group) => $"{group.Region}: {Session.Selected(group.Id)?.Title ?? "Empty"}";
    private bool CanCreate(DockGroup group) => Session.State.Groups.Count(item => item.Region == group.Region) <
        (group.Region == "center" ? 4 : group.Region == "bottom" ? Session.State.BottomLimit : Session.State.SideLimit);

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
            menu.Items.Add(Ui.Menu(group.Folded ? "Expand" : "Fold", () => ToggleFold(group.Id)));
            menu.Items.Add(Ui.Menu("Hide " + group.Region + " region", () => Session.Visible(group.Region, false)));
        }
        menu.Items.Add(RemoveGroupMenuItem(group));
        return menu;
    }

    private MenuItem RemoveGroupMenuItem(DockGroup group) => Ui.Menu("Remove group", () => RemoveGroup(group.Id),
        group.Region == "center" ? Session.State.Center.Leaves().Count() > 1 :
            Session.State.Groups.Count(item => item.Region == group.Region) > 1 || group.Tools.Count == 0);

    private void RemoveGroup(string id)
    {
        if (Session.RemoveGroup(id)) FocusGroup(Session.View.FocusedGroup);
    }

    private void ToggleFold(string id)
    {
        Session.Fold(id);
        FocusGroup(id);
    }

    private void FocusGroup(string id, bool focusContent = false) => Dispatcher.UIThread.Post(() =>
    {
        if (!Session.State.Groups.Any(group => group.Id == id)) return;
        if (Session.View.FocusedGroup != id) Session.Focus(id);
        if (Session.Group(id).Folded) { groupHeaders.GetValueOrDefault(id)?.Focus(); return; }
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
            target?.GetLogicalDescendants().OfType<DockTabButton>().Single().Focus();
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