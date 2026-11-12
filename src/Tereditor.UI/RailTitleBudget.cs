namespace Tereditor.UI;

/// <summary>
/// How many lines a workspace title may take in the Projects rail. A project's list shares one budget, so its rows do not
/// jump from row to row: few workspaces keep their long titles, many shrink them to fit one calmer screenful.
/// </summary>
internal static class RailTitleBudget
{
    /// <summary>Line height of the title text; the label sets it.</summary>
    internal const double TitleLineHeight = 17.5;
    /// <summary>Line height of the branch line under the title.</summary>
    internal const double SecondLineHeight = 15;
    /// <summary>Font size of everything on the second line: the branch and the change numbers.</summary>
    internal const double SecondFontSize = 12;
    /// <summary>Vertical padding of the row button (4 above and below).</summary>
    private const double RowPadding = 8;
    /// <summary>Gap between rows in a project's list.</summary>
    private const double RowGap = 4;
    /// <summary>Most lines a title may take.</summary>
    internal const int MostLines = 3;
    /// <summary>Height one project's workspace list may fill before titles lose lines: about a third of a laptop window.</summary>
    internal const double ListHeight = 360;

    internal static double RowHeight(int titleLines) => TitleLineHeight * titleLines + SecondLineHeight + RowPadding + RowGap;

    /// <summary>The largest line count at which a list of this many rows still fits <see cref="ListHeight"/>; one line at least.</summary>
    internal static int Lines(int rows)
    {
        for (var lines = MostLines; lines > 1; lines--)
            if (rows * RowHeight(lines) <= ListHeight) return lines;
        return 1;
    }

    /// <summary>The row's tooltip: the path, and the whole title above it once the title may be cut.</summary>
    internal static string Tip(string title, string path, int lines) => lines is > 0 and < MostLines ? title + "\n" + path : path;
}