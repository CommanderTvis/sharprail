using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.UI.Terminal;

namespace Tereditor.Browser.Checks;

internal static partial class BrowserChecks
{
    [JSExport]
    public static async Task<string> PointerSnapshot() => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        var window = Workbench.ActiveWindow!;
        var projects = window.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ProjectsTree");
        return JsonSerializer.Serialize(new
        {
            Projects = projects.IsPointerOver,
            TerminalFocused = window.GetVisualDescendants().OfType<TerminalView>().Any(terminal => terminal.IsKeyboardFocusWithin)
        });
    });

    private static bool popupClicked;

    /// <summary>Opens a menu over the middle of the terminal and returns where its only item is, in window coordinates.</summary>
    [JSExport]
    public static async Task<string> OpenPopupOverTerminal()
    {
        popupClicked = false;
        var (menu, item, window) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Workbench.ActiveWindow!;
            var terminal = window.GetVisualDescendants().OfType<TerminalView>().First(view => view.IsEffectivelyVisible);
            var item = new MenuItem { Header = "Popup over terminal", Name = "PopupOverTerminal", MinWidth = 220 };
            item.Click += (_, _) => popupClicked = true;
            var menu = new ContextMenu { Items = { item }, Placement = Avalonia.Controls.PlacementMode.Center, PlacementTarget = terminal };
            menu.Open(terminal);
            return (menu, item, window);
        });
        await Task.Delay(500);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var center = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
            return JsonSerializer.Serialize(new { X = center.X, Y = center.Y, Open = menu.IsOpen });
        });
    }

    [JSExport]
    public static Task<bool> PopupClicked() => Task.FromResult(popupClicked);
}
