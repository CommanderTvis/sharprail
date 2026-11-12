using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>
/// Large top-level tables, lists and code blocks render as consecutive chunks, so the block list can virtualize
/// inside them instead of laying a whole 5,000-row table or 20,000-line listing out at once.
/// </summary>
public sealed partial class MarkdownPreview
{
    /// <summary>A rendered document with more top-level blocks than this, or with any split block, virtualizes.</summary>
    internal const int VirtualizeAbove = 32;
    /// <summary>Body rows per table chunk; tables with more are split, the header staying in the first chunk.</summary>
    internal const int TableChunkRows = 50;
    /// <summary>Items per list chunk; numbering continues across chunks.</summary>
    internal const int ListChunkItems = 100;
    /// <summary>Source lines per code chunk; Copy on the first chunk copies the whole block.</summary>
    internal const int CodeChunkLines = 100;

    // Code chunks after the first continue the previous chunk's text, so a selection joins them with one line break.
    private readonly HashSet<SelectableTextBlock> continuations = [];

    internal bool Continues(SelectableTextBlock block) => continuations.Contains(block);

    // Set once any block splits: chunks only pay off inside the virtualizing block list.
    private bool chunked;

    /// <summary>The chunks a top-level block renders as, or null when it is small enough to lay out whole.</summary>
    private List<Control>? Chunks(Block block)
    {
        var chunks = Split(block);
        chunked |= chunks is not null;
        return chunks;
    }

    private List<Control>? Split(Block block) => block switch
    {
        FencedCodeBlock fence when renderDiagrams && string.Equals(fence.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase) => null,
        CodeBlock code when code.Lines.Count > CodeChunkLines => CodeChunks(code),
        Table table when table.OfType<TableRow>().Count(row => !row.IsHeader) > TableChunkRows => TableChunks(table),
        ListBlock list when list.Count > ListChunkItems => ListChunks(list),
        _ => null
    };

    private List<Control> CodeChunks(CodeBlock code)
    {
        var text = code.Lines.ToString();
        var lines = text.Split('\n');
        var (blockStart, blockEnd) = SourceRange(code);
        var firstLine = code.Lines.Count > 0 ? code.Lines.Lines[0].Line + 1 : blockStart;
        var group = new HorizontalScrollGroup();
        // Every chunk is as wide as the block's longest line, so they scroll horizontally as one listing.
        var width = lines.Max(line => line.Length + line.Count(character => character == '\t') * 3) * CodeAdvance();
        var chunks = new List<Control>();
        for (var start = 0; start < lines.Length; start += CodeChunkLines)
        {
            var count = Math.Min(CodeChunkLines, lines.Length - start);
            bool first = start == 0, last = start + count >= lines.Length;
            var source = Code(string.Join('\n', lines, start, count));
            source.MinWidth = width;
            if (!first) continuations.Add(source);
            var codeBody = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            codeBody.Children.Add(group.Add(new ScrollViewer
            {
                Content = source,
                HorizontalScrollBarVisibility = last ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            }));
            if (first)
            {
                var copy = Ui.Button("Copy", () => _ = CopyAsync(text), "check");
                copy.VerticalAlignment = VerticalAlignment.Top;
                copy.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(copy, 1);
                codeBody.Children.Add(copy);
            }
            var frame = new Border
            {
                Child = codeBody,
                Background = Ui.Elevated,
                Padding = new Thickness(12, first ? 12 : 0, 12, last ? 12 : 0),
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1, first ? 1 : 0, 1, last ? 1 : 0),
                CornerRadius = new CornerRadius(first ? 4 : 0, first ? 4 : 0, last ? 4 : 0, last ? 4 : 0),
                Margin = new Thickness(0, first ? 12 : 0, 0, last ? 12 : 0)
            };
            frame.SetValue(MarkdownBlockList.EstimateProperty, count * Typography.CodeDocument.LineHeight + frame.Padding.Top + frame.Padding.Bottom);
            chunks.Add(SourcedLines(frame, first ? blockStart : firstLine + start, last ? blockEnd : firstLine + start + count - 1));
        }
        return chunks;
    }

    private static double CodeAdvance()
    {
        using var layout = new TextLayout("0000000000", new Typeface(Ui.CodeFont), Typography.CodeDocument.FontSize, null);
        return layout.WidthIncludingTrailingWhitespace / 10;
    }

    private List<Control> TableChunks(Table table)
    {
        var rows = table.OfType<TableRow>().ToArray();
        var columns = rows.Select(row => row.Count).DefaultIfEmpty(0).Max();
        // Chunks are separate grids, so columns take fixed widths estimated from the longest cell text instead of Auto.
        var widths = new double[columns];
        foreach (var row in rows)
            for (var column = 0; column < row.Count; column++)
            {
                var characters = ((TableCell)row[column]).OfType<LeafBlock>().Sum(leaf => leaf.Inline is { } inline ? Plain(inline).Length : 0);
                widths[column] = Math.Max(widths[column], characters * context.FontSize * (row.IsHeader ? 0.6 : 0.55) + 18);
            }
        for (var column = 0; column < columns; column++) widths[column] = Math.Clamp(widths[column], 100, 360);
        var (blockStart, blockEnd) = SourceRange(table);
        var header = rows.TakeWhile(row => row.IsHeader).Count();
        var group = new HorizontalScrollGroup();
        var chunks = new List<Control>();
        for (var start = header; start < rows.Length; start += TableChunkRows)
        {
            var end = Math.Min(rows.Length, start + TableChunkRows);
            bool first = start == header, last = end == rows.Length;
            var grid = new Grid();
            foreach (var width in widths) grid.ColumnDefinitions.Add(new(width, GridUnitType.Pixel));
            var shown = (first ? Enumerable.Range(0, header) : []).Concat(Enumerable.Range(start, end - start)).ToArray();
            for (var line = 0; line < shown.Length; line++)
            {
                grid.RowDefinitions.Add(new(GridLength.Auto));
                for (var column = 0; column < rows[shown[line]].Count; column++)
                {
                    var cell = TableCellView(rows[shown[line]], shown[line], column);
                    cell.Width = widths[column];
                    Ui.Place(grid, cell, line, column);
                }
            }
            var scroll = group.Add(new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = last ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, first ? 12 : 0, 0, last ? 12 : 0)
            });
            scroll.SetValue(MarkdownBlockList.EstimateProperty, shown.Length * (context.FontSize * 1.6 + 10));
            chunks.Add(SourcedLines(scroll, first ? blockStart : rows[start].Line + 1, last ? blockEnd : rows[end - 1].Line + 1));
        }
        return chunks;
    }

    private List<Control> ListChunks(ListBlock list)
    {
        var rows = ListRows(list);
        var items = list.OfType<ListItemBlock>().ToArray();
        var (blockStart, blockEnd) = SourceRange(list);
        var chunks = new List<Control>();
        for (var start = 0; start < rows.Count; start += ListChunkItems)
        {
            var end = Math.Min(rows.Count, start + ListChunkItems);
            bool first = start == 0, last = end == rows.Count;
            // The list's own spacing continues between chunks.
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, first ? 12 : 4, 0, last ? 12 : 0) };
            for (var index = start; index < end; index++) panel.Children.Add(rows[index]());
            chunks.Add(SourcedLines(panel, first ? blockStart : items[start].Line + 1, last ? blockEnd : SourceRange(items[end - 1]).End));
        }
        return chunks;
    }

    private Border TableCellView(TableRow row, int index, int column)
    {
        var contents = Container((TableCell)row[column]);
        foreach (var child in contents.Children)
        {
            child.Margin = new Thickness(0);
            if (row.IsHeader && child is SelectableTextBlock cellText) cellText.FontWeight = FontWeight.SemiBold;
        }
        return new Border
        {
            Child = contents,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4),
            Background = row.IsHeader ? Ui.Elevated : (index % 2 == 0 ? Ui.Surface : Ui.Sidebar),
            MinWidth = 100,
            MaxWidth = 360
        };
    }

    /// <summary>
    /// Keeps the horizontal offset of a split block's chunks together: scrolling one chunk scrolls the others, and a
    /// chunk realized later takes the shared offset once it knows its extent.
    /// </summary>
    private sealed class HorizontalScrollGroup
    {
        private readonly List<ScrollViewer> members = [];
        private double offset;
        private bool applying;

        internal ScrollViewer Add(ScrollViewer scroll)
        {
            members.Add(scroll);
            scroll.ScrollChanged += (_, e) =>
            {
                if (applying) return;
                if (e.ExtentDelta.X != 0 || e.ViewportDelta.X != 0)
                {
                    if (Math.Abs(scroll.Offset.X - offset) > 0.5) Apply(scroll);
                    return;
                }
                if (e.OffsetDelta.X == 0) return;
                offset = scroll.Offset.X;
                foreach (var other in members) if (other != scroll) Apply(other);
            };
            return scroll;
        }

        private void Apply(ScrollViewer scroll)
        {
            applying = true;
            try { scroll.Offset = new Vector(offset, scroll.Offset.Y); }
            finally { applying = false; }
        }
    }
}