using Avalonia.Input;

namespace Ghostty.Avalonia;

/// <summary>Maps the pointer shapes a program requests with OSC 22 (Ghostty's mouse shape numbers) to Avalonia cursors.</summary>
internal static class TerminalPointer
{
    /// <summary>Ghostty's text shape, the terminal's default.</summary>
    internal const int Text = 8;

    internal static StandardCursorType For(int shape) => shape switch
    {
        0 or 1 => StandardCursorType.Arrow,
        2 => StandardCursorType.Help,
        3 => StandardCursorType.Hand,
        4 => StandardCursorType.AppStarting,
        5 => StandardCursorType.Wait,
        6 or 7 => StandardCursorType.Cross,
        10 => StandardCursorType.DragLink,
        11 => StandardCursorType.DragCopy,
        12 or 15 or 16 => StandardCursorType.DragMove,
        13 or 14 => StandardCursorType.No,
        17 => StandardCursorType.SizeAll,
        18 or 21 or 23 or 28 => StandardCursorType.SizeWestEast,
        19 or 20 or 22 or 29 => StandardCursorType.SizeNorthSouth,
        24 => StandardCursorType.TopRightCorner,
        25 => StandardCursorType.TopLeftCorner,
        26 => StandardCursorType.BottomRightCorner,
        27 => StandardCursorType.BottomLeftCorner,
        30 => StandardCursorType.TopRightCorner,
        31 => StandardCursorType.TopLeftCorner,
        // Text, vertical text, zoom and unknown shapes keep the terminal's I-beam.
        _ => StandardCursorType.Ibeam
    };
}