using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// Lets a view that zooms itself (a chat) keep Mod+=, Mod+- and Mod+0 while it has focus: the window's page-zoom chords stand aside for it.
/// </summary>
public static class ZoomChords
{
    public static readonly AttachedProperty<bool> OwnsProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Owns", typeof(ZoomChords));

    public static void SetOwns(Control control, bool value) => control.SetValue(OwnsProperty, value);

    public static bool GetOwns(Control control) => control.GetValue(OwnsProperty);

    /// <summary>Whether the keyboard focus is inside a view that owns the zoom chords.</summary>
    public static bool FocusOwned(TopLevel? top) =>
        top?.FocusManager?.GetFocusedElement() is Visual focused && focused.GetSelfAndVisualAncestors().OfType<Control>().Any(GetOwns);
}