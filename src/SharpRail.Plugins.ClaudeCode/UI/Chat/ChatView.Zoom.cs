using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Visualization;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

/// <summary>
/// A chat's own zoom, like a terminal's font size and unlike the page zoom: pinch, Ctrl or Cmd with the wheel, and Mod+=, Mod+- and Mod+0
/// while the chat has focus. The whole chat sits in a layout transform, so the list, panels and composer reflow at the scaled width (layouts are
/// cached by width, which carries the scale), and hit rectangles, the reading band and selections stay in the chat's own units.
/// </summary>
internal sealed partial class ChatView
{
    internal const double MinZoom = 0.6, MaxZoom = 2.5;
    private static readonly double[] ZoomSteps = [0.6, 0.7, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5];
    private const int ZoomsKept = 300;

    private LayoutTransformControl zoomHost = null!;
    private Grid content = null!;
    private TextBlock zoomLabel = null!;
    private DispatcherTimer? zoomLabelTimer;
    private double zoom = 1;

    /// <summary>This chat's zoom factor, 1 being the normal size.</summary>
    internal double Zoom => zoom;

    internal string ZoomLabelText => zoomLabel.Text ?? "";

    // The content grid goes inside the transform; the level label floats over it, outside, so showing it moves nothing.
    private void InitZoom()
    {
        content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto") };
        zoom = StoredZoom();
        zoomHost = new LayoutTransformControl { Name = "ChatZoomHost", Child = content, LayoutTransform = new ScaleTransform(zoom, zoom) };
        zoomLabel = new TextBlock { Name = "ChatZoomLabel", FontSize = 12, Foreground = Ui.TextBrush, Margin = new Thickness(Space.Inset, Space.Gap) };
        var plate = new Border { Background = Ui.Elevated, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = zoomLabel, IsHitTestVisible = false, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Space.Inset), ZIndex = 30, Name = "ChatZoomPlate" };
        zoomPlate = plate;
        Children.Add(zoomHost);
        Children.Add(plate);
        ZoomChords.SetOwns(this, true);
        AddHandler(KeyDownEvent, ZoomKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!ZoomGesture.IsZoom(e.KeyModifiers)) return;
            WheelZoom(e.Delta.Y);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) => { MagnifyZoom(e.Delta.X); e.Handled = true; });
        if (OperatingSystem.IsAndroid())
        {
            GestureRecognizers.Add(new PinchGestureRecognizer());
            double? pinchStart = null;
            AddHandler(PinchEvent, (_, e) => { pinchStart ??= zoom; SetZoom(Snap(pinchStart.Value * e.Scale)); e.Handled = true; });
            AddHandler(PinchEndedEvent, (_, _) => pinchStart = null);
        }
    }

    private Border zoomPlate = null!;

    private static double Snap(double value) => Math.Round(Math.Clamp(value, MinZoom, MaxZoom) * 20) / 20;

    private double StoredZoom()
    {
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, double>>(env.ZoomPreference.Get() ?? "{}");
            return all is not null && all.TryGetValue(chatId, out var stored) && double.IsFinite(stored) ? Math.Clamp(stored, MinZoom, MaxZoom) : 1;
        }
        catch (JsonException) { return 1; }
    }

    private void StoreZoom()
    {
        Dictionary<string, double> all;
        try { all = JsonSerializer.Deserialize<Dictionary<string, double>>(env.ZoomPreference.Get() ?? "{}") ?? []; }
        catch (JsonException) { all = []; }
        if (zoom == 1) all.Remove(chatId); else all[chatId] = zoom;
        while (all.Count > ZoomsKept) all.Remove(all.Keys.First());
        env.ZoomPreference.Set(JsonSerializer.Serialize(all));
    }

    /// <summary>Sets this chat's zoom (clamped), remembers it for this chat id and shows the level for a moment.</summary>
    internal void SetZoom(double value)
    {
        var next = Math.Clamp(value, MinZoom, MaxZoom);
        if (next == zoom) { ShowZoomLevel(); return; }
        zoom = next;
        zoomHost.LayoutTransform = new ScaleTransform(zoom, zoom);
        StoreZoom();
        ShowZoomLevel();
    }

    /// <summary>The adjacent step in the direction given; 0 puts the chat back to 100%.</summary>
    internal void StepZoom(int step) => SetZoom(step == 0 ? 1
        : step > 0 ? ZoomSteps.FirstOrDefault(factor => factor > zoom + 0.001, zoom) : ZoomSteps.LastOrDefault(factor => factor < zoom - 0.001, zoom));

    internal void WheelZoom(double lines) => SetZoom(Snap(ZoomGesture.ForWheel(zoom, lines)));

    internal void MagnifyZoom(double delta) => SetZoom(Snap(zoom * (1 + delta)));

    private void ShowZoomLevel()
    {
        zoomLabel.Text = $"{Math.Round(zoom * 100)}%";
        zoomPlate.IsVisible = true;
        zoomLabelTimer?.Stop();
        zoomLabelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        zoomLabelTimer.Tick += (_, _) => { zoomLabelTimer?.Stop(); zoomPlate.IsVisible = false; };
        zoomLabelTimer.Start();
    }

    internal bool ZoomPlateVisible => zoomPlate.IsVisible;

    private void ZoomKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers is not (KeyModifiers.Control or KeyModifiers.Meta or (KeyModifiers.Control | KeyModifiers.Shift) or (KeyModifiers.Meta | KeyModifiers.Shift))) return;
        var step = e.Key is Key.OemPlus or Key.Add ? 1 : e.Key is Key.OemMinus or Key.Subtract ? -1 : e.Key is Key.D0 or Key.NumPad0 ? 0 : (int?)null;
        if (step is not { } direction) return;
        StepZoom(direction);
        e.Handled = true;
    }
}