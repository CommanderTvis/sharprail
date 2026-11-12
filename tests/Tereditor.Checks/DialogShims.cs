using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Checks;

/// <summary>
/// A dialog shown in its owner's overlay is never a window of its own, so its card is not under the dialog's window. These
/// extensions, found before Avalonia's for any control, look through to the card (and send keys to the owner),
/// so checks read an overlay dialog and an owned one alike.
/// </summary>
internal static class DialogShims
{
    private static Control Root(Control control) => control is DialogWindow { Surface: { } surface } ? surface : control;

    public static IEnumerable<ILogical> GetLogicalDescendants(this Control window) => LogicalExtensions.GetLogicalDescendants(Root(window));

    public static IEnumerable<Visual> GetVisualDescendants(this Control window) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Root(window));

    public static void KeyPress(this Window window, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? keySymbol) =>
        HeadlessWindowExtensions.KeyPress(window is DialogWindow { Host: { } host } ? host : window, key, modifiers, physicalKey, keySymbol);

    /// <summary>Whether a dialog is showing, in its overlay or as a window.</summary>
    public static bool IsShown(this Window window) => window is DialogWindow dialog ? dialog.IsVisible : window.IsVisible;

    /// <summary>Closes a dialog whichever way it is shown.</summary>
    public static void Dismiss(this Window window)
    {
        if (window is DialogWindow dialog) dialog.Close(); else window.Close();
    }
}

internal static class DialogShimResults
{
    /// <summary>Closes a dialog with <paramref name="result"/> whichever way it is shown.</summary>
    public static void Dismiss(this Window window, object? result)
    {
        if (window is DialogWindow dialog) dialog.Close(result); else window.Close(result);
    }
}