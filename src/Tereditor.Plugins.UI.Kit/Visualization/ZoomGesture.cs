using Avalonia.Input;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>Zoom clamping and gesture math shared by zoomable views, so every one zooms at the same pace and bounds.</summary>
public static class ZoomGesture
{
    /// <summary>The smallest presented scale.</summary>
    public const double MinScale = 0.25;
    /// <summary>The largest presented scale.</summary>
    public const double MaxScale = 6;
    /// <summary>The multiplicative step for toolbar zoom buttons.</summary>
    public const double ScaleStep = 1.15;

    // How much wheel travel makes one factor of e: bigger is slower. A trackpad sends a stream of small deltas, so
    // the zoom follows their size; a mouse notch is one step, capped so it does not leap.
    private const double Sensitivity = 100;
    private const double MaxDelta = 12;
    private const double PixelsPerLine = 16;

    /// <summary>Bounds a requested scale to the shared presentation limits.</summary>
    public static double Clamp(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>Where a wheel gesture leaves the zoom; a positive delta (scrolling up, pinching open) zooms in.</summary>
    public static double ForWheel(double scale, double deltaLines) =>
        Clamp(scale * Math.Exp(Math.Clamp(deltaLines * PixelsPerLine, -MaxDelta, MaxDelta) / Sensitivity));

    /// <summary>Whether a wheel event zooms rather than scrolls: Control or Command held, as macOS also marks a pinch.</summary>
    public static bool IsZoom(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
}