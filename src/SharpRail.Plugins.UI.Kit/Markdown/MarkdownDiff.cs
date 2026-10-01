using System.Text;
using System.Text.RegularExpressions;

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

    public static string Merge(string before, string after, CancellationToken cancellationToken = default)
    {
        var oldLines = Lines(before);
        var newLines = Lines(after);
        var output = new StringBuilder(after.Length + 64);
        var fenced = false;
        var deleted = new List<string>();
        var inserted = new List<string>();

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
                if (fenced || FenceMarker().IsMatch(deleted[index]) || FenceMarker().IsMatch(inserted[index])) Emit(inserted[index]);
                else Emit(MergeLine(deleted[index], inserted[index], cancellationToken));
            }
            foreach (var line in deleted.Skip(pairs))
                if (!fenced && !FenceMarker().IsMatch(line)) EmitWhole(line, "del");
            foreach (var line in inserted.Skip(pairs))
                if (fenced || FenceMarker().IsMatch(line)) Emit(line);
                else EmitWhole(line, "ins");
            deleted.Clear(); inserted.Clear();
        }

        void EmitWhole(string line, string tag)
        {
            var prefix = BlockPrefix().Match(line).Length;
            if (line[prefix..].Trim().Length == 0) { if (tag == "ins") Emit(line); return; }
            Emit(line[..prefix] + Wrap(tag, line[prefix..]));
        }

        foreach (var (op, line) in Align(oldLines, newLines, cancellationToken))
        {
            switch (op)
            {
                case Op.Equal: Flush(); Emit(line); break;
                case Op.Delete: deleted.Add(line); break;
                default: inserted.Add(line); break;
            }
        }
        Flush();
        return output.ToString();
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