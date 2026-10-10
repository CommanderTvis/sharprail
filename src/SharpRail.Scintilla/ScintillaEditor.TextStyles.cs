using Avalonia.Media;

namespace SharpRail.Scintilla;

public sealed partial class ScintillaEditor
{
    private const int FirstTextStyle = 40;
    private IReadOnlyList<ScintillaLineStyle> textStyles = [];

    /// <summary>Applies a style index per UTF-8 document byte using an opaque colour palette. Does not edit text or undo history.</summary>
    public unsafe void StyleText(byte[] styles, IReadOnlyList<ScintillaLineStyle> palette)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(palette.Count, 256 - FirstTextStyle);
        if (styles.Length != document.Send(ScintillaMessage.GetLength))
            throw new ArgumentException("Styles must cover every UTF-8 document byte.", nameof(styles));
        if (styles.Any(style => style >= palette.Count))
            throw new ArgumentException("Style index is outside the palette.", nameof(styles));
        textStyles = palette;
        appliedColors = null;
        var nativeStyles = new byte[styles.Length];
        for (var i = 0; i < styles.Length; i++) nativeStyles[i] = (byte)(FirstTextStyle + styles[i]);
        document.Send(ScintillaMessage.StartStyling);
        fixed (byte* pointer = nativeStyles)
            document.Send(ScintillaMessage.SetStylingEx, nativeStyles.Length, (nint)pointer);
        InvalidateVisual();
    }

    /// <summary>Restores the document to its default text style without editing it.</summary>
    public void ClearTextStyles()
    {
        textStyles = [];
        document.Send(ScintillaMessage.StartStyling);
        document.Send(ScintillaMessage.SetStyling, document.Send(ScintillaMessage.GetLength));
        InvalidateVisual();
    }

    private void ApplyTextStyles(Func<Color, uint> rgb)
    {
        for (var index = 0; index < textStyles.Count; index++)
        {
            var style = FirstTextStyle + index;
            document.Send(ScintillaMessage.StyleSetFore, style, (nint)rgb(textStyles[index].Foreground));
            if (textStyles[index].Background is not { } background) continue;
            document.Send(ScintillaMessage.StyleSetBack, style, (nint)rgb(background));
            document.Send(ScintillaMessage.StyleSetEOLFilled, style, 1);
        }
    }
}