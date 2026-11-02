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
        menu.Items.Add(Ui.Menu("Copy absolute path", () => { }));
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
        var chevron = items[^1].GetVisualDescendants().OfType<Control>().First(part => part.Name == "PART_ChevronPath");
        var chevronRight = chevron.TranslatePoint(new Point(chevron.Bounds.Width, 0), menu)!.Value.X;
        Require(chevronRight <= menu.Bounds.Width - menu.Padding.Right, $"Split chevron ends at x={chevronRight:0.#}, past the menu's padded edge {menu.Bounds.Width - menu.Padding.Right:0.#}.");
        menu.Close();
        window.Close();
        Console.WriteLine("PASS menu layout: mixed icon, plain and submenu rows fit inside the menu padding");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}