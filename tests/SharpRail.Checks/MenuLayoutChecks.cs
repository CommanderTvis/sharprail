using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Checks;

/// <summary>
/// A menu mixing icon rows, plain rows and a submenu row (the tab menu) is as wide as its widest row, and no row's content
/// sits outside the menu padding (src/SharpRail.Plugins.UI.Kit/ActionIcons/SPEC.md).
/// </summary>
internal static class MenuLayoutChecks
{
    internal static void Run()
    {
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
        menu.Open(anchor);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

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
        menu.Close();
        window.Close();
        Console.WriteLine("PASS menu layout: mixed icon, plain and submenu rows fit inside the menu padding");
        BottomPadding();
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