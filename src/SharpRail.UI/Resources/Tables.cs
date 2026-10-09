using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Resources;

internal sealed record TableRow(int Index, string[] Cells, string Raw);

internal enum TableChange { Unchanged, Added, Removed, Changed }

/// <summary>One row of a table diff; <see cref="ChangedCells"/> names the columns that differ in a changed row.</summary>
internal sealed record AlignedRow(TableChange Kind, TableRow? Original, TableRow? Modified, IReadOnlyList<int> ChangedCells);

/// <summary>Delimited text as rows of cells: delimiter sniffing, RFC 4180 quoting and row alignment for diffs.</summary>
internal static class TableModel
{
    private static readonly char[] Sniffed = [',', ';', '\t', '|'];
    private const int SniffRecords = 20;

    internal static char SniffDelimiter(string path, params string[] texts)
    {
        if (path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)) return '\t';
        var records = texts.Select(Sample).FirstOrDefault(lines => lines.Length > 0);
        if (records is null) return ',';
        var consistent = Sniffed.Select(delimiter => (Delimiter: delimiter, Fields: ConsistentFields(records, delimiter))).Where(entry => entry.Fields is not null).ToArray();
        if (consistent.Any(entry => entry.Delimiter == ',')) return ',';
        var widest = consistent.Select(entry => entry.Fields!.Value).DefaultIfEmpty(0).Max();
        var leaders = consistent.Where(entry => entry.Fields == widest).ToArray();
        return leaders.Length == 1 ? leaders[0].Delimiter : ',';
    }

    private static string[] Sample(string text) => text.TrimStart('﻿').Split('\n').Select(line => line.TrimEnd('\r'))
        .Where(line => line.Trim().Length > 0).Take(SniffRecords).ToArray();

    // The field count every sampled record shares, when there are at least two fields.
    private static int? ConsistentFields(string[] records, char delimiter)
    {
        int? first = null;
        foreach (var record in records)
        {
            var count = 1; var quoted = false;
            foreach (var character in record)
                if (character == '"') quoted = !quoted;
                else if (character == delimiter && !quoted) count++;
            first ??= count;
            if (count != first || count < 2) return null;
        }
        return first;
    }

    internal static List<TableRow> Parse(string text, char delimiter)
    {
        var rows = new List<TableRow>();
        if (text.Length == 0) return rows;
        var cells = new List<string>();
        var field = new StringBuilder();
        bool quoted = false; int rowStart = 0, index = 0;

        void Finish(int end)
        {
            cells.Add(field.ToString()); field.Clear();
            rows.Add(new(rows.Count, [.. cells], text[rowStart..end]));
            cells.Clear();
        }

        while (index < text.Length)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index += 2; continue; }
                    quoted = false; index++; continue;
                }
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') { field.Append('\n'); index += 2; continue; }
                field.Append(character == '\r' ? '\n' : character); index++;
                continue;
            }
            if (character == '"' && field.Length == 0) { quoted = true; index++; continue; }
            if (character == delimiter) { cells.Add(field.ToString()); field.Clear(); index++; continue; }
            if (character is '\r' or '\n')
            {
                Finish(index);
                index += character == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                rowStart = index;
                continue;
            }
            field.Append(character); index++;
        }
        if (rowStart < text.Length || cells.Count > 0 || field.Length > 0) Finish(text.Length);
        return rows;
    }

    internal static List<int> ChangedCells(TableRow original, TableRow modified) =>
        Enumerable.Range(0, Math.Max(original.Cells.Length, modified.Cells.Length))
            .Where(index => original.Cells.ElementAtOrDefault(index) != modified.Cells.ElementAtOrDefault(index)).ToList();

    /// <summary>Rows aligned by their raw text; a run of removals replaced by as many additions pairs up as changed rows.</summary>
    internal static List<AlignedRow> Align(IReadOnlyList<TableRow> original, IReadOnlyList<TableRow> modified, CancellationToken token = default)
    {
        var aligned = new List<AlignedRow>();
        List<TableRow> removed = [], added = [];
        int left = 0, right = 0;

        void Flush()
        {
            if (removed.Count == added.Count)
                for (var index = 0; index < removed.Count; index++) aligned.Add(new(TableChange.Changed, removed[index], added[index], ChangedCells(removed[index], added[index])));
            else
            {
                aligned.AddRange(removed.Select(row => new AlignedRow(TableChange.Removed, row, null, [])));
                aligned.AddRange(added.Select(row => new AlignedRow(TableChange.Added, null, row, [])));
            }
            removed.Clear(); added.Clear();
        }

        foreach (var (side, _) in MarkdownDiff.Sequence([.. original.Select(row => row.Raw)], [.. modified.Select(row => row.Raw)], token))
        {
            if (side < 0) removed.Add(original[left++]);
            else if (side > 0) added.Add(modified[right++]);
            else { Flush(); aligned.Add(new(TableChange.Unchanged, original[left++], modified[right++], [])); }
        }
        Flush();
        return aligned;
    }
}

/// <summary>A delimited file as a table, and two of them as one table with changed rows and cells marked.</summary>
internal static class TableViews
{
    private const int MaxCellCharacters = 40;
    private const double CharacterWidth = 7.3, GutterWidth = 48;

    internal static Control View(ResourceView view)
    {
        var text = (view.Content as ResourceContent.Text)?.Value ?? "";
        var rows = TableModel.Parse(text, TableModel.SniffDelimiter(view.Resource.Path, text));
        return List("TableResource", rows.Select(row => new AlignedRow(TableChange.Unchanged, null, row, [])).ToList());
    }

    internal static Task<Control?> DiffAsync(ResourceDiff diff, CancellationToken token)
    {
        if (diff.Original is ResourceContent.Bytes || diff.Modified is ResourceContent.Bytes) return Task.FromResult<Control?>(null);
        string original = (diff.Original as ResourceContent.Text)?.Value ?? "", modified = (diff.Modified as ResourceContent.Text)?.Value ?? "";
        return Task.Run<Control?>(async () =>
        {
            var delimiter = TableModel.SniffDelimiter(diff.Resource.Path, modified, original);
            var aligned = TableModel.Align(TableModel.Parse(original, delimiter), TableModel.Parse(modified, delimiter), token);
            return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => List("TableDiff", aligned));
        }, token);
    }

    private static string Shown(string cell) => cell.Replace('\n', '⏎');

    private static Control List(string name, List<AlignedRow> rows)
    {
        if (rows.Count == 0)
        {
            var empty = Ui.Text("No rows", Ui.Hint, 12);
            empty.Name = name; empty.Margin = new Thickness(20); empty.HorizontalAlignment = HorizontalAlignment.Left; empty.VerticalAlignment = VerticalAlignment.Top;
            return empty;
        }
        var columns = rows.Max(row => Math.Max(row.Original?.Cells.Length ?? 0, row.Modified?.Cells.Length ?? 0));
        var widths = new double[columns];
        foreach (var row in rows)
            for (var column = 0; column < columns; column++)
            {
                var length = (row.Modified?.Cells.ElementAtOrDefault(column)?.Length ?? 0) +
                    (row.ChangedCells.Contains(column) || row.Modified is null ? (row.Original?.Cells.ElementAtOrDefault(column)?.Length ?? 0) + 1 : 0);
                widths[column] = Math.Max(widths[column], Math.Min(length, MaxCellCharacters) * CharacterWidth + 18);
            }
        var template = new FuncDataTemplate<AlignedRow>((row, _) => row is null ? null : Row(row, widths), supportsRecycling: false);
        var list = new ListBox
        {
            Name = name,
            ItemsSource = rows,
            ItemTemplate = template,
            Background = Brushes.Transparent,
            Padding = new Thickness(0)
        };
        list.Resources["ListBoxItemPadding"] = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        return list;
    }

    private static Control Row(AlignedRow row, double[] widths)
    {
        var shown = row.Modified ?? row.Original!;
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = row.Kind switch { TableChange.Added => Ui.SuccessWash, TableChange.Removed => Ui.DangerWash, _ => shown.Index == 0 ? Ui.Header : Brushes.Transparent }
        };
        panel.Classes.Add("table-" + row.Kind.ToString().ToLowerInvariant());
        panel.Children.Add(Cell((shown.Index + 1).ToString(CultureInfo.InvariantCulture), GutterWidth, Ui.Hint, HorizontalAlignment.Right));
        for (var column = 0; column < widths.Length; column++)
        {
            var value = shown.Cells.ElementAtOrDefault(column) ?? "";
            if (row.Kind == TableChange.Changed && row.ChangedCells.Contains(column))
            {
                var before = row.Original!.Cells.ElementAtOrDefault(column) ?? "";
                var block = Block(widths[column], Ui.TextBrush, HorizontalAlignment.Left);
                if (before.Length > 0) block.Inlines!.Add(new Run(Shown(before)) { Foreground = Ui.Danger, TextDecorations = TextDecorations.Strikethrough });
                if (before.Length > 0 && value.Length > 0) block.Inlines!.Add(new Run(" "));
                if (value.Length > 0) block.Inlines!.Add(new Run(Shown(value)) { Foreground = Ui.Success });
                ToolTip.SetTip(block, before + " → " + value);
                var cell = Framed(block);
                cell.Classes.Add("table-cell-changed");
                cell.Background = Ui.WarningWash;
                panel.Children.Add(cell);
            }
            else panel.Children.Add(Cell(Shown(value), widths[column], row.Kind == TableChange.Removed ? Ui.Danger : row.Kind == TableChange.Added ? Ui.Success : Ui.TextBrush, HorizontalAlignment.Left, value));
        }
        return panel;
    }

    private static TextBlock Block(double width, IBrush color, HorizontalAlignment alignment) => new()
    {
        Width = width - 16,
        Foreground = color,
        FontFamily = Ui.CodeFont,
        FontSize = 12,
        TextAlignment = alignment == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        Inlines = []
    };

    private static Border Framed(Control child) => new()
    {
        BorderBrush = Ui.BorderBrush,
        BorderThickness = new Thickness(0, 0, 1, 1),
        Padding = new Thickness(8, 3),
        Child = child
    };

    private static Border Cell(string text, double width, IBrush color, HorizontalAlignment alignment, string? full = null)
    {
        var block = Block(width, color, alignment);
        block.Text = text;
        if (full is { Length: > MaxCellCharacters } || full?.Contains('\n') == true) ToolTip.SetTip(block, full);
        return Framed(block);
    }
}