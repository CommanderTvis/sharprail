using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// One semantic text style from Rendering/Design/typography.json. Controls take it by its <see cref="Class"/>
/// (<c>Classes="tr-title-section"</c>) or through <see cref="Typography.Apply"/>; no call site writes a size,
/// weight or line height of its own.
/// </summary>
/// <param name="Class">The style class, such as <c>tr-text-metadata</c>.</param>
/// <param name="FontFamily">The face.</param>
/// <param name="FontSize">The size in device-independent pixels.</param>
/// <param name="FontWeight">The weight.</param>
/// <param name="LineHeight">The line box in device-independent pixels: the size times the role's leading.</param>
/// <param name="LetterSpacing">The tracking in device-independent pixels.</param>
/// <param name="Uppercase">Whether the role's text is authored in capitals; Avalonia has no text transform.</param>
public sealed record TextRole(string Class, FontFamily FontFamily, double FontSize, FontWeight FontWeight, double LineHeight,
    double LetterSpacing, bool Uppercase);

public static partial class Typography
{
    /// <summary>
    /// Gives a text block or templated control the role's class and writes its values locally, so the role also holds on a
    /// control built by <see cref="Ui.Text"/>, whose local size and weight would outrank the class style.
    /// </summary>
    /// <typeparam name="T">The control type.</typeparam>
    /// <param name="control">The control to style.</param>
    /// <param name="role">The role.</param>
    /// <returns>The control.</returns>
    public static T Apply<T>(T control, TextRole role) where T : Control
    {
        control.Classes.Add(role.Class);
        if (control is TextBlock text)
        {
            text.FontFamily = role.FontFamily; text.FontSize = role.FontSize; text.FontWeight = role.FontWeight;
            text.LineHeight = role.LineHeight; text.LetterSpacing = role.LetterSpacing;
        }
        else if (control is TemplatedControl templated)
        {
            templated.FontFamily = role.FontFamily; templated.FontSize = role.FontSize; templated.FontWeight = role.FontWeight;
        }
        return control;
    }

    /// <summary>The style for every role's class, added once to the application's styles.</summary>
    /// <returns>The styles.</returns>
    public static Styles Styles()
    {
        var styles = new Styles();
        foreach (var role in All)
        {
            styles.Add(new Style(selector => selector.OfType<TextBlock>().Class(role.Class))
            {
                Setters =
                {
                    new Setter(TextBlock.FontFamilyProperty, role.FontFamily),
                    new Setter(TextBlock.FontSizeProperty, role.FontSize),
                    new Setter(TextBlock.FontWeightProperty, role.FontWeight),
                    new Setter(TextBlock.LineHeightProperty, role.LineHeight),
                    new Setter(TextBlock.LetterSpacingProperty, role.LetterSpacing)
                }
            });
            styles.Add(new Style(selector => selector.Is<TemplatedControl>().Class(role.Class))
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontFamilyProperty, role.FontFamily),
                    new Setter(TemplatedControl.FontSizeProperty, role.FontSize),
                    new Setter(TemplatedControl.FontWeightProperty, role.FontWeight)
                }
            });
        }
        return styles;
    }
}