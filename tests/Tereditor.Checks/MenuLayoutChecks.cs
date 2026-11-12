using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Docking;

namespace SharpRail.Checks;

/// <summary>
/// A menu mixing icon rows, plain rows and a submenu row (the tab menu) is as wide as its widest row, and no row's content
/// sits outside the menu padding (src/SharpRail.Plugins.UI.Kit/ActionIcons/SPEC.md).
/// </summary>
internal static class MenuLayoutChecks
{
    // The tab menu's "Group with" rows: none offers nothing, one stays a flat row, several share one submenu of titles in order.
    private static void GroupWithGrouping()
    {
        static DockSurface.ShowWithOffer Offer(string title) => new("GroupWith_" + title, title, () => { });
        static ContextMenu Build(params string[] titles)
        {
            var menu = new ContextMenu();
            DockSurface.AddGroupWith(menu, titles.Select(Offer).ToArray());
            return menu;
        }
        Require(Build().Items.Count == 0, "No candidate offers no Group with row.");
        var one = Build("Orchestrator").Items.OfType<MenuItem>().ToArray();
        Require(one.Length == 1 && (string?)one[0].Header == "Group with Orchestrator" && one[0].Items.Count == 0, "One candidate keeps a flat Group with row.");
        var mixed = new ContextMenu();
        DockSurface.AddGroupWith(mixed, [Offer("B"), Offer("C"), new("GroupWith_pane_x", "A and 2 more", () => { }, null, true), new("GroupWith_pane_y", "D and 1 more", () => { }, null, true)]);
        var mixedRows = mixed.Items.OfType<MenuItem>().Single().Items;
        Require(mixedRows.Cast<Control>().Select(item => item is MenuItem row ? (string?)row.Header : "-").SequenceEqual(["B", "C", "-", "A and 2 more", "D and 1 more"]),
            "Tab group entries follow the lone tabs after one separator.");
        var onlyPane = new ContextMenu();
        DockSurface.AddGroupWith(onlyPane, [new("GroupWith_pane_x", "A and 2 more", () => { }, null, true)]);
        Require(onlyPane.Items.OfType<MenuItem>().Single().Header is "Group with A and 2 more", "A single tab group is a flat row.");
        foreach (var count in new[] { 2, 15 })
        {
            var titles = Enumerable.Range(1, count).Select(index => "Tab " + index).ToArray();
            var rows = Build(titles).Items.OfType<MenuItem>().ToArray();
            Require(rows.Length == 1 && rows[0].Name == "GroupWithMenu" && (string?)rows[0].Header == "Group with" && rows[0].Items.OfType<MenuItem>().Select(item => (string?)item.Header)
                .SequenceEqual(titles), $"{count} candidates share one Group with submenu of their titles in order.");
        }
    }

    // Choosing a tab that is not a neighbour groups the two, and Show in directory reveals the file in the Files tree: only file tabs offer it, deep files expand their parents, and a folded Files pane opens.
    internal static void GroupAndReveal(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "group-reveal", "deep", "er", "est"));
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "d.txt", Path.Combine("deep", "er", "est", "z.txt") }) File.WriteAllText(Path.Combine(root, "group-reveal", name), name);
        using var app = new SharpRail.Checks.E2E.E2eWorkspace(Path.Combine(root, "group-reveal"));
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" }) app.Open(name, true);
        _ = app.Window.OpenDocumentAsync("deep/er/est/z.txt", true);
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => app.Tabs.Any(tab => tab.Path == "deep/er/est/z.txt"));
        app.Window.Layout.Select(app.Center, "file:a.txt");
        var tab = app.Tab("a.txt");
        app.Click(tab, mouseButton: Avalonia.Input.MouseButton.Right);
        var menu = tab.ContextMenu!;
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => menu.IsOpen);
        var submenu = menu.Items.OfType<MenuItem>().Single(item => item.Name == "GroupWithMenu");
        Require(submenu.Items.OfType<MenuItem>().Select(item => (string?)item.Header).SequenceEqual(new[] { "b.txt", "c.txt", "d.txt", "z.txt" }),
            "Group with lists every other tab in strip order, not only the neighbours.");
        Require(menu.Items.OfType<MenuItem>().Any(item => item.Name == "TabShowInDirectory"), "A file tab offers Show in directory.");
        submenu.Items.OfType<MenuItem>().Single(item => item.Name == "GroupWith_file_c.txt").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Require(app.Window.Layout.PaneFor(app.Center, "file:a.txt") is { } pane && pane.TabIds.Contains("file:c.txt") && !pane.TabIds.Contains("file:b.txt"),
            "Choosing a non-neighbour tab groups the two tabs.");

        var filesGroup = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tool => tool.Id == "files"));
        app.Window.Layout.Visible(filesGroup.Region, false);
        var zTab = app.Tab("deep/er/est/z.txt");
        app.Click(zTab, mouseButton: Avalonia.Input.MouseButton.Right);
        var zMenu = zTab.ContextMenu!;
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => zMenu.IsOpen);
        zMenu.Items.OfType<MenuItem>().Single(item => item.Name == "TabShowInDirectory").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        zMenu.Close();
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => app.Window.Layout.RegionVisible(filesGroup.Region) && app.Window.GetLogicalDescendants().OfType<SharpRail.Plugins.UI.Kit.DrawnTree>()
            .FirstOrDefault(tree => tree.Name == "FilesTree") is { SelectedItem: SharpRail.Plugins.UI.Kit.DrawnTreeEntry { Tag: SharpRail.Host.Abstractions.ProjectFile { Path: "deep/er/est/z.txt" } } } shown &&
            shown.Rows.Any(row => row.Id == "deep/er/est" && row.Expanded)); // the single-child chain deep/er/est is one compacted row

        // A document that is not a file has no node: a terminal-style tab with no path offers nothing.
        app.Window.Layout.Open(new("terminal:menu", "Shell", "terminal"), false);
        var shell = app.Find<Button>("Tab_terminal_menu");
        app.Click(shell, mouseButton: Avalonia.Input.MouseButton.Right);
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => shell.ContextMenu!.IsOpen);
        Require(!shell.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Name == "TabShowInDirectory"), "A terminal tab does not offer Show in directory.");
        shell.ContextMenu.Close();
        Console.WriteLine("PASS menu layout: Group with lists every tab and groups a non-neighbour, Show in directory reveals deep files");
    }

    internal static void Run()
    {
        GroupWithGrouping();
        var anchor = new Button { Content = "tab", Width = 80, Height = 24 };
        var window = new Window { Width = 400, Height = 300, Content = anchor };
        window.Show();
        var menu = new ContextMenu();
        menu.Items.Add(Ui.IconMenu("Close", () => { }, ActionIcon.Close));
        menu.Items.Add(Ui.IconMenu("Copy path", () => { }, ActionIcon.CopyPath));
        menu.Items.Add(Ui.IconMenu("Copy absolute path", () => { }, ActionIcon.CopyAbsolutePath));
        menu.Items.Add(Ui.Menu("Close group", () => { }));
        var split = new MenuItem { Header = "Split" };
        split.Items.Add(Ui.Menu("Right", () => { }));
        menu.Items.Add(split);
        anchor.ContextMenu = menu;
        var born = Born(menu);
        RequestContext(anchor);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        BornWideEnough("tab menu with submenu chevron", menu, born);

        var items = menu.Items.OfType<MenuItem>().ToArray();
        var widest = items.Max(item => item.DesiredSize.Width);
        var inner = menu.Bounds.Width - menu.Padding.Left - menu.Padding.Right - menu.BorderThickness.Left - menu.BorderThickness.Right;
        Require(inner >= widest - 0.5, $"Menu is {menu.Bounds.Width:0.#} wide ({inner:0.#} inside padding) but its widest row needs {widest:0.#}.");
        var icon = items[0].GetVisualDescendants().OfType<ContentControl>().First(part => part.Name == "PART_IconPresenter");
        var iconX = icon.TranslatePoint(default, menu)!.Value.X;
        Require(iconX >= menu.Padding.Left + items[0].Padding.Left - 0.5, $"Close icon starts at x={iconX:0.#}, outside the menu and row padding.");
        var textX = items.Take(3).Select(item => item.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == (string)item.Header!)
            .TranslatePoint(default, menu)!.Value.X).ToArray();
        Require(items[1].Icon is not null && items[2].Icon is not null && textX.All(x => Math.Abs(x - textX[0]) < 0.5),
            $"Copy path rows must carry icons and align their text with Close (x={string.Join(", ", textX.Select(x => x.ToString("0.#")))}).");
        var closeIcon = icon.TranslatePoint(new Point(icon.Bounds.Width, 0), menu)!.Value.X;
        var edgeGap = iconX - menu.BorderThickness.Left;
        var textGap = textX[0] - closeIcon;
        Require(Math.Abs(edgeGap - textGap) <= 3, $"Close icon is {edgeGap:0.#} from the menu edge but {textGap:0.#} from its text.");
        var chevron = items[^1].GetVisualDescendants().OfType<Control>().First(part => part.Name == "PART_ChevronPath");
        var chevronRight = chevron.TranslatePoint(new Point(chevron.Bounds.Width, 0), menu)!.Value.X;
        Require(chevronRight <= menu.Bounds.Width - menu.Padding.Right, $"Split chevron ends at x={chevronRight:0.#}, past the menu's padded edge {menu.Bounds.Width - menu.Padding.Right:0.#}.");
        IconsInside("tab menu", menu);
        menu.Close();
        // The Git panel's change menu: plain rows around one Copy path row with the same icon as the tab menu.
        var changeMenu = new ContextMenu();
        changeMenu.Items.Add(Ui.Menu("View", () => { }));
        changeMenu.Items.Add(Ui.IconMenu("Copy path", () => { }, ActionIcon.CopyPath));
        changeMenu.Items.Add(Ui.Menu("Stage file", () => { }));
        anchor.ContextMenu = changeMenu;
        changeMenu.Open(anchor);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var changeItems = changeMenu.Items.OfType<MenuItem>().ToArray();
        var changeWidest = changeItems.Max(item => item.DesiredSize.Width);
        Require(changeItems[1].Icon is not null && changeMenu.Bounds.Width - changeMenu.Padding.Left - changeMenu.Padding.Right >= changeWidest - 0.5,
            "The change menu's Copy path row must carry an icon and fit inside the menu padding.");
        changeMenu.Close();
        window.Close();
        Console.WriteLine("PASS menu layout: mixed icon, plain and submenu rows fit inside the menu padding");
        LazyTabMenu();
        BottomPadding();
        ShortcutsFit();
        PopupsStayInWindow();
        RecentProjectRows();
        ActionIconChecks.InkInsideViewBox();
        AuditCatchesBrokenIcons();
    }

    // The audit must fail on a menu that breaks the rule: an icon bigger than its slot, one pushed past it by a margin, and one off-centre.
    private static void AuditCatchesBrokenIcons()
    {
        var broken = new (string Name, Action<MenuItem> Break)[]
        {
            ("an icon pushed past the slot by a margin", item => ((Control)item.Icon!).Margin = new Thickness(9, 0, 0, 0)),
            ("an icon moved out of the slot by a transform", item => ((Control)item.Icon!).RenderTransform = new Avalonia.Media.TranslateTransform(7, 0)),
            ("an icon cut by a clipping ancestor", item => item.Icon = new Border { ClipToBounds = true, Width = 10, Height = 14, Child = Ui.Icon("add", size: 14), HorizontalAlignment = HorizontalAlignment.Center }),
        };
        MenuIconAudit.Probing = true;
        try
        {
            foreach (var (name, apply) in broken)
            {
                var anchor = new Button { Content = "x", Width = 80, Height = 24 };
                var window = new Window { Width = 400, Height = 300, Content = anchor };
                window.Show();
                var menu = Menu(("Add", ActionIcon.Add), ("Close", ActionIcon.Close));
                anchor.ContextMenu = menu;
                menu.Open(anchor);
                Settle();
                MenuIconAudit.Audit("good menu", menu);
                apply(menu.Items.OfType<MenuItem>().First());
                Settle();
                var failed = false;
                try { MenuIconAudit.Audit(name, menu); }
                catch (InvalidOperationException) { failed = true; }
                Require(failed, $"The menu icon audit passed a menu with {name}.");
                menu.Close();
                window.Close();
            }
        }
        finally { MenuIconAudit.Probing = false; }
        Console.WriteLine($"PASS menu layout: icons sit in one {LayoutRoles.MenuIconSlot}px slot, centred and unclipped, in every audited menu ({MenuIconAudit.Audited} audited); the audit fails on broken icons");
    }

    // The add-project menu's recent paths read as folder name plus a muted parent, cut in the middle, and never widen the menu past its bound.
    private static void RecentProjectRows()
    {
        var anchor = new Button { Content = "+", Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Width = 360, Height = 400, Content = new Grid { Children = { anchor } } };
        window.Show();
        var menu = new ContextMenu { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = anchor };
        menu.Items.Add(Ui.IconMenu("Create project…", () => { }, ActionIcon.NewFolder));
        menu.Items.Add(Ui.IconMenu("Clone repository…", () => { }, ActionIcon.Clone));
        menu.Items.Add(new Separator());
        var paths = new[] { "/Users/commandertvis/ansible-configuration", "/Users/commandertvis/thinkrail/a-project-with-a-really-long-folder-name-that-goes-on-and-on", "/Users/commandertvis/some/very/deeply/nested/directory/structure/that/is/long/sample-project" };
        foreach (var path in paths) menu.Items.Add(new MenuItem { Header = SharpRail.UI.WorkbenchWindow.RecentLabel(path) });
        anchor.ContextMenu = menu;
        menu.Open(anchor);
        Settle();
        var popup = ScreenBounds(menu);
        var button = ScreenBounds(anchor);
        Require(menu.Bounds.Width <= menu.MaxWidth + 0.5, $"recent paths: the menu is {menu.Bounds.Width:0.#} wide, past {menu.MaxWidth:0.#}.");
        Require(popup.Y >= button.Y + button.Height - 1 && Inside(popup, ScreenBounds(window)), $"recent paths: the menu spans {popup} against the button {button}.");
        foreach (var label in menu.Items.OfType<MenuItem>().Select(item => item.Header).OfType<StackPanel>())
        {
            var shown = ScreenBounds(label);
            Require(Inside(shown, popup), $"recent paths: a row's text spans {shown}, outside the menu {popup}.");
        }
        Require(((TextBlock)SharpRail.UI.WorkbenchWindow.RecentLabel(paths[0]).Children[0]).Text == "ansible-configuration", "recent paths: the folder name leads the row.");
        menu.Close();
        window.Close();
        Console.WriteLine("PASS menu layout: recent project rows show the folder name, stay bounded and sit under the button");
    }

    // Every menu the app builds, by the shape its builder gives it. A new menu is added here, and a menu that leaves the window fails the sweep.
    private static (string Name, Func<ContextMenu> Build)[] SweptMenus() =>
    [
        ("project row", () => Menu(("Copy absolute path", ActionIcon.CopyAbsolutePath), ("Start work", ActionIcon.Add), ("Open existing worktree", ActionIcon.OpenWorktree), ("Rename", ActionIcon.EnterPath), ("Close project", ActionIcon.Close))),
        ("workspace row", () => Menu(("Rename", ActionIcon.EnterPath), ("Copy path", ActionIcon.CopyPath), ("Delete workspace", ActionIcon.Delete))),
        ("tab", () => { var menu = Menu(("Close", ActionIcon.Close), ("Copy path", ActionIcon.CopyPath), ("Copy absolute path", ActionIcon.CopyAbsolutePath)); menu.Items.Add(Ui.Menu("Close group", () => { })); var split = new MenuItem { Header = "Split" }; split.Items.Add(Ui.Menu("Right", () => { })); menu.Items.Add(split); return menu; }),
        ("tab with Group with", () => { var menu = Menu(("Close", ActionIcon.Close)); DockSurface.AddGroupWith(menu, [new("GroupWith_a", "A", () => { }), new("GroupWith_b", "B", () => { })]); return menu; }),
        ("header View", () => { var menu = Menu(("Files", ActionIcon.Check), ("Changes", ActionIcon.Check)); menu.Items.Add(new Separator()); menu.Items.Add(Ui.Menu("Reset layout", () => { })); return menu; }),
        ("branch", () => Menu(("Check out", ActionIcon.Check), ("Copy name", ActionIcon.CopyPath), ("Delete branch", ActionIcon.Delete))),
        ("file tree", () => Menu(("New file…", ActionIcon.NewFile), ("Rename…", ActionIcon.EnterPath), ("Copy path", ActionIcon.CopyPath), ("Delete file", ActionIcon.Delete))),
        ("settings theme choice", () => { var menu = new ContextMenu { MinWidth = 280 }; foreach (var label in new[] { "SharpRail Light", "Solarized Light", "GitHub Light" }) menu.Items.Add(Ui.Menu(label, () => { })); return menu; }),
        ("long labels", () => { var menu = Menu(("Open build definition for the selected project in the workspace", ActionIcon.EnterPath)); for (var i = 0; i < 40; i++) menu.Items.Add(Ui.Menu("Commit " + i, () => { })); return menu; }),
    ];

    private static ContextMenu Menu(params (string Label, ActionIcon Icon)[] rows)
    {
        var menu = new ContextMenu();
        foreach (var (label, icon) in rows) menu.Items.Add(Ui.IconMenu(label, () => { }, icon));
        return menu;
    }

    private static (double X, double Y, double Width, double Height) ScreenBounds(Control control)
    {
        var top = TopLevel.GetTopLevel(control)!;
        var origin = top.PointToScreen(control.TranslatePoint(default, top)!.Value);
        return (origin.X, origin.Y, control.Bounds.Width * top.RenderScaling, control.Bounds.Height * top.RenderScaling);
    }

    private static bool Inside((double X, double Y, double Width, double Height) inner, (double X, double Y, double Width, double Height) outer) =>
        inner.X >= outer.X - 1 && inner.Y >= outer.Y - 1 && inner.X + inner.Width <= outer.X + outer.Width + 1 && inner.Y + inner.Height <= outer.Y + outer.Height + 1;

    private static void Settle()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    // Popups never leave the window: every menu, opened with its anchor against each edge of a narrow window and under each placement, lies inside
    // the window with its icons inside the menu, and so do a combo box dropdown and a flyout.
    private static void PopupsStayInWindow()
    {
        var alignments = new (string Name, HorizontalAlignment Horizontal, VerticalAlignment Vertical)[]
        {
            ("left", HorizontalAlignment.Left, VerticalAlignment.Center), ("right", HorizontalAlignment.Right, VerticalAlignment.Center),
            ("top", HorizontalAlignment.Center, VerticalAlignment.Top), ("bottom", HorizontalAlignment.Center, VerticalAlignment.Bottom),
            ("top left", HorizontalAlignment.Left, VerticalAlignment.Top), ("bottom right", HorizontalAlignment.Right, VerticalAlignment.Bottom),
        };
        foreach (var (name, build) in SweptMenus())
            foreach (var placement in new[] { Avalonia.Controls.PlacementMode.Pointer, Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft, Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight })
                foreach (var (edge, horizontal, vertical) in alignments)
                {
                    var anchor = new Button { Content = "x", Width = 60, Height = 24, HorizontalAlignment = horizontal, VerticalAlignment = vertical };
                    var window = new Window { Width = 480, Height = 320, Content = new Grid { Children = { anchor } } };
                    window.Show();
                    var menu = build();
                    menu.Placement = placement;
                    anchor.ContextMenu = menu;
                    menu.Open(anchor);
                    Settle();
                    var label = $"{name} menu, {placement}, anchor at the {edge}";
                    Require(menu.IsOpen, $"{label}: the menu did not open.");
                    var popup = ScreenBounds(menu);
                    var area = ScreenBounds(window);
                    Require(Inside(popup, area), $"{label}: the popup spans {popup} but the window is {area}.");
                    MenuIconAudit.Audit(label, menu);
                    foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Icon is not null))
                    {
                        var icon = ScreenBounds(item.GetVisualDescendants().OfType<ContentControl>().First(part => part.Name == "PART_IconPresenter"));
                        Require(Inside(icon, popup), $"{label}: the icon of '{item.Header}' spans {icon}, outside the menu {popup}.");
                    }
                    menu.Close();
                    window.Close();
                }
        foreach (var (edge, horizontal, vertical) in alignments)
        {
            var combo = new ComboBox { Width = 120, HorizontalAlignment = horizontal, VerticalAlignment = vertical, ItemsSource = Enumerable.Range(1, 30).Select(i => "Option " + i).ToArray(), SelectedIndex = 0 };
            var window = new Window { Width = 480, Height = 320, Content = new Grid { Children = { combo } } };
            window.Show();
            combo.IsDropDownOpen = true;
            Settle();
            var child = combo.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single().Child!;
            var bounds = ScreenBounds(child);
            Require(Inside(bounds, ScreenBounds(window)), $"combo box dropdown, anchor at the {edge}: the dropdown spans {bounds} but the window is {ScreenBounds(window)}.");
            combo.IsDropDownOpen = false;
            window.Close();
        }
        Console.WriteLine("PASS menu layout: every swept menu, a combo box dropdown and their icons stay inside the window at every edge");
    }

    // A menu with shortcuts (the editor's language menu): the popup is born wide enough for the widest label, a gap and the widest shortcut, at several font sizes.
    private static void ShortcutsFit()
    {
        const double gap = 16;
        var cases = new (string Name, (string Label, Avalonia.Input.Key Key, Avalonia.Input.KeyModifiers Modifiers)[] Rows)[]
        {
            ("editor language menu", [("Go to Declaration", Avalonia.Input.Key.B, Avalonia.Input.KeyModifiers.Meta),
                ("Go to Type Declaration", Avalonia.Input.Key.B, Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Shift), ("Find Usages", Avalonia.Input.Key.F7, Avalonia.Input.KeyModifiers.Alt),
                ("Start / Retry Language Server", Avalonia.Input.Key.None, Avalonia.Input.KeyModifiers.None)]),
            ("long labels and long shortcuts", [("Open build definition for the selected project in the workspace", Avalonia.Input.Key.F9, Avalonia.Input.KeyModifiers.Alt | Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Shift | Avalonia.Input.KeyModifiers.Control),
                ("Go", Avalonia.Input.Key.PageDown, Avalonia.Input.KeyModifiers.Alt | Avalonia.Input.KeyModifiers.Meta)]),
        };
        foreach (var (name, rows) in cases)
            foreach (var fontSize in new[] { 10.0, 13, 16, 22 })
            {
                var anchor = new Button { Content = "x", Width = 80, Height = 24 };
                var window = new Window { Width = 900, Height = 600, Content = anchor, FontSize = fontSize };
                window.Show();
                var menu = new ContextMenu { FontSize = fontSize };
                foreach (var (label, key, modifiers) in rows)
                {
                    var item = Ui.Menu(label, () => { });
                    if (key != Avalonia.Input.Key.None) item.InputGesture = new Avalonia.Input.KeyGesture(key, modifiers);
                    menu.Items.Add(item);
                }
                anchor.ContextMenu = menu;
                var born = Born(menu);
                RequestContext(anchor);
                Dispatcher.UIThread.RunJobs();
                var bornWidth = born();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var items = menu.Items.OfType<MenuItem>().ToArray();
                double Width(IEnumerable<string?> texts) => texts.Where(text => !string.IsNullOrEmpty(text)).Select(text => items.SelectMany(item => item.GetVisualDescendants().OfType<TextBlock>())
                    .First(block => block.Text == text).Bounds.Width).DefaultIfEmpty(0).Max();
                var labels = Width(items.Select(item => (string?)item.Header));
                var shortcuts = Width(items.Select(item => item.InputGesture?.ToString()));
                var needed = Math.Min(labels + gap + shortcuts + items[0].Padding.Left + items[0].Padding.Right + menu.Padding.Left + menu.Padding.Right, menu.MaxWidth);
                Console.WriteLine($"{name} at {fontSize}: born {bornWidth:0.#}, settled {menu.Bounds.Width:0.#}, needs {needed:0.#} (label {labels:0.#} + shortcut {shortcuts:0.#})");
                Require(bornWidth >= needed - 0.5 && menu.Bounds.Width >= needed - 0.5, $"{name} at {fontSize}: the popup is born {bornWidth:0.#} wide (settled {menu.Bounds.Width:0.#}) but its labels and shortcuts need {needed:0.#}.");
                foreach (var item in items.Where(item => item.InputGesture is not null))
                {
                    var shortcut = item.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == item.InputGesture!.ToString());
                    var right = shortcut.TranslatePoint(new Point(shortcut.Bounds.Width, 0), menu)!.Value.X;
                    Require(right <= bornWidth - menu.Padding.Right + 0.5, $"{name} at {fontSize}: the shortcut of '{item.Header}' ends at x={right:0.#}, past the popup's {bornWidth:0.#}.");
                }
                menu.Close();
                window.Close();
            }
        Console.WriteLine("PASS menu layout: labels and shortcuts fit the popup at several font sizes");
    }

    // The terminal tab menu: plugins' rows (an icon among them) arrive as it opens, after the first measure.
    private static void LazyTabMenu()
    {
        var anchor = new Button { Content = "tab", Width = 80, Height = 24 };
        var window = new Window { Width = 400, Height = 300, Content = anchor };
        window.Show();
        var menu = new ContextMenu();
        menu.Items.Add(Ui.IconMenu("Close", () => { }, ActionIcon.Close));
        menu.Items.Add(Ui.Menu("Rename", () => { }));
        menu.Items.Add(Ui.Menu("Move up", () => { }));
        var split = new MenuItem { Header = "Split" };
        split.Items.Add(Ui.Menu("Right", () => { }));
        menu.Items.Add(split);
        // A plugin's row arrives while the menu opens, before the popup is shown.
        menu.Opening += (_, _) =>
        {
            var copy = Ui.Menu("Copy session ID", () => { });
            copy.Icon = Ui.Icon("terminal", size: 14);
            menu.Items.Insert(2, copy);
        };
        anchor.ContextMenu = menu;
        var born = Born(menu);
        RequestContext(anchor);
        // The popup is sized from this first pass, before the shared icon column has settled across the rows.
        (TopLevel.GetTopLevel(menu) as Avalonia.Layout.Layoutable)?.UpdateLayout();
        BornWideEnough("terminal tab menu with a plugin row inserted at Opening", menu, born);
        IconsInside("terminal tab menu, first pass", menu);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        IconsInside("terminal tab menu", menu);
        menu.Close();
        window.Close();
        Console.WriteLine("PASS menu layout: the terminal tab menu keeps plugin-inserted icons inside its bounds");
    }

    // The width the menu holds right after Opened fires, before the deferred re-measure: what a native popup is sized from.
    // What a right click does: the context request raises Opening and then opens the menu (ContextMenu.Open alone raises no Opening).
    private static void RequestContext(Control anchor) =>
        anchor.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = anchor });

    private static Func<double> Born(ContextMenu menu)
    {
        var width = double.NaN;
        menu.Opened += (_, _) => width = Math.Max(menu.Width is var w && !double.IsNaN(w) ? w : 0, menu.MinWidth);
        return () => width;
    }

    private static void BornWideEnough(string name, ContextMenu menu, Func<double> born)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var rows = menu.Items.OfType<MenuItem>().ToArray();
        var needed = rows.Max(row => row.DesiredSize.Width) + menu.Padding.Left + menu.Padding.Right + menu.BorderThickness.Left + menu.BorderThickness.Right;
        needed = Math.Min(needed, menu.MaxWidth);
        Console.WriteLine($"{name}: born {born():0.#}, widest row needs {needed:0.#}");
        Require(born() >= needed - 0.5, $"{name}: the popup is born {born():0.#} wide but its widest row needs {needed:0.#}.");
    }

    private static void IconsInside(string name, ContextMenu menu)
    {
        var rows = menu.Items.OfType<MenuItem>().ToArray();
        var inner = menu.Bounds.Width - menu.Padding.Left - menu.Padding.Right - menu.BorderThickness.Left - menu.BorderThickness.Right;
        var widest = rows.Max(row => row.DesiredSize.Width);
        Require(inner >= widest - 0.5, $"{name}: the menu is {inner:0.#} wide inside its padding but its widest row needs {widest:0.#}.");
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Icon is not null))
        {
            var icon = item.GetVisualDescendants().OfType<ContentControl>().First(part => part.Name == "PART_IconPresenter");
            var left = icon.TranslatePoint(default, menu)!.Value.X;
            var right = icon.TranslatePoint(new Point(icon.Bounds.Width, 0), menu)!.Value.X;
            var text = item.GetVisualDescendants().OfType<TextBlock>().First(part => part.Text == (string)item.Header!).TranslatePoint(default, menu)!.Value.X;
            Require(left >= 0 && right <= menu.Bounds.Width && right <= text + 0.5, $"{name}: the icon of '{item.Header}' spans x={left:0.#}..{right:0.#}, outside the menu (0..{menu.Bounds.Width:0.#}).");
        }
        MenuIconAudit.Audit(name, menu);
    }

    internal static void RealFileMenu(string root)
    {
        using var app = new SharpRail.Checks.E2E.E2eWorkspace(Path.Combine(root, "menu-layout"));
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        foreach (var id in new[] { "claude-code", "codex", "omp" })
            app.Workbench.PluginRegistry.AddFileAction(id, new("mention", target => "Mention file in " + id, _ => { }) { Icon = "terminal-line" });
        var row = app.FileRow("notes.txt");
        app.Click(row, mouseButton: Avalonia.Input.MouseButton.Right);
        var menu = row.ContextMenu!;
        SharpRail.Checks.E2E.E2eWorkspace.Until(() => menu.IsOpen);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var rows = menu.Items.OfType<MenuItem>().ToArray();
        var bottom = rows[^1].TranslatePoint(new Point(0, rows[^1].Bounds.Height), menu)!.Value.Y;
        Console.WriteLine($"real file menu: {rows.Length} rows, height {menu.Bounds.Height:0.#}, last row bottom {bottom:0.#}, gap {menu.Bounds.Height - bottom:0.#}, first row top {rows[0].TranslatePoint(default, menu)!.Value.Y:0.#}");
        var top = rows[0].TranslatePoint(default, menu)!.Value.Y;
        Require(Math.Abs(menu.Bounds.Height - bottom - top) <= 1, $"The file menu leaves {menu.Bounds.Height - bottom:0.#} below its last row but {top:0.#} above the first.");
        menu.Close();
        Console.WriteLine("PASS menu layout: the Files panel's own menu, with plugins' file actions, is as tight at the bottom as at the top");
    }

    // The last row sits as far from the menu's bottom edge as the first row does from the top edge, whatever the rows hold above it.
    private static void BottomPadding()
    {
        var entry = new SharpRail.Plugins.Api.PluginRosterEntry("claude-code", "Claude Code", "puzzle", "1", 1, default, default) { Assets = "assets" };
        var gate = new TaskCompletionSource<byte[]?>();
        SharpRail.UI.Plugins.PluginIcons.ReadAsset = (_, _, _) => gate.Task;
        ContextMenu Menu(bool plugin, bool assetIcon, bool trailing)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Ui.IconMenu("New file…", () => { }, ActionIcon.NewFile));
            menu.Items.Add(Ui.Menu("Rename…", () => { }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.IconMenu("Delete file", () => { }, ActionIcon.Delete));
            if (plugin)
            {
                menu.Items.Add(new Separator());
                var mention = Ui.Menu("Mention file in Claude Code", () => { });
                mention.Icon = SharpRail.UI.Plugins.PluginIcons.Resolve(assetIcon ? "asset:icon.svg" : "terminal-line", entry, null, 14);
                menu.Items.Add(mention);
                var codex = Ui.Menu("Mention file in Codex", () => { }, false);
                codex.Icon = SharpRail.UI.Plugins.PluginIcons.Resolve("openai-line", entry, null, 14);
                menu.Items.Add(codex);
            }
            if (trailing)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Ui.Menu("Configure csharp language service (workspace)…", () => { }));
                menu.Items.Add(Ui.Menu("Open build definition · src/SharpRail.Plugins.ClaudeCode/UI/SharpRail.Plugins.ClaudeCode.UI.csproj", () => { }));
            }
            if (trailing) menu.Items.Add(Ui.Menu("Last plain row", () => { }));
            return menu;
        }
        var cases = new (string Name, ContextMenu Menu)[]
        {
            ("file menu, glyph plugin icon", Menu(true, false, false)),
            ("file menu, async asset plugin icon", Menu(true, true, false)),
            ("file menu, plain last row", Menu(true, false, true)),
            ("no plugin items", Menu(false, false, false)),
        };
        foreach (var (name, menu) in cases)
        {
            var anchor = new Button { Content = "x", Width = 80, Height = 24 };
            var window = new Window { Width = 400, Height = 400, Content = anchor };
            window.Show();
            anchor.ContextMenu = menu;
            menu.Open(anchor);
            var opened = $"{menu.DesiredSize.Height:0.#}/{menu.Bounds.Height:0.#}";
            menu.UpdateLayout();
            var first = $"{menu.DesiredSize.Height:0.#}/{menu.Bounds.Height:0.#}";
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"  desired/bounds right after open {opened}, after layout {first}, settled {menu.DesiredSize.Height:0.#}/{menu.Bounds.Height:0.#}");
            if (name.Contains("async", StringComparison.Ordinal))
            {
                gate.TrySetResult(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M12 2l3 7h7l-6 5 2 8-6-4-6 4 2-8-6-5h7z' fill='currentColor'/></svg>"));
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"  after the asset arrived: desired/bounds {menu.DesiredSize.Height:0.#}/{menu.Bounds.Height:0.#}");
            }
            var rows = menu.Items.OfType<MenuItem>().ToArray();
            var last = rows[^1];
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), menu)!.Value.Y;
            var gap = menu.Bounds.Height - bottom;
            var expected = rows[0].TranslatePoint(default, menu)!.Value.Y;
            var icon = rows.First(row => row.Icon is not null).GetVisualDescendants().OfType<ContentControl>().First(part => part.Name == "PART_IconPresenter");
            var left = icon.TranslatePoint(default, menu)!.Value.X;
            Console.WriteLine($"{name}: menu height {menu.Bounds.Height:0.#}, last row bottom {bottom:0.#}, bottom gap {gap:0.#} (top gap {expected:0.#}), icon left {left:0.#}");
            Require(Math.Abs(gap - expected) <= 1, $"{name}: {gap:0.#} below the last row, but {expected:0.#} above the first.");
            menu.Close();
            window.Close();
        }
        Console.WriteLine("PASS menu layout: the last row sits at the menu padding from the bottom edge");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}