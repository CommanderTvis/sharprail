using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Tereditor.UI;

/// <summary>
/// The system caption bar of an Android desktop window, which the header draws into instead of sitting below it: its
/// height and the room the system's own controls (the app chip at the left, minimize, maximize and close at the right)
/// take at each side, in dp. The caption does not zoom, like the traffic lights on a Mac.
/// </summary>
public readonly record struct CaptionBar(double Height, double Left, double Right)
{
    /// <summary>
    /// The caption bar from its height and its controls' bounding rects (left and right edges), in px across a window
    /// <paramref name="width"/> px wide, at <paramref name="scaling"/> px per dp. A control left of the centre line
    /// reserves room at the left, one right of it at the right. Null when there is no caption, when it reports no
    /// controls (their place is unknown), or when a rect crosses the centre line: that is a caption with no room to
    /// draw into, so the header stays below it.
    /// </summary>
    public static CaptionBar? FromPixels(int height, int width, IEnumerable<(int Left, int Right)> controls, double scaling)
    {
        if (height <= 0 || width <= 0 || scaling <= 0) return null;
        double centre = width / 2.0, left = 0, right = 0;
        var any = false;
        foreach (var (start, end) in controls)
        {
            if (end <= start) continue;
            any = true;
            if (start < centre && end > centre) return null;
            if (end <= centre) left = Math.Max(left, end);
            else right = Math.Max(right, width - start);
        }
        return any ? new(height / scaling, left / scaling, right / scaling) : null;
    }

    /// <summary>
    /// The header's horizontal margins at <paramref name="zoom"/>: the caption's controls keep their physical size, so
    /// their room shrinks in the zoomed header as it zooms in, and the header's ordinary 12 px margin stays at least.
    /// </summary>
    public Thickness HeaderMargin(double zoom) => new(Math.Max(12, Left / zoom), 0, Math.Max(12, Right / zoom), 0);

    /// <summary>The header row's height at <paramref name="zoom"/>: never shorter than the caption, nor than its usual height.</summary>
    public double RowHeight(double zoom, double usual) => Math.Max(usual, Height / zoom);

    /// <summary>
    /// The parts of the caption the header's controls cover, in window coordinates: <paramref name="targets"/> cut to
    /// the caption's height (<paramref name="height"/>, in the same coordinates). Only these take touches from the
    /// system; the rest of the caption stays the system's, which drags the window.
    /// </summary>
    public static IReadOnlyList<Rect> Claimed(IEnumerable<Rect> targets, double height)
    {
        var caption = new Rect(0, 0, double.MaxValue, Math.Max(0, height));
        return [.. targets.Select(target => target.Intersect(caption)).Where(part => part.Width > 0 && part.Height > 0)];
    }
}

public sealed partial class WorkbenchWindow
{
    private CaptionBar? captionBar;
    private IReadOnlyList<Rect> claimedCaption = [];
    private bool? lightCaption;

    /// <summary>
    /// Whether this window's header can be the title bar of an Android desktop window: the desktop frame's header can,
    /// the phone pages have none.
    /// </summary>
    internal bool DrawsIntoCaption => !unified && !workbench.Compact;

    /// <summary>The caption bar the header draws into; null keeps the header below the system's title bar.</summary>
    internal CaptionBar? Caption
    {
        get => captionBar;
        set
        {
            if (captionBar == value) return;
            var first = captionBar is null;
            captionBar = value;
            if (first && value is not null) Ui.ThemeChanged += ReportCaptionTheme;
            if (value is null) Ui.ThemeChanged -= ReportCaptionTheme;
            claimedCaption = []; lightCaption = null;
            ApplyChromeZoom();
            ReportCaption();
        }
    }

    /// <summary>
    /// Told the parts of the caption the header's controls cover (window coordinates) whenever they change, so they take
    /// touches from the system while the blank header still drags the window.
    /// </summary>
    internal Action<IReadOnlyList<Rect>>? CaptionClaimed { get; set; }

    /// <summary>Told whether the header is light whenever that changes, so the system's caption controls stay readable on it.</summary>
    internal Action<bool>? CaptionLight { get; set; }

    /// <summary>
    /// The header's buttons and text boxes, in window coordinates: what <see cref="LinuxWindowChrome.StartsMove"/> keeps
    /// clickable, so a press anywhere else in the header is a title-bar press.
    /// </summary>
    internal IEnumerable<Rect> HeaderTargets()
    {
        var header = this.FindControl<Grid>("MainHeader")!;
        foreach (var control in header.GetVisualDescendants().OfType<Control>())
        {
            if (control is not (Button or TextBox) || !control.IsEffectivelyVisible || !control.IsHitTestVisible || !control.IsEffectivelyEnabled) continue;
            if (control.GetVisualAncestors().TakeWhile(ancestor => ancestor != header).Any(ancestor => ancestor is Button or TextBox)) continue;
            if (control.TransformToVisual(this) is not { } transform) continue;
            var bounds = new Rect(control.Bounds.Size).TransformToAABB(transform);
            if (bounds.Width > 0 && bounds.Height > 0) yield return bounds;
        }
    }

    private void ReportCaption()
    {
        if (captionBar is not { } bar) return;
        ReportCaptionTheme();
        var claimed = CaptionBar.Claimed(HeaderTargets(), bar.Height);
        if (claimed.SequenceEqual(claimedCaption)) return;
        claimedCaption = claimed;
        CaptionClaimed?.Invoke(claimed);
    }

    private void ReportCaptionTheme()
    {
        var color = Ui.Header.Color;
        var light = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255 > 0.5;
        if (light == lightCaption) return;
        lightCaption = light;
        CaptionLight?.Invoke(light);
    }
}
