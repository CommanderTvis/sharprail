using Avalonia.VisualTree;

using Tereditor.UI.State;

namespace Tereditor.UI;

/// <summary>
/// Which shell an Android client's one window wears: the phone pages (compact) or the desktop frame (wide), chosen from
/// the window's size in dp and changed live as the window is resized, turned, unfolded or moved to another display.
/// The shell is fixed for a workbench's life, so a change rebuilds the workbench over the same profile and host.
/// </summary>
public sealed class ShellSwitch(bool compact)
{
    /// <summary>The narrowest window, in dp, that gets the desktop frame (Material's medium width): a portrait tablet.</summary>
    public const double WideWidth = 600;
    /// <summary>
    /// The lowest window, in dp, that gets the desktop frame on a phone-sized display, so a phone held sideways
    /// (915×412, 851×393, 780×360 dp) stays compact although it is wider than <see cref="WideWidth"/>.
    /// </summary>
    public const double WideHeight = 480;
    /// <summary>
    /// The smallest side, in dp, of a display that is not a phone's own screen: a tablet's, or the external monitor
    /// Android's desktop mode puts windows on. Phones' own screens are narrower.
    /// </summary>
    public const double LargeDisplay = 600;
    /// <summary>
    /// The lowest window, in dp, that gets the desktop frame on a <see cref="LargeDisplay"/>: a 1280×720 px
    /// desktop-mode window at density 2 is 640×360 dp, and a tablet's split screen is short too.
    /// </summary>
    public const double LargeDisplayHeight = 360;
    /// <summary>
    /// How far, in dp, a wide window must shrink below a threshold before it turns compact, so a desktop-mode window
    /// dragged around the threshold does not rebuild the workbench on every step.
    /// </summary>
    public const double Hysteresis = 32;

    private (bool Left, bool Right)? wideSides;

    /// <summary>Whether the workbench is, or is next, composed compact.</summary>
    public bool Compact { get; private set; } = compact;

    /// <summary>The shell a window of this size, on a display whose smallest side is <paramref name="display"/> dp, starts with.</summary>
    public static ShellSwitch For(double width, double height, double display) => new(Decide(null, width, height, display));

    /// <summary>
    /// Wide when the window is at least <see cref="WideWidth"/> dp wide and at least <see cref="WideHeight"/> dp high,
    /// or <see cref="LargeDisplayHeight"/> dp high on a display whose smallest side (<paramref name="display"/>, in
    /// dp; 0 when unknown) is at least <see cref="LargeDisplay"/>. So a phone on its own screen is compact in either
    /// orientation, a tablet is wide in either, a desktop-mode window on a monitor is wide from 1280×720 px, and a
    /// window narrower than <see cref="WideWidth"/> is compact anywhere. A wide shell stays until the window falls
    /// below a threshold by <see cref="Hysteresis"/>; an unknown window size keeps the current shell.
    /// </summary>
    public static bool Decide(bool? current, double width, double height, double display)
    {
        if (width <= 0 || height <= 0) return current ?? true;
        var margin = current == false ? Hysteresis : 0;
        var floor = display >= LargeDisplay ? LargeDisplayHeight : WideHeight;
        return width < WideWidth - margin || height < floor - margin;
    }

    /// <summary>Follows a size change while no workbench is open, so the next one opens in the shell that fits.</summary>
    public void Fit(double width, double height, double display) => Compact = Decide(Compact, width, height, display);

    /// <summary>Whether a window resized to this size needs the other shell.</summary>
    public bool Wants(double width, double height, double display) => Decide(Compact, width, height, display) != Compact;

    /// <summary>
    /// Whether rebuilding now would raise a question: closing the window asks about unsaved edits, so the swap waits
    /// until none is left. Agents and reported processes run on the host and survive the reopen.
    /// </summary>
    public static bool Busy(Workbench workbench) =>
        workbench.Windows.Any(window => window.GetVisualDescendants().OfType<Editor.CodeDocumentView>().Any(view => view.HasPendingChanges));

    /// <summary>
    /// Lets the workbench close for a swap without asking to quit: the agents mid-turn and the reported processes
    /// live on the host, and the reopened workbench finds them again.
    /// </summary>
    public static void Release(Workbench workbench) => workbench.ExitConfirmed = true;

    /// <summary>
    /// Records the swap to the other shell before the workbench closes. The phone pages hide both side panels whenever
    /// the current page shows, so the side panels the desktop frame showed are kept to restore later.
    /// </summary>
    public void Swap(WindowProfile slot)
    {
        if (!Compact) wideSides = (slot.Layout.LeftVisible, slot.Layout.RightVisible);
        Compact = !Compact;
    }

    /// <summary>
    /// Prepares the profile's window for the shell it opens in. The desktop frame gets back the side panels it last
    /// showed; with none remembered, a frame the phone pages left with both hidden shows both again.
    /// </summary>
    public void Prepare(WindowProfile slot)
    {
        if (Compact) return;
        var layout = slot.Layout;
        if (wideSides is { } sides) (layout.LeftVisible, layout.RightVisible) = sides;
        else if (!layout.LeftVisible && !layout.RightVisible) layout.LeftVisible = layout.RightVisible = true;
    }
}