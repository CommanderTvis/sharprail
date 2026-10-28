using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.UI.Browser;
using SharpRail.Host.Abstractions;

namespace SharpRail.Browser.Checks;

internal static partial class BrowserChecks
{
    [JSExport]
    public static async Task RequestWorkbenchSize(double width, double height)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = BrowserSession.Current!.Workbench!.ActiveWindow!;
            window.Width = width; window.Height = height;
        });
    }

    [JSExport]
    public static async Task<string> SettingsGeometry()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = BrowserSession.Current!.Workbench!.ActiveWindow!;
            var gear = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsButton");
            var point = gear.TranslatePoint(default, window)!.Value;
            return JsonSerializer.Serialize(new
            {
                Width = window.ClientSize.Width,
                Height = window.ClientSize.Height,
                window.RenderScaling,
                Gear = new { point.X, point.Y, Width = gear.TranslatePoint(new Point(gear.Bounds.Width, 0), window)!.Value.X - point.X, Height = gear.TranslatePoint(new Point(0, gear.Bounds.Height), window)!.Value.Y - point.Y },
                Dialogs = window.GetVisualDescendants().OfType<SharpRail.UI.Panels.SettingsView>().Select(dialog =>
                {
                    var close = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsClose");
                    var position = close.TranslatePoint(default, dialog)!.Value;
                    var end = close.TranslatePoint(new Point(close.Bounds.Width, close.Bounds.Height), dialog)!.Value;
                    return new
                    {
                        Width = dialog.Bounds.Width,
                        Height = dialog.Bounds.Height,
                        window.RenderScaling,
                        Close = new { position.X, position.Y, Width = end.X - position.X, Height = end.Y - position.Y }
                    };
                }).ToArray()
            });
        });
    }

    [JSExport]
    public static async Task<bool> SettingsRetainPageOnHostUpdate()
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = BrowserSession.Current!.Workbench!.ActiveWindow!;
            var dialog = window.GetVisualDescendants().OfType<SharpRail.UI.Panels.SettingsView>().Single();
            var body = dialog.GetVisualDescendants().OfType<ContentControl>().Single(control => control.Name == "SettingsBody");
            return (Window: window, Body: body, Content: body.Content);
        });
        var title = "Settings activity " + Guid.NewGuid().ToString("N");
        await snapshot.Window.Workbench.State.ChangeAsync(HostStateChange.TitleTerminal(new(snapshot.Window.WorkspaceRoot, "terminal:restty-check"), title));
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var result = await Dispatcher.UIThread.InvokeAsync(() =>
                snapshot.Window.Workbench.State.Current.TerminalTitles.Any(item => item.Title == title)
                    ? (bool?)ReferenceEquals(snapshot.Body.Content, snapshot.Content) : null);
            if (result is { } retained) return retained;
            await Task.Delay(25);
        }
        throw new TimeoutException("Terminal title broadcast did not arrive.");
    }

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