using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>Converts the symbol-count line widths in preferences to logical pixels, like CSS <c>ch</c> units.</summary>
public static class LineWidths
{
    public const int Minimum = 40, Maximum = 240, FileDefault = 120, MarkdownDefault = 78;

    public static bool IsValid(int columns) => columns is >= Minimum and <= Maximum;

    public static double File(Preferences preferences) =>
        preferences.FileLineWidthBounded ? preferences.FileLineWidth * Advance(Ui.CodeFont, 13) : double.PositiveInfinity;

    public static double Markdown(Preferences preferences) =>
        preferences.MarkdownLineWidthBounded ? preferences.MarkdownLineWidth * Advance(Ui.InterfaceFont, preferences.FontSize) : double.PositiveInfinity;

    private static double Advance(FontFamily family, double size)
    {
        using var layout = new TextLayout("0", new Typeface(family), size, null);
        return layout.WidthIncludingTrailingWhitespace;
    }
}
