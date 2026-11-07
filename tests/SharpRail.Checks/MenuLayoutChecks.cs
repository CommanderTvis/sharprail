using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Interactivity;
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