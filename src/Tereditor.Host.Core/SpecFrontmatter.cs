namespace SharpRail.Host.Core;

/// <summary>
/// The frontmatter dialect specs use: a block between a first line of <c>---</c> and the next, holding top-level
/// <c>key: value</c> scalars, flow lists (<c>[a, b]</c>) and block lists (<c>- a</c>). A leading byte order mark
/// and CRLF line endings are tolerated; nested maps are not interpreted.
/// </summary>
internal static class SpecFrontmatter
{
    internal const string Fence = "---";

    /// <summary>The lines between the fences without their line endings, or null when the text has no closed block.</summary>
    internal static string[]? Block(string text, out int bodyStart)
    {
        bodyStart = 0;
        var start = text.StartsWith('﻿') ? 1 : 0;
        var lines = new List<string>();
        var position = start;
        for (var index = 0; position <= text.Length; index++)
        {
            var end = text.IndexOf('\n', position);
            var line = (end < 0 ? text[position..] : text[position..end]).TrimEnd('\r');
            var next = end < 0 ? text.Length + 1 : end + 1;
            if (index == 0) { if (line.Trim() != Fence) return null; }
            else if (line.Trim() == Fence) { bodyStart = Math.Min(next, text.Length); return lines.ToArray(); }
            else lines.Add(line);
            position = next;
        }
        return null;
    }

    internal static Dictionary<string, string[]>? Parse(string text)
    {
        if (Block(text, out _) is not { } lines) return null;
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (KeyOf(line) is not { } key) continue;
            var value = line[(KeyEnd(line) + 1)..].Trim();
            if (value.StartsWith('['))
            {
                // A flow list may continue over the following lines until its bracket closes.
                while (!Closed(value) && index + 1 < lines.Length) value += " " + lines[++index].Trim();
                result[key] = SplitFlow(value);
            }
            else if (value.Length == 0 || value[0] == '#')
            {
                var items = new List<string>();
                while (index + 1 < lines.Length && lines[index + 1].TrimStart() is { Length: > 0 } next && next[0] == '-' && (next.Length == 1 || next[1] == ' '))
                {
                    index++;
                    if (Scalar(next[1..].Trim()) is { Length: > 0 } item) items.Add(item);
                }
                if (items.Count > 0) result[key] = items.ToArray();
            }
            else if (Scalar(value) is { Length: > 0 } scalar) result[key] = [scalar];
        }
        return result;
    }

    /// <summary>The key a top-level line declares, or null for a comment, a list item, a continuation or a blank line.</summary>
    internal static string? KeyOf(string line) =>
        line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-' || KeyEnd(line) is not (> 0 and var colon) ? null : Unquote(line[..colon].Trim());

    /// <summary>The index of the colon that ends a key: followed by a space or the end of the line.</summary>
    private static int KeyEnd(string line)
    {
        for (var index = 1; index < line.Length; index++)
            if (line[index] == ':' && (index + 1 == line.Length || line[index + 1] == ' ')) return index;
        return -1;
    }

    private static bool Closed(string value)
    {
        char quote = '\0';
        foreach (var letter in value)
        {
            if (quote != '\0') { if (letter == quote) quote = '\0'; }
            else if (letter is '"' or '\'') quote = letter;
            else if (letter == ']') return true;
        }
        return false;
    }

    private static string[] SplitFlow(string value)
    {
        var items = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var letter in value.AsSpan(1))
        {
            if (quote != '\0') { current.Append(letter); if (letter == quote) quote = '\0'; }
            else if (letter is '"' or '\'') { quote = letter; current.Append(letter); }
            else if (letter is ',' or ']')
            {
                if (Scalar(current.ToString().Trim()) is { Length: > 0 } item) items.Add(item);
                current.Clear();
                if (letter == ']') break;
            }
            else current.Append(letter);
        }
        return items.ToArray();
    }

    private static string Scalar(string value)
    {
        if (value.Length > 0 && value[0] is '"' or '\'') return Unquote(value);
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] is not ('"' or '\'')) return value;
        var end = value.LastIndexOf(value[0]);
        if (end <= 0) return value;
        var inner = value[1..end];
        return value[0] == '\'' ? inner.Replace("''", "'") : inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    /// <summary>A scalar as it must be written so this dialect reads the same value back.</summary>
    internal static string Write(string value) =>
        value.Length == 0 || value != value.Trim() || value.AsSpan().IndexOfAny(":#[]{},\"'&*!|>%@`\n") >= 0 || value[0] is '-' or '?'
            ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\""
            : value;
}