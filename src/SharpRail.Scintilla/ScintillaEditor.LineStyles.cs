using Avalonia.Media;

namespace SharpRail.Scintilla;

/// <summary>A whole-line style: the text colour and an optional opaque band behind the line.</summary>
public sealed record ScintillaLineStyle(Color Foreground, Color? Background = null);

public sealed partial class ScintillaEditor
{
    // Line style i is Scintilla style i + 1, below the predefined styles from 32.
    private const int MaxLineStyles = 31;
    private const int LineNumberStyle = 33, MarginNumber = 1, MarginRightText = 5;
    private IReadOnlyList<ScintillaLineStyle> lineStyles = [];
    private bool lineNumbers = true;
    private string? widestLabel;

    /// <summary>The palette <see cref="StyleLines"/> indexes; re-applied after every colour change.</summary>
    public IReadOnlyList<ScintillaLineStyle> LineStyles
    {
        get => lineStyles;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Count, MaxLineStyles);
            lineStyles = value; appliedColors = null; InvalidateVisual();
        }
    }

    public bool ShowLineNumbers
    {
        get => lineNumbers;
        set
        {
            lineNumbers = value; widestLabel = null;
            document.Send(ScintillaMessage.SetMarginTypeN, 0, MarginNumber);
            document.Send(ScintillaMessage.MarginTextClearAll);
            document.Send(ScintillaMessage.SetMarginWidthN, 0, value ? NumberMargin(appliedSize) : 0);
            ApplyWrap(Bounds.Width); InvalidateVisual();
        }
    }

    /// <summary>
    /// Replaces the line numbers with a right-aligned label per document line, such as a diff's file line
    /// numbers; null entries stay blank.
    /// </summary>
    public void LabelLines(IReadOnlyList<string?> labels)
    {
        lineNumbers = true;
        document.Send(ScintillaMessage.SetMarginTypeN, 0, MarginRightText);
        document.Send(ScintillaMessage.MarginTextClearAll);
        var widest = "";
        for (var line = 0; line < labels.Count; line++)
        {
            if (labels[line] is not { Length: > 0 } label) continue;
            document.Send(ScintillaMessage.MarginSetText, line, label);
            document.Send(ScintillaMessage.MarginSetStyle, line, LineNumberStyle);
            if (label.Length > widest.Length) widest = label;
        }
        widestLabel = widest;
        SizeLabelMargin();
        ApplyWrap(Bounds.Width); InvalidateVisual();
    }

    // Label widths depend on the line-number style's font, which colour application resets.
    private void SizeLabelMargin()
    {
        if (widestLabel is null) return;
        var width = widestLabel.Length == 0 ? 0 : document.Send(ScintillaMessage.TextWidth, LineNumberStyle, widestLabel + "  ");
        document.Send(ScintillaMessage.SetMarginWidthN, 0, width);
    }

    /// <summary>Styles each document line with an index into <see cref="LineStyles"/>, or -1 for the default.</summary>
    public void StyleLines(IReadOnlyList<int> styles)
    {
        var count = document.Send(ScintillaMessage.GetLineCount);
        var length = document.Send(ScintillaMessage.GetLength);
        document.Send(ScintillaMessage.StartStyling);
        for (var line = 0; line < count; line++)
        {
            var style = line < styles.Count ? styles[line] : -1;
            var end = line + 1 < count ? document.Send(ScintillaMessage.PositionFromLine, line + 1) : length;
            document.Send(ScintillaMessage.SetStyling, end - document.Send(ScintillaMessage.PositionFromLine, line), style + 1);
        }
        InvalidateVisual();
    }

    private void ApplyLineStyles(Func<Color, uint> rgb)
    {
        // Runs while rendering, so it must not invalidate; the wrap margin follows the new gutter width.
        SizeLabelMargin(); ApplyWrap(Bounds.Width);
        for (var index = 0; index < lineStyles.Count; index++)
        {
            // A style background with end-of-line fill reaches every drawing path, including tabs and wrapped
            // sublines, which a background marker misses in the bidirectional layout.
            document.Send(ScintillaMessage.StyleSetFore, index + 1, (nint)rgb(lineStyles[index].Foreground));
            if (lineStyles[index].Background is not { } background) continue;
            document.Send(ScintillaMessage.StyleSetBack, index + 1, (nint)rgb(background));
            document.Send(ScintillaMessage.StyleSetEOLFilled, index + 1, 1);
        }
    }
}