using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.UI;
using SharpRail.UI.Browser;

namespace SharpRail.Browser.Checks;

// Compiled only by the browser acceptance build, never by the shipped client.
internal static partial class BrowserChecks
{
    /// <summary>The shell the page shows: the phone one (drawer pages, bottom bar) or the desktop frame.</summary>
    [JSExport]
    public static async Task<string> PhoneState()
    {
        if (BrowserSession.Current?.Workbench is not { } workbench) return "null";
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = workbench.ActiveWindow;
            if (window is null) return "null";
            var bar = window.FindControl<Border>("PageBar");
            var settings = window.FindControl<Button>("SettingsButton");
            var layout = window.Layout.State;
            return JsonSerializer.Serialize(new
            {
                workbench.Compact,
                workbench.Touch,
                Windows = workbench.Windows.Count,
                BarVisible = bar?.IsVisible ?? false,
                BarHeight = bar is { IsVisible: true } ? bar.Bounds.Height : 0,
                BarBottom = bar is { IsVisible: true } ? bar.TranslatePoint(new Avalonia.Point(0, bar.Bounds.Height), window)?.Y : null,
                SettingsVisible = settings?.IsVisible ?? false,
                Width = window.ClientSize.Width,
                Height = window.ClientSize.Height,
                layout.ActiveWorkspace,
                Selected = window.Layout.Selected(window.Layout.View.FocusedCenter)?.Id,
                Left = layout.LeftVisible,
                Right = layout.RightVisible
            });
        });
    }

    /// <summary>The pure breakpoint rule, for the cases a headless viewport cannot reach.</summary>
    [JSExport]
    public static bool PhoneDecision(bool current, double width, bool coarse, double smallestScreenSide) =>
        PhoneLayout.Decide(current, width, coarse, smallestScreenSide);
}
