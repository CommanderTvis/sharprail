using Avalonia.Media;
using Avalonia.Media.TextFormatting;
namespace SharpRail.Plugins.UI.Kit;

/// <summary>Converts symbol-count line widths to logical pixels, like CSS <c>ch</c> units.</summary>
public static class LineWidths
{
    public const int Minimum = 40, Maximum = 240, FileDefault = 120, MarkdownDefault = 78;

    public static bool IsValid(int columns) => columns is >= Minimum and <= Maximum;

    public static double File(int width, bool bounded) =>
        bounded ? width * Advance(Ui.CodeFont, 13) : double.PositiveInfinity;

    public static double Markdown(int width, bool bounded, double fontSize) =>
        bounded ? width * Advance(Ui.InterfaceFont, fontSize) : double.PositiveInfinity;

    /// <summary>The width of <paramref name="columns"/> symbols in the code font, as a diff or editor lays them out.</summary>
    public static double Code(int columns) => columns * Advance(Ui.CodeFont, 13);

    private static double Advance(FontFamily family, double size)
    {
        using var layout = new TextLayout("0", new Typeface(family), size, null);
        return layout.WidthIncludingTrailingWhitespace;
    }
}