using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>
/// A line diff, so an edit can be read before it is written. The point is spotting what an edit removed: a generated
/// config change that quietly drops a key is exactly the failure the approval step exists to catch.
/// </summary>
internal static partial class LineDiff
{
    private const int ContextLines = 3;

    public static IReadOnlyList<ClaudeDiffLine> Lines(string before, string after)
    {
        var from = before.Length == 0 ? [] : before.Split('\n');
        var to = after.Length == 0 ? [] : after.Split('\n');
        var lines = new List<ClaudeDiffLine>();
        int i = 0, j = 0;
        foreach (var (fromIndex, toIndex) in LongestCommonSubsequence(from, to))
        {
            while (i < fromIndex) lines.Add(new(ClaudeDiffKind.Remove, from[i++]));
            while (j < toIndex) lines.Add(new(ClaudeDiffKind.Add, to[j++]));
            lines.Add(new(ClaudeDiffKind.Context, from[fromIndex]));
            i = fromIndex + 1;
            j = toIndex + 1;
        }
        while (i < from.Length) lines.Add(new(ClaudeDiffKind.Remove, from[i++]));
        while (j < to.Length) lines.Add(new(ClaudeDiffKind.Add, to[j++]));
        return Elide(lines);
    }

    // Unchanged runs far from any change collapse: ~/.claude.json holds tokens, and a diff is not the place to show them.
    private static List<ClaudeDiffLine> Elide(List<ClaudeDiffLine> lines)
    {
        var near = new HashSet<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Kind == ClaudeDiffKind.Context) continue;
            for (var at = index - ContextLines; at <= index + ContextLines; at++) near.Add(at);
        }
        var output = new List<ClaudeDiffLine>();
        var skipped = 0;
        void Flush()
        {
            if (skipped == 0) return;
            output.Add(new(ClaudeDiffKind.Gap, $"{skipped} unchanged line{(skipped == 1 ? "" : "s")}"));
            skipped = 0;
        }
        for (var index = 0; index < lines.Count; index++)
        {
            if (near.Contains(index)) { Flush(); output.Add(lines[index]); }
            else skipped++;
        }
        Flush();
        return output;
    }

    private static List<(int, int)> LongestCommonSubsequence(string[] from, string[] to)
    {
        var table = new int[from.Length + 1, to.Length + 1];
        for (var i = from.Length - 1; i >= 0; i--)
            for (var j = to.Length - 1; j >= 0; j--)
                table[i, j] = from[i] == to[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        var pairs = new List<(int, int)>();
        int a = 0, b = 0;
        while (a < from.Length && b < to.Length)
        {
            if (from[a] == to[b]) { pairs.Add((a, b)); a++; b++; continue; }
            if (table[a + 1, b] >= table[a, b + 1]) a++;
            else b++;
        }
        return pairs;
    }

    [GeneratedRegex(@"\n([ \t]+)\S")]
    private static partial Regex Indent();

    /// <summary>Re-serializes JSON the way the file already writes it, so an edit shows as the lines it changed.</summary>
    public static string FormatJson(string existing, JsonNode value)
    {
        var indent = Indent().Match(existing) is { Success: true } match ? match.Groups[1].Value : "  ";
        var trailingNewline = existing.Length == 0 || existing.EndsWith('\n');
        return Json.Stringify(value, indent) + (trailingNewline ? "\n" : "");
    }
}