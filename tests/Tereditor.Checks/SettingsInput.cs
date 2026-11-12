using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;

using SharpRail.UI.Panels;

namespace SharpRail.Checks;

/// <summary>
/// Headless input for Settings, which is a control inside the workbench window's overlay rather than a window of its own:
/// keys and pointers go through the window, with points taken in Settings' own space.
/// </summary>
internal static class SettingsInput
{
    private static TopLevel Top(SettingsView settings) => TopLevel.GetTopLevel(settings)!;

    private static Point In(SettingsView settings, Point point) => settings.TranslatePoint(point, Top(settings))!.Value;

    // Keys reach the focused control; Settings takes focus when it is not already holding it.
    private static TopLevel Focused(SettingsView settings)
    {
        var top = Top(settings);
        if (top.FocusManager?.GetFocusedElement() is not Visual focus || focus != settings && !Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(focus).Contains(settings)) settings.Focus();
        return top;
    }

    internal static void KeyPress(this SettingsView settings, Key key, RawInputModifiers modifiers, PhysicalKey physical, string? symbol) => Focused(settings).KeyPress(key, modifiers, physical, symbol);
    internal static void KeyRelease(this SettingsView settings, Key key, RawInputModifiers modifiers, PhysicalKey physical, string? symbol) => Focused(settings).KeyRelease(key, modifiers, physical, symbol);
    internal static void KeyTextInput(this SettingsView settings, string text) => Focused(settings).KeyTextInput(text);
    internal static IHeadlessTouchPointer TouchBegin(this SettingsView settings, Point point, RawInputModifiers modifiers) => Top(settings).TouchBegin(In(settings, point), modifiers);
    internal static void TouchMove(this SettingsView settings, IHeadlessTouchPointer finger, Point point, RawInputModifiers modifiers) => Top(settings).TouchMove(finger, In(settings, point), modifiers);
    internal static void TouchEnd(this SettingsView settings, IHeadlessTouchPointer finger, Point point, RawInputModifiers modifiers) => Top(settings).TouchEnd(finger, In(settings, point), modifiers);
    internal static void MouseDown(this SettingsView settings, Point point, MouseButton button) => Top(settings).MouseDown(In(settings, point), button);
    internal static void MouseUp(this SettingsView settings, Point point, MouseButton button) => Top(settings).MouseUp(In(settings, point), button);
}