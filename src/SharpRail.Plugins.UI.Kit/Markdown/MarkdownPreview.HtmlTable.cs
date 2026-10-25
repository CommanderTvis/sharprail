using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace SharpRail.Plugins.UI.Kit.Markdown;

// HTML tables follow GitHub's sanitizer: table structure, align, valign, colspan, rowspan and width are read; style, class,
// ids and every other attribute are ignored. Cell content is raw HTML unless a blank line let Markdown blocks in.
public sealed partial class MarkdownPreview
{
    internal const int HtmlTableNesting = 20;
    internal const int HtmlTableRows = 2000;
    internal const int HtmlTableColumns = 200;
    internal const int HtmlTableCells = 20000;
    private const int HtmlTableOccupied = 200000;

    private int htmlTableDepth;

    private sealed class HtmlCell(HtmlToken tag)
    {
        public HtmlToken Tag { get; } = tag;
        public List<object> Items { get; } = [];
        public bool Header { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int ColumnSpan { get; set; } = 1;
        public int RowSpan { get; set; } = 1;
    }

    private static int TableDelta(HtmlToken token) => token.Tag == "table" ? token.Closing ? -1 : 1 : 0;

    // Index of the first <table> that tokens leave open, or -1 when every table closes.
    private static int UnmatchedTable(IReadOnlyList<HtmlToken> tokens)
    {
        int depth = 0, start = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            var delta = TableDelta(tokens[index]);
            if (delta > 0 && depth++ == 0) start = index;
            else if (delta < 0 && depth > 0) depth--;
        }
        return depth > 0 ? start : -1;
    }

    private static int MatchingTableClose(IReadOnlyList<object> items, int open)
    {
        var depth = 0;
        for (var index = open; index < items.Count; index++)
        {
            if (items[index] is not HtmlToken token) continue;
            depth += TableDelta(token);
            if (depth == 0) return index;
        }
        return -1;
    }

    // "200", "200px" or "50%"; anything else is auto.
    private static (double Value, bool Percent)? Dimension(HtmlToken token, string name)
    {
        var text = token.Attributes.GetValueOrDefault(name)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var percent = text.EndsWith('%');
        text = text.TrimEnd('%');
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 10000 && (!percent || value <= 100) ? (value, percent) : null;
    }

    private static int Span(HtmlToken token, string name, int limit) =>
        int.TryParse(token.Attributes.GetValueOrDefault(name)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, 1, limit) : 1;

    /// <summary>Controls for a run of tokens and Markdown blocks (blocks appear when blank lines split an HTML block).</summary>
    private List<Control> HtmlItemControls(IReadOnlyList<object> items)
    {
        var controls = new List<Control>();
        var run = new List<HtmlToken>();
        foreach (var item in items)
        {
            if (item is HtmlToken token) { run.Add(token); continue; }
            controls.AddRange(HtmlControls(run)); run.Clear();
            if (item is Block block && block is not YamlFrontMatterBlock) controls.Add(Render(block));
        }
        controls.AddRange(HtmlControls(run));
        return controls;
    }

    /// <summary>items starts at the opening &lt;table&gt; and ends at its close (or at the end of the document when unclosed).</summary>
    private Control HtmlTable(IReadOnlyList<object> items)
    {
        var table = (HtmlToken)items[0];
        var rows = new List<List<HtmlCell>>();
        var caption = new List<object>();
        var columnWidths = new Dictionary<int, (double Value, bool Percent)>();
        List<HtmlCell>? row = null;
        HtmlCell? cell = null;
        var cells = 0;
        var header = false;
        var inCaption = false;
        var nested = 0;

        for (var index = 1; index < items.Count; index++)
        {
            if (items[index] is HtmlToken { Tag: not null } token && (nested == 0 || token.Tag == "table"))
            {
                if (nested > 0 || token is { Tag: "table", Closing: false })
                {
                    nested += TableDelta(token);
                    (inCaption ? caption : cell?.Items)?.Add(token);
                    continue;
                }
                switch (token.Tag)
                {
                    case "table": index = items.Count; break;
                    case "thead": cell = null; header = !token.Closing; break;
                    case "tbody" or "tfoot": cell = null; header = false; break;
                    case "caption": cell = null; inCaption = !token.Closing; break;
                    case "col" when !token.Closing:
                        if (Dimension(token, "width") is { } width) columnWidths[columnWidths.Count] = width;
                        else columnWidths[columnWidths.Count] = default;
                        break;
                    case "tr":
                        cell = null;
                        row = token.Closing ? null : rows.Count < HtmlTableRows ? new() : null;
                        if (row is not null) rows.Add(row);
                        break;
                    case "td" or "th":
                        cell = null;
                        if (token.Closing) break;
                        if (row is null)
                        {
                            if (rows.Count >= HtmlTableRows) break;
                            row = [];
                            rows.Add(row);
                        }
                        if (cells >= HtmlTableCells) break;
                        cells++;
                        cell = new HtmlCell(token)
                        {
                            Header = token.Tag == "th" || header,
                            ColumnSpan = Span(token, "colspan", HtmlTableColumns),
                            RowSpan = Span(token, "rowspan", HtmlTableRows)
                        };
                        row.Add(cell);
                        break;
                    default: (inCaption ? caption : cell?.Items)?.Add(token); break;
                }
                continue;
            }
            (inCaption ? caption : cell?.Items)?.Add(items[index]);
        }

        // Place cells on the grid, skipping slots that earlier row spans cover.
        var occupied = new HashSet<(int, int)>();
        var columns = 0;
        var placed = new List<HtmlCell>();
        for (var r = 0; r < rows.Count; r++)
        {
            var c = 0;
            foreach (var current in rows[r])
            {
                while (occupied.Contains((r, c))) c++;
                if (c >= HtmlTableColumns) break;
                current.Row = r; current.Column = c;
                current.ColumnSpan = Math.Min(current.ColumnSpan, HtmlTableColumns - c);
                current.RowSpan = Math.Min(current.RowSpan, rows.Count - r);
                if (occupied.Count + current.ColumnSpan * current.RowSpan > HtmlTableOccupied) current.RowSpan = 1;
                for (var dr = 0; dr < current.RowSpan; dr++)
                    for (var dc = 0; dc < current.ColumnSpan; dc++) occupied.Add((r + dr, c + dc));
                c += current.ColumnSpan;
                columns = Math.Max(columns, c);
                placed.Add(current);
            }
        }

        var grid = new Grid();
        for (var r = 0; r < rows.Count; r++) grid.RowDefinitions.Add(new(GridLength.Auto));
        var percent = false;
        for (var c = 0; c < columns; c++)
        {
            // A width on <col> or on the column's first single-column cell sets the column; percentages share the remaining space.
            var width = columnWidths.GetValueOrDefault(c);
            if (width == default)
                foreach (var candidate in placed)
                    if (candidate.Column == c && candidate.ColumnSpan == 1 && Dimension(candidate.Tag, "width") is { } found) { width = found; break; }
            if (width is { Percent: true, Value: > 0 }) { percent = true; grid.ColumnDefinitions.Add(new(width.Value, GridUnitType.Star)); }
            else grid.ColumnDefinitions.Add(new(GridLength.Auto));
        }

        foreach (var current in placed)
        {
            var contents = new StackPanel();
            contents.Children.AddRange(HtmlItemControls(current.Items));
            var align = Alignment(current.Tag);
            var imageWidth = 0d;
            foreach (var child in contents.Children)
            {
                child.Margin = new Thickness(0, 2);
                if (current.Tag.Tag == "th" && child is SelectableTextBlock text) text.FontWeight = FontWeight.SemiBold;
                if (align is { } side)
                {
                    if (child is SelectableTextBlock block) block.TextAlignment = side switch { HorizontalAlignment.Center => Avalonia.Media.TextAlignment.Center, HorizontalAlignment.Right => Avalonia.Media.TextAlignment.Right, _ => Avalonia.Media.TextAlignment.Left };
                    else child.HorizontalAlignment = side;
                }
                imageWidth = Math.Max(imageWidth, child is Border { MaxWidth: < 10000 } image && image.Classes.Contains("html-image") ? image.MaxWidth : 0);
            }
            contents.VerticalAlignment = current.Tag.Attributes.GetValueOrDefault("valign")?.ToLowerInvariant() switch
            {
                "top" => VerticalAlignment.Top,
                "bottom" => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Center
            };
            var stated = Dimension(current.Tag, "width") is { Percent: false } pixels ? pixels.Value : 0;
            var view = new Border
            {
                Child = contents,
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4),
                Background = current.Tag.Tag == "th" ? Ui.Elevated : current.Row % 2 == 0 ? Ui.Surface : Ui.Sidebar,
                MinWidth = stated,
                // Text wraps at the same width as a Markdown table cell; a stated width or a wide picture lifts the cap.
                MaxWidth = percent ? double.PositiveInfinity : Math.Max(360, Math.Max(stated, imageWidth)) + 18
            };
            Grid.SetRow(view, current.Row); Grid.SetColumn(view, current.Column);
            Grid.SetRowSpan(view, current.RowSpan); Grid.SetColumnSpan(view, current.ColumnSpan);
            grid.Children.Add(view);
        }
        if (Dimension(table, "width") is { Percent: false } tableWidth) grid.MinWidth = tableWidth.Value;

        var panel = new StackPanel { Margin = new Thickness(0, 12), HorizontalAlignment = Alignment(table) ?? HorizontalAlignment.Left };
        panel.Classes.Add("html-table");
        if (caption.Count > 0)
        {
            var title = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            title.Children.AddRange(HtmlItemControls(caption));
            CollapseMargins(title);
            panel.Children.Add(title);
        }
        panel.Children.Add(percent ? grid : new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        return panel;
    }
}