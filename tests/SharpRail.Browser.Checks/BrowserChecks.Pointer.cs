using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.UI.Terminal;

namespace SharpRail.Browser.Checks;

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
}