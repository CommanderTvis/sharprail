using Avalonia.Controls;

namespace SharpRail.UI.Rendering;

/// <summary>
/// Fluent paints control states from its own palette. These resource keys point the states it would
/// otherwise own at roles, once for the whole application, so no control restyles its own disabled,
/// pressed or focused look.
/// </summary>
internal static class ControlStates
{
    internal static void Apply(IResourceDictionary resources)
    {
        foreach (var control in new[] { "Button", "ToggleButton", "TextControl", "ComboBox" })
        {
            resources[control + "BackgroundDisabled"] = Ui.ControlDisabledFill;
            resources[control + "ForegroundDisabled"] = Ui.ControlDisabledText;
            resources[control + "BorderBrushDisabled"] = Ui.ControlDisabledBorder;
        }
        resources["ToggleButtonBackgroundCheckedDisabled"] = Ui.ControlDisabledFill;
        resources["ToggleButtonForegroundCheckedDisabled"] = Ui.ControlDisabledText;
        resources["ToggleButtonBorderBrushCheckedDisabled"] = Ui.ControlDisabledBorder;
        resources["TextControlPlaceholderForegroundDisabled"] = Ui.ControlDisabledText;
        resources["ComboBoxDropDownGlyphForegroundDisabled"] = Ui.ControlDisabledText;
        resources["MenuFlyoutItemForegroundDisabled"] = Ui.ControlDisabledText;

        // Inputs rest on the control fill inside the quiet border.
        foreach (var state in new[] { "", "PointerOver", "Focused" }) resources["TextControlBackground" + state] = Ui.ControlFill;
        foreach (var state in new[] { "", "PointerOver" }) resources["TextControlBorderBrush" + state] = Ui.ControlBorder;

        // Editing, pressing and opening strengthen the border neutrally rather than switching to the accent.
        resources["TextControlBorderBrushFocused"] = Ui.ControlBorderActive;
        resources["ButtonBorderBrushPressed"] = Ui.ControlBorderActive;
        resources["ToggleButtonBorderBrushPressed"] = Ui.ControlBorderActive;
        resources["ComboBoxBorderBrushPressed"] = Ui.ControlBorderActive;
        resources["ComboBoxBackgroundBorderBrushFocused"] = Ui.ControlBorderActive;
    }
}