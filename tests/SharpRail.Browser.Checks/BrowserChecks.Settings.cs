using System.Reflection;
using System.Runtime.InteropServices.JavaScript;

using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.UI.Browser;

namespace SharpRail.Browser.Checks;

internal static partial class BrowserChecks
{
    [JSExport]
    public static async Task RepaintEmbeddedRoots()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windows = (IEnumerable<BrowserWindow>)typeof(BrowserWindows).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(BrowserWindows.Instance)!;
            foreach (var window in windows)
            {
                var view = (AvaloniaView)typeof(BrowserWindow).GetField("view", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                view.Content = new Border { Background = Brushes.Magenta };
                ((TopLevel)typeof(AvaloniaView).GetProperty("TopLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).InvalidateVisual();
            }
        });
    }
}