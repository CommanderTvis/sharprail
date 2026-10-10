using System.Text;
using System.Text.RegularExpressions;

using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>
/// Merges two Markdown sources into one document whose changed words are wrapped in
/// &lt;ins&gt; and &lt;del&gt; tags, the native counterpart of the reference's htmldiff merge.
/// Lines are aligned first so unchanged regions of large files cost only a comparison;
/// only paired changed lines are diffed word by word.
/// </summary>
public static partial class MarkdownDiff
{
    // Beyond this many line edits the alignment falls back to replacing the changed region.
    private const int MaxEdits = 2000;

    private enum Op { Equal, Delete, Insert }

    private static readonly MarkdownPipeline TablePipeline = new MarkdownPipelineBuilder().UsePipeTables().UseYamlFrontMatter().Build();
    private sealed record Cell(int Start, int Length);
    private sealed record Row(string Text, Cell[] Cells);
    private sealed record TableSource(Row Header, string Separator, Row[] Rows);
    private sealed record Unit(string Text, TableSource? Table = null);

    public static string Merge(string before, string after, CancellationToken cancellationToken = default)
    {
        var oldUnits = Units(before, cancellationToken);
        var newUnits = Units(after, cancellationToken);
        var output = new StringBuilder(after.Length + 64);
        var fenced = false;
        var deleted = new List<Unit>();
        var inserted = new List<Unit>();

        void Emit(string line)
        {
            if (FenceMarker().IsMatch(line)) fenced = !fenced;
            output.Append(line).Append('\n');
        }

        void Flush()
        {
            var pairs = Math.Min(deleted.Count, inserted.Count);
            for (var index = 0; index < pairs; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldUnit = deleted[index];
                var newUnit = inserted[index];
                if (oldUnit.Table is { } oldTable && newUnit.Table is { } newTable && oldTable.Header.Cells.Length == newTable.Header.Cells.Length)
                    Emit(MergeTable(oldTable, newTable, cancellationToken));
                else if (oldUnit.Table is not null || newUnit.Table is not null)
                {
                    EmitUnit(oldUnit, "del");
                    Emit("");
                    EmitUnit(newUnit, "ins");
                }
                else if (fenced || FenceMarker().IsMatch(oldUnit.Text) || FenceMarker().IsMatch(newUnit.Text)) Emit(newUnit.Text);
                else Emit(MergeLine(oldUnit.Text, newUnit.Text, cancellationToken));
            }
            foreach (var unit in deleted.Skip(pairs))
                if (!fenced && !FenceMarker().IsMatch(unit.Text)) EmitUnit(unit, "del");
            foreach (var unit in inserted.Skip(pairs))
                if (fenced || FenceMarker().IsMatch(unit.Text)) Emit(unit.Text);
                else EmitUnit(unit, "ins");
            deleted.Clear(); inserted.Clear();
        }

        void EmitUnit(Unit unit, string tag)
        {
            if (unit.Table is { } table)
            {
                Emit(MarkRow(table.Header, tag));
                Emit(table.Separator);
                foreach (var row in table.Rows) Emit(MarkRow(row, tag));
                return;
            }
            var line = unit.Text;
            var prefix = BlockPrefix().Match(line).Length;
            if (line[prefix..].Trim().Length == 0) { if (tag == "ins") Emit(line); return; }
            Emit(line[..prefix] + Wrap(tag, line[prefix..]));
        }

        var oldIndex = 0;
        var newIndex = 0;
        foreach (var (op, line) in Align(oldUnits.Select(unit => unit.Text).ToArray(), newUnits.Select(unit => unit.Text).ToArray(), cancellationToken))
        {
            switch (op)
            {
                case Op.Equal: Flush(); Emit(line); oldIndex++; newIndex++; break;
                case Op.Delete: deleted.Add(oldUnits[oldIndex++]); break;
                default: inserted.Add(newUnits[newIndex++]); break;
            }
        }
        Flush();
        return output.ToString();
    }

    // Treat each pipe table as one alignment unit. Its structural syntax never receives diff tags.
    private static Unit[] Units(string source, CancellationToken cancellationToken)
    {
        var lines = Lines(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.Contains('|')) return lines.Select(line => new Unit(line)).ToArray();
        var offsets = new int[lines.Length];
        for (var index = 1; index < lines.Length; index++) offsets[index] = offsets[index - 1] + lines[index - 1].Length + 1;
        var document = Markdig.Markdown.Parse(string.Join('\n', lines), TablePipeline);
        var tables = document.Descendants<Table>().ToDictionary(table => table.Line);
        var units = new List<Unit>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tables.TryGetValue(index, out var table)) { units.Add(new(lines[index])); continue; }
            var rows = table.OfType<TableRow>().ToArray();
            var last = rows[^1].Line;
            var header = ReadRow(rows[0]);
            var body = rows.Skip(1).Select(ReadRow).ToArray();
            // A header-only table still includes its delimiter row.
            last = Math.Max(last, index + 1);
            units.Add(new(string.Join('\n', lines[index..(last + 1)]), new(header, lines[index + 1], body)));
            index = last;
        }
        return units.ToArray();

        Row ReadRow(TableRow row)
        {
            var text = lines[row.Line];
            var cells = row.OfType<TableCell>()
                .Where(cell => cell.Span.Start >= offsets[row.Line] && cell.Span.End < offsets[row.Line] + text.Length)
                .Select(cell => new Cell(cell.Span.Start - offsets[row.Line], cell.Span.Length)).ToArray();
            return new(text, cells);
        }
    }

    private static string MarkRow(Row row, string tag) => RewriteRow(row, (cell, _) => Wrap(tag, cell));

    private static string RewriteRow(Row row, Func<string, int, string> rewrite)
    {
        var result = new StringBuilder(row.Text.Length + 32);
        var position = 0;
        for (var index = 0; index < row.Cells.Length; index++)
        {
            var cell = row.Cells[index];
            result.Append(row.Text.AsSpan(position, cell.Start - position));
            result.Append(rewrite(row.Text.Substring(cell.Start, cell.Length), index));
            position = cell.Start + cell.Length;
        }
        result.Append(row.Text.AsSpan(position));
        return result.ToString();
    }

    private static string MergeRow(Row before, Row after, CancellationToken cancellationToken) =>
        RewriteRow(after, (cell, index) => index < before.Cells.Length
            ? MergeLine(before.Text.Substring(before.Cells[index].Start, before.Cells[index].Length), cell, cancellationToken)
            : Wrap("ins", cell));

    private static string MergeTable(TableSource before, TableSource after, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        output.AppendLine(MergeRow(before.Header, after.Header, cancellationToken));
        output.AppendLine(after.Separator);
        var deleted = new List<Row>();
        var inserted = new List<Row>();
        var oldIndex = 0;
        var newIndex = 0;
        void Flush()
        {
            var pairs = Math.Min(deleted.Count, inserted.Count);
            for (var index = 0; index < pairs; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deleted[index].Cells.Length == inserted[index].Cells.Length)
                    output.AppendLine(MergeRow(deleted[index], inserted[index], cancellationToken));
                else
                {
                    output.AppendLine(MarkRow(deleted[index], "del"));
                    output.AppendLine(MarkRow(inserted[index], "ins"));
                }
            }
            foreach (var row in deleted.Skip(pairs)) output.AppendLine(MarkRow(row, "del"));
            foreach (var row in inserted.Skip(pairs)) output.AppendLine(MarkRow(row, "ins"));
            deleted.Clear(); inserted.Clear();
        }
        foreach (var (op, text) in Align(before.Rows.Select(row => row.Text).ToArray(), after.Rows.Select(row => row.Text).ToArray(), cancellationToken))
        {
            switch (op)
            {
                case Op.Equal: Flush(); output.AppendLine(text); oldIndex++; newIndex++; break;
                case Op.Delete: deleted.Add(before.Rows[oldIndex++]); break;
                default: inserted.Add(after.Rows[newIndex++]); break;
            }
        }
        Flush();
        return output.ToString().TrimEnd('\n');
    }

    private static string[] Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static string MergeLine(string before, string after, CancellationToken cancellationToken)
    {
        var oldPrefix = BlockPrefix().Match(before).Value;
        var newPrefix = BlockPrefix().Match(after).Value;
        if (oldPrefix.Trim() != newPrefix.Trim())
        {
            var whole = new StringBuilder();
            if (before[oldPrefix.Length..].Trim().Length > 0) whole.Append(newPrefix).Append(Wrap("del", before[oldPrefix.Length..])).Append(' ');
            else whole.Append(newPrefix);
            whole.Append(Wrap("ins", after[newPrefix.Length..]));
            return whole.ToString();
        }
        var oldTokens = Tokens(before[oldPrefix.Length..]);
        var newTokens = Tokens(after[newPrefix.Length..]);
        // Like htmldiff, a changed region spans the whitespace between changed words, so each region
        // renders as one deletion followed by one insertion instead of alternating fragments.
        var merged = new StringBuilder(newPrefix);
        StringBuilder removed = new(), added = new(), spacing = new();
        var open = false;
        void Close()
        {
            if (open)
            {
                if (removed.ToString().Trim().Length > 0) merged.Append(Wrap("del", removed.ToString()));
                if (added.ToString().Trim().Length > 0) merged.Append(Wrap("ins", added.ToString()));
                removed.Clear(); added.Clear(); open = false;
            }
            merged.Append(spacing); spacing.Clear();
        }
        foreach (var (op, token) in Align(oldTokens, newTokens, cancellationToken))
        {
            if (op == Op.Equal && token.Trim().Length == 0 && open) { spacing.Append(token); continue; }
            if (op == Op.Equal) { Close(); merged.Append(token); continue; }
            if (open) { removed.Append(spacing); added.Append(spacing); spacing.Clear(); }
            open = true;
            (op == Op.Delete ? removed : added).Append(token);
        }
        Close();
        return merged.ToString();
    }

    // Whitespace stays outside the tags so emphasis delimiters and line structure keep their meaning.
    private static string Wrap(string tag, string text)
    {
        var start = text.Length - text.TrimStart().Length;
        var end = text.TrimEnd().Length;
        if (end <= start) return text;
        return text[..start] + "<" + tag + ">" + text[start..end] + "</" + tag + ">" + text[end..];
    }

    private static string[] Tokens(string text) => Token().Matches(text).Select(match => match.Value).ToArray();

    /// <summary>Aligns two sequences: -1 for a value only in <paramref name="a"/>, 0 in both, 1 only in <paramref name="b"/>.</summary>
    /// <summary>Aligns two sequences into unchanged, removed and added values.</summary>
    public static List<(int Side, string Value)> Sequence(string[] a, string[] b, CancellationToken cancellationToken = default) =>
        Align(a, b, cancellationToken).Select(step => (step.Op == Op.Delete ? -1 : step.Op == Op.Insert ? 1 : 0, step.Value)).ToList();

    // Myers' O(ND) alignment after trimming the common prefix and suffix.
    private static List<(Op Op, string Value)> Align(string[] a, string[] b, CancellationToken cancellationToken)
    {
        var result = new List<(Op, string)>(Math.Max(a.Length, b.Length));
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        for (var index = 0; index < prefix; index++) result.Add((Op.Equal, b[index]));
        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var x = new int[n]; var y = new int[m];
        for (var index = 0; index < n; index++) x[index] = Id(a[prefix + index]);
        for (var index = 0; index < m; index++) y[index] = Id(b[prefix + index]);
        var middle = Myers(x, y, cancellationToken);
        if (middle is null)
        {
            for (var index = 0; index < n; index++) result.Add((Op.Delete, a[prefix + index]));
            for (var index = 0; index < m; index++) result.Add((Op.Insert, b[prefix + index]));
        }
        else foreach (var (op, i, j) in middle)
            result.Add(op == Op.Insert ? (op, b[prefix + j]) : (op, a[prefix + i]));
        for (var index = b.Length - suffix; index < b.Length; index++) result.Add((Op.Equal, b[index]));
        return result;

        int Id(string value)
        {
            if (!ids.TryGetValue(value, out var id)) ids[value] = id = ids.Count;
            return id;
        }
    }

    private static List<(Op Op, int I, int J)>? Myers(int[] a, int[] b, CancellationToken cancellationToken)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var edits = new List<(Op, int, int)>(max);
        if (max == 0) return edits;
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            if (d > MaxEdits) return null;
            if ((d & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            // Keep only the diagonals round d can read: -d-1 through d+1.
            trace.Add(v[(offset - d - 1)..(offset + d + 2)]);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || k != d && v[offset + k - 1] < v[offset + k + 1] ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, n, m, d);
            }
        }
        return null;
    }

    private static List<(Op Op, int I, int J)> Backtrack(List<int[]> trace, int n, int m, int depth)
    {
        var edits = new List<(Op, int, int)>();
        int x = n, y = m;
        for (var d = depth; d > 0; d--)
        {
            var v = trace[d];
            var at = d + 1;
            var k = x - y;
            var previousK = k == -d || k != d && v[at + k - 1] < v[at + k + 1] ? k + 1 : k - 1;
            var previousX = v[at + previousK];
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY) { x--; y--; edits.Add((Op.Equal, x, y)); }
            if (x == previousX) { y--; edits.Add((Op.Insert, x, y)); }
            else { x--; edits.Add((Op.Delete, x, y)); }
        }
        while (x > 0 && y > 0) { x--; y--; edits.Add((Op.Equal, x, y)); }
        edits.Reverse();
        return edits;
    }

    [GeneratedRegex(@"^\s{0,3}(```|~~~)")]
    private static partial Regex FenceMarker();

    [GeneratedRegex(@"^(?:\s*(?:[-*+]\s+(?:\[[ xX]\]\s+)?|\d{1,9}[.)]\s+|#{1,6}\s+|>\s?))*\s*")]
    private static partial Regex BlockPrefix();

    [GeneratedRegex(@"\s+|\w+|[^\w\s]")]
    private static partial Regex Token();
}