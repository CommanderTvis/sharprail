using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

using SharpRail.UI.Rendering;

namespace SharpRail.UI;

/// <summary>What a double-click on the title bar does, following the macOS preference.</summary>
public enum TitleBarAction { Zoom, Minimize, None }

/// <summary>The custom title bar's platform manners: the double-click preference and trackpad pinch zoom.</summary>
public sealed partial class WorkbenchWindow
{
    private readonly DispatcherTimer pinchEnd = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double? pinchStart;
    private double pinchScale;
    private bool titleBarPending;

    /// <summary>An unset preference zooms, as do Maximize and Fill; Minimize minimizes; anything else does nothing.</summary>
    public static TitleBarAction TitleBarActionFor(string? preference) => preference switch
    {
        null or "Maximize" or "Fill" => TitleBarAction.Zoom,
        "Minimize" => TitleBarAction.Minimize,
        _ => TitleBarAction.None
    };

    /// <summary>Applies a title-bar double-click with the given <c>AppleActionOnDoubleClick</c> value; full screen ignores it.</summary>
    public void TitleBarDoubleClick(string? preference)
    {
        if (WindowState == WindowState.FullScreen) return;
        var action = TitleBarActionFor(preference);
        if (action == TitleBarAction.Zoom) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (action == TitleBarAction.Minimize) WindowState = WindowState.Minimized;
    }

    /// <summary>The header covers the native strip, so the window reads the preference itself, afresh on each double-click.</summary>
    private async void TitleBarDoubleClicked()
    {
        if (!OperatingSystem.IsMacOS() || TryGetPlatformHandle() is not { Handle: not 0 }) { TitleBarDoubleClick(null); return; }
        if (titleBarPending) return;
        titleBarPending = true;
        try { TitleBarDoubleClick(await Task.Run(ReadTitleBarPreference)); }
        finally { titleBarPending = false; }
    }

    private static string? ReadTitleBarPreference()
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/defaults") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "read", "-g", "AppleActionOnDoubleClick" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    private void WirePinchZoom()
    {
        // Bubbling: content that claims the gesture (a diagram viewer) keeps it.
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) => { PinchZoom(e.Delta.X); e.Handled = true; });
        pinchEnd.Tick += (_, _) => { pinchEnd.Stop(); pinchStart = null; SaveProfile(); };
        Closed += (_, _) => pinchEnd.Stop();
    }

    /// <summary>
    /// One step of a trackpad pinch. The factor is captured when the gesture starts and the accumulated scale
    /// is applied against that baseline, so updates never compound; the gesture ends when the steps stop.
    /// </summary>
    public void PinchZoom(double delta)
    {
        if (!double.IsFinite(delta)) return;
        if (pinchStart is null) { pinchStart = Preferences.Zoom; pinchScale = 1; }
        pinchScale += delta;
        pinchEnd.Stop(); pinchEnd.Start();
        var factor = InterfaceZoom.ForGesture(pinchStart.Value, pinchScale);
        if (factor == Preferences.Zoom) return;
        Preferences.Zoom = factor;
        if (Application.Current is { } app) InterfaceZoom.Apply(app, factor);
    }
}