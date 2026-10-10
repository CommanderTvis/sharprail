using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SharpRail.UI.Android;

/// <summary>
/// A finger that travels is scrolling or flicking, not pressing: a button under it stops being pressed once the
/// finger has moved past the slop, so lifting it there does not click. A mouse keeps the desktop rule.
/// </summary>
internal static class TouchClicks
{
    private const double Slop = 12;

    public static void Install()
    {
        Point? pressed = null;
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((top, e) =>
            pressed = e.Pointer.Type == PointerType.Touch ? e.GetPosition(top) : null, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((top, e) =>
        {
            if (pressed is not { } start || e.Pointer.Type != PointerType.Touch) return;
            var moved = e.GetPosition(top) - start;
            if (Math.Abs(moved.X) < Slop && Math.Abs(moved.Y) < Slop) return;
            pressed = null;
            if ((e.Pointer.Captured as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) e.Pointer.Capture(null);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}