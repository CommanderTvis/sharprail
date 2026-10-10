using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>Activating a list or tree row the way each pointer expects.</summary>
public static class RowActivation
{
    /// <summary>
    /// Calls <paramref name="action"/> with the click count when a mouse presses <paramref name="row"/>, and with
    /// one when a finger taps it: a finger's press also begins a scroll, so only its tap is a choice.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="accepts">Whether a press or tap on this part of the row activates it.</param>
    /// <param name="action">What activation does.</param>
    public static void OnActivate(Control row, Func<Control, bool> accepts, Action<int> action)
    {
        row.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Touch || e.Source is not Control source ||
                !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed || !accepts(source)) return;
            action(e.ClickCount);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        row.AddHandler(InputElement.TappedEvent, (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Touch && e.Source is Control source && accepts(source)) action(1);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}