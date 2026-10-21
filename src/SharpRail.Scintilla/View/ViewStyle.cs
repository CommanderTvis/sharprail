// Shared view data set by editor messages (Editing) and read by painting and layout (View).
// Owned by the skeleton; both wave-3 components may add fields, never change existing ones.
using SkiaSharp;

namespace SharpRail.Scintilla.View;

/// <summary>A resolved font request; the shaper maps it to an SKFont of the editor typeface.</summary>
internal readonly record struct FontSpec(float Size, int Weight, bool Italic);

/// <summary>One of the 256 styles (upstream src/Style.h). Colours are opaque.</summary>
internal sealed class Style
{
    public SKColor Fore = SKColors.Black;
    public SKColor Back = SKColors.White;
    public float Size = 13;
    public int Weight = 400;
    public bool Italic;
    public bool EolFilled;
    public string Font = "";
    public FontSpec FontSpec => new(Size, Weight, Italic);
    public Style Clone() => (Style)MemberwiseClone();
}

/// <summary>Indicator drawing styles in use (INDIC_*).</summary>
internal enum IndicatorStyle { Plain = 0, Squiggle = 1, Hidden = 5 }

internal sealed class Indicator
{
    public IndicatorStyle Style;
    public SKColor Fore = new(0, 0x7f, 0);
}

internal enum MarginType { Symbol = 0, Number = 1, Text = 4, RightText = 5 }

internal sealed class Margin
{
    public MarginType Type;
    public int Width;
}

/// <summary>Element ids with settable colours (SC_ELEMENT_*).</summary>
internal enum Element { SelectionText = 10, SelectionBack = 11, SelectionInactiveText = 16, SelectionInactiveBack = 17, Caret = 40, CaretLineBack = 50 }

/// <summary>Everything painting reads besides the document (upstream src/ViewStyle.h, trimmed to SharpRail's use).</summary>
internal sealed class ViewStyle
{
    public const int Default = 32, LineNumber = 33, MaxStyles = 256, MaxIndicators = 36, MaxMargins = 5;
    public readonly Style[] Styles = CreateStyles();
    public readonly Indicator[] Indicators = CreateIndicators();
    public readonly Margin[] Margins = [new() { Type = MarginType.Number }, new() { Width = 16 }, new(), new(), new()];
    public readonly Dictionary<Element, SKColor> Elements = [];
    public int LeftMarginWidth = 1, RightMarginWidth = 1;
    public int TabWidth = 8;
    public bool UseTabs = true;
    public bool WrapWord;
    public SKColor CaretFore = SKColors.Black;
    public int CaretPeriodMs = 500;
    // Derived metrics, refreshed by Refresh from the shaper's font metrics.
    public float LineHeight, Ascent, Descent, SpaceWidth, TabPixels;
    /// <summary>Widest printable ASCII advance across the styles' fonts, bounding a line's width without shaping it.</summary>
    public float MaxAsciiAdvance;

    public int FixedColumnWidth
    {
        get
        {
            var width = LeftMarginWidth;
            foreach (var margin in Margins) width += margin.Width;
            return width;
        }
    }

    /// <summary>STYLECLEARALL: copies the default style into every other style.</summary>
    public void ClearAll()
    {
        for (var i = 0; i < MaxStyles; i++)
            if (i != Default) Styles[i] = Styles[Default].Clone();
    }

    private static Style[] CreateStyles()
    {
        var styles = new Style[MaxStyles];
        for (var i = 0; i < MaxStyles; i++) styles[i] = new Style();
        return styles;
    }

    private static Indicator[] CreateIndicators()
    {
        var indicators = new Indicator[MaxIndicators];
        for (var i = 0; i < MaxIndicators; i++) indicators[i] = new Indicator();
        return indicators;
    }
}