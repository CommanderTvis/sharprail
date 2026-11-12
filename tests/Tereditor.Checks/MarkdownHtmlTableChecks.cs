using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.UI.Kit.Markdown;

using SkiaSharp;

namespace SharpRail.Checks;

/// <summary>HTML tables in Markdown follow GitHub: structure, spans, alignment and widths are read; Markdown inside a cell needs a blank line; style, script and frames are dropped.</summary>
internal static class MarkdownHtmlTableChecks
{
    internal static void Run()
    {
        var simple = Render("<table>\n<tr><th>A</th><th>B</th></tr>\n<tr><td>1</td><td>2</td></tr>\n</table>\n");
        var grid = Grids(simple.Preview).Single();
        Require(grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 2 && grid.Children.Count == 4, "A simple HTML table draws a 2x2 grid.");
        Require(Text(simple.Preview) == "AB12", "Cell text is kept in order: " + Text(simple.Preview));
        simple.Dispose();

        var spans = Render("<table>\n<thead><tr><th colspan=\"2\">Head</th></tr></thead>\n<tr><td rowspan=\"2\" align=\"right\">x</td><td>a</td></tr>\n<tr><td>b</td></tr>\n</table>\n");
        var spanned = Grids(spans.Preview).Single();
        var cells = spanned.Children.OfType<Border>().ToArray();
        Require(spanned.ColumnDefinitions.Count == 2 && spanned.RowDefinitions.Count == 3, "colspan and rowspan size the grid.");
        Require(Grid.GetColumnSpan(cells[0]) == 2 && Grid.GetRowSpan(cells[1]) == 2, "colspan and rowspan reach the cells.");
        Require(Grid.GetColumn(cells[3]) == 1 && Grid.GetRow(cells[3]) == 2, "A cell below a row span skips the covered column.");
        Require(((StackPanel)cells[1].Child!).Children.OfType<SelectableTextBlock>().Single().TextAlignment == TextAlignment.Right, "align=right aligns the cell text.");
        spans.Dispose();

        var picture = Render("<table><tr><td>" + string.Join(" ", Enumerable.Repeat("A long sentence of ordinary words that wraps.", 12)) + "</td><td><img src=\"big.png\" width=\"400\" alt=\"Big\"></td></tr></table>\n", 1000);
        Wait(picture, () => picture.Preview.GetLogicalDescendants().OfType<Image>().Any(image => image.Name == "MarkdownImage"));
        var holder = picture.Preview.GetLogicalDescendants().OfType<Border>().Single(border => border.Classes.Contains("html-image"));
        Require(Math.Abs(holder.Bounds.Width - 400) < 1.5, "A cell picture keeps its stated width instead of being squeezed: " + holder.Bounds.Width);
        picture.Dispose();

        var markdown = Render("<table>\n<tr>\n<td>\n\n**bold** and `code`\n\n</td>\n<td>\n**literal**\n</td>\n</tr>\n</table>\n");
        var bold = markdown.Preview.GetLogicalDescendants().OfType<SelectableTextBlock>().SelectMany(text => text.Inlines?.OfType<Run>() ?? []).ToArray();
        Require(bold.Any(run => run.Text == "bold" && run.FontWeight == FontWeight.Medium), "Markdown after a blank line inside a cell is rendered.");
        Require(Text(markdown.Preview).Contains("**literal**", StringComparison.Ordinal), "Markdown without a blank line stays literal, as on GitHub: " + Text(markdown.Preview));
        Require(Grids(markdown.Preview).Single().Children.Count == 2, "Blocks split by blank lines stay inside one table.");
        markdown.Dispose();

        var unsafeTable = Render("<table style=\"color:red\"><tr><td class=\"x\" onclick=\"bad()\" style=\"width:9px\">ok<script>alert(1)</script><iframe src=\"http://example.com/\">frame</iframe><style>p{}</style> <font color=\"red\">kept</font></td></tr></table>\n");
        var shown = Text(unsafeTable.Preview);
        Require(shown.Contains("ok", StringComparison.Ordinal) && shown.Contains("kept", StringComparison.Ordinal) && !shown.Contains("alert", StringComparison.Ordinal)
            && !shown.Contains("frame", StringComparison.Ordinal) && !shown.Contains("p{}", StringComparison.Ordinal), "Script, style and iframe are dropped and unknown tags keep their text: " + shown);
        Require(unsafeTable.Preview.GetLogicalDescendants().OfType<Border>().All(border => border.MinWidth == 0), "style widths are ignored.");
        unsafeTable.Dispose();

        var wide = Render("<table><tr><td colspan=\"99999\">x</td></tr></table>\n");
        Require(Grids(wide.Preview).Single().ColumnDefinitions.Count <= MarkdownPreview.HtmlTableColumns, "A huge colspan is capped.");
        wide.Dispose();
        var tall = Render("<table>" + string.Concat(Enumerable.Repeat("<tr><td>r</td></tr>", MarkdownPreview.HtmlTableRows + 500)) + "</table>\n");
        Require(Grids(tall.Preview).Single().RowDefinitions.Count == MarkdownPreview.HtmlTableRows, "Rows beyond the cap are dropped.");
        tall.Dispose();
        var deep = Render(string.Concat(Enumerable.Repeat("<table><tr><td>", 30)) + "core" + string.Concat(Enumerable.Repeat("</td></tr></table>", 30)) + "\n");
        var tables = deep.Preview.GetLogicalDescendants().OfType<StackPanel>().Count(panel => panel.Classes.Contains("html-table"));
        Require(tables == MarkdownPreview.HtmlTableNesting && Text(deep.Preview).Contains("core", StringComparison.Ordinal), "Nesting stops at the limit and keeps the text: " + tables);
        deep.Dispose();
        Console.WriteLine("PASS Markdown HTML tables: structure, spans, alignment, widths, Markdown in cells after a blank line, sanitising and limits follow GitHub");
    }

    private sealed record Shown(MarkdownPreview Preview, Window Window) : IDisposable
    {
        public void Dispose() { Window.Close(); Preview.Dispose(); }
    }

    private static Shown Render(string source, double width = 800)
    {
        using var png = new MemoryStream();
        using (var bitmap = new SKBitmap(1600, 800)) { bitmap.Erase(SKColors.Red); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); data.SaveTo(png); }
        var bytes = png.ToArray();
        var context = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(bytes), _ => null, (_, _) => { }, 14, 100, true);
        var preview = new MarkdownPreview(MarkdownPreview.Parse(source), "README.md", context);
        var window = new Window { Width = width, Height = 600, Content = preview };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return new Shown(preview, window);
    }

    private static void Wait(Shown shown, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(done(), "The picture loaded.");
        Dispatcher.UIThread.RunJobs();
        shown.Window.UpdateLayout();
    }

    private static Grid[] Grids(MarkdownPreview preview) => preview.GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Classes.Contains("html-table"))
        .Select(panel => panel.GetLogicalDescendants().OfType<Grid>().First()).ToArray();

    private static string Text(MarkdownPreview preview) => string.Concat(preview.GetLogicalDescendants().OfType<SelectableTextBlock>()
        .SelectMany(text => text.Inlines?.OfType<Run>() ?? []).Select(run => run.Text));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}