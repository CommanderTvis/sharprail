using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.UI.Browser;

namespace SharpRail.Browser.Checks;

internal static partial class BrowserChecks
{
    [JSExport]
    public static async Task<string> ConnectStatus()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windows = (IEnumerable<BrowserWindow>)typeof(BrowserWindows).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(BrowserWindows.Instance)!;
            var window = windows.Select(item => item.Window).OfType<ConnectWindow>().FirstOrDefault();
            if (window is null) return JsonSerializer.Serialize(new { Connected = BrowserSession.Current?.Workbench?.State.Connected == true });
            var submit = window.FindControl<Button>("ConnectSubmit")!;
            return JsonSerializer.Serialize(new
            {
                Enabled = submit.IsEnabled,
                Button = submit.Content?.ToString(),
                Position = submit.TranslatePoint(default, window),
                Error = window.FindControl<TextBlock>("ConnectError")!.IsVisible ? window.FindControl<TextBlock>("ConnectError")!.Text : null,
                Pending = window.FindControl<TextBlock>("ConnectProgress")!.IsVisible,
                LocalHint = window.FindControl<TextBlock>("ConnectLocalHint")!.IsVisible
            });
        });
    }

}