using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>The chip family every terminal accessory row draws from: one height, radius, type size, padding and fill.</summary>
public static class Chips
{
    /// <summary>The 11 px muted label inside a chip.</summary>
    public static TextBlock Label(string text) => Ui.Text(text, Ui.Muted, 11);

    /// <summary>A control with the tooltip and accessible name of a chip.</summary>
    public static T Tip<T>(T control, string tooltip) where T : Control
    {
        ToolTip.SetTip(control, tooltip);
        AutomationProperties.SetName(control, tooltip);
        return control;
    }

    /// <summary>Space after a chip and below its row, so rows and wrapped lines sit on one grid.</summary>
    public static Thickness Gap => new(0, 0, LayoutRoles.ChipGap, LayoutRoles.ChipGap);

    /// <summary>A chip-shaped button, the trigger every interactive chip uses.</summary>
    public static Button Button(string name, Control content, string tooltip, Action onClick)
    {
        var button = Tip(new Button
        {
            Name = name,
            Content = content,
            Padding = new Thickness(4, 2),
            MinHeight = 0,
            Background = Ui.Elevated,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        }, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }
}