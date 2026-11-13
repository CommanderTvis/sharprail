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
    /// <summary>The narrowest window, in dp, that gets the desktop frame (Material's expanded width).</summary>
    public const double WideWidth = 840;
    /// <summary>The lowest window, in dp, that gets the desktop frame; a phone held sideways stays compact.</summary>
    public const double WideHeight = 480;
    /// <summary>A desktop-mode window dragged around the threshold must not rebuild the workbench on every step.</summary>
    public const double Hysteresis = 40;

    private (bool Left, bool Right)? wideSides;

    /// <summary>Whether the workbench is, or is next, composed compact.</summary>
    public bool Compact { get; private set; } = compact;

    /// <summary>The shell a window of this size starts with.</summary>
    public static ShellSwitch For(double width, double height) => new(Decide(null, width, height));

    /// <summary>
    /// Wide when the window is at least <see cref="WideWidth"/> by <see cref="WideHeight"/> dp. A compact shell stays
    /// until both clear the threshold by <see cref="Hysteresis"/>; an unknown size keeps the current shell.
    /// </summary>
    public static bool Decide(bool? current, double width, double height)
    {
        if (width <= 0 || height <= 0) return current ?? true;
        var margin = current == true ? Hysteresis : 0;
        return width < WideWidth + margin || height < WideHeight + margin;
    }

    /// <summary>Follows a size change while no workbench is open, so the next one opens in the shell that fits.</summary>
    public void Fit(double width, double height) => Compact = Decide(Compact, width, height);

    /// <summary>Whether a window resized to this size needs the other shell.</summary>
    public bool Wants(double width, double height) => Decide(Compact, width, height) != Compact;

    /// <summary>
    /// Whether rebuilding now would raise a question: closing the window asks about unsaved edits, and about agents
    /// mid-turn or reported processes. A resize must not, so the swap waits until none is left.
    /// </summary>
    public static bool Busy(Workbench workbench) =>
        workbench.AgentInProgress || workbench.State.Current.OwnedProcesses.Count > 0 ||
        workbench.Windows.Any(window => window.GetVisualDescendants().OfType<Editor.CodeDocumentView>().Any(view => view.HasPendingChanges));

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