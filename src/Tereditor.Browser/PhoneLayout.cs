namespace SharpRail.UI.Browser;

/// <summary>Decides whether the page gets the phone shell (the Android client's drawer pages) or the desktop frame.</summary>
internal static class PhoneLayout
{
    /// <summary>CSS pixels; the width Android's compact workbench starts at.</summary>
    public const double Breakpoint = 600;
    /// <summary>A desktop window dragged around the breakpoint must not rebuild the workbench on every pixel.</summary>
    public const double Hysteresis = 40;

    /// <summary>
    /// A touch screen whose smallest side is under the breakpoint is a phone in either orientation (as on Android).
    /// Otherwise a layout viewport under the breakpoint is one; a phone shell stays until the width clears the breakpoint
    /// by the hysteresis.
    /// </summary>
    public static bool Decide(bool current, double width, bool coarsePointer, double smallestScreenSide)
    {
        if (coarsePointer && smallestScreenSide > 0 && smallestScreenSide < Breakpoint) return true;
        return width < (current ? Breakpoint + Hysteresis : Breakpoint);
    }

    internal static bool Read(bool current) => Decide(current, BrowserInterop.ViewportWidth(), BrowserInterop.IsCoarse(), BrowserInterop.SmallestScreenSide());
}
