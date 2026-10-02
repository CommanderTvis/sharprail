using Avalonia;
using Avalonia.Controls;

namespace Ghostty.Avalonia;

internal static class PixelScale
{
    /// <summary>
    /// Device pixels per layout unit: the window's render scaling times any ancestor scale (a zoomed
    /// layout transform), so terminals rasterize at the density they are shown instead of being resampled.
    /// </summary>
    internal static double Of(Visual visual)
    {
        if (TopLevel.GetTopLevel(visual) is not { } top) return 1;
        var zoom = visual.TransformToVisual(top) is { } m ? Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12) : 1;
        return top.RenderScaling * (zoom > 0 ? zoom : 1);
    }
}