using System.Text.RegularExpressions;

namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>A frontmatter value: exactly one of text, a sequence of scalars or a one-level mapping.</summary>
public sealed record FrontmatterProperty(string Key, string? Text = null, IReadOnlyList<string>? Items = null,
    IReadOnlyList<KeyValuePair<string, string>>? Entries = null);

/// <param name="Raw">The block's inner text as written; what the unreadable fallback shows.</param>
/// <param name="Readable">False when the YAML uses shapes the table does not speak, so the raw block is shown instead.</param>
/// <param name="Lines">The number of source lines the block occupies, fences included.</param>
public sealed record FrontmatterBlock(IReadOnlyList<FrontmatterProperty> Properties, string Raw, bool Readable, int Lines);

/// <summary>A spec is frontmatter carrying an <c>id</c> and one of the spec graph's own <c>type</c>s.</summary>
public sealed record SpecIdentity(string Id, string Type, string? Title);

/// <summary>
/// Reads a leading YAML frontmatter block as properties. Only top-level <c>key: scalar</c>, inline or
/// multi-line flow sequences, block lists of scalars and a one-level mapping of scalars are readable; any other
/// shape (deeper nesting, multiline strings, anchors) leaves the whole block unreadable rather than guessing.
/// </summary>
public static partial class Frontmatter
{
    public static readonly string[] SpecTypes = ["goal-and-requirements", "architecture-design", "module-design", "submodule-design", "task-spec"];

    [GeneratedRegex(@"^---[ \t]*\r?\n")] private static partial Regex Open();
    [GeneratedRegex(@"^(?:---|\.\.\.)[ \t]*$")] private static partial Regex Close();
    [GeneratedRegex(@"^([A-Za-z0-9_][A-Za-z0-9_ ./-]*):(.*)$")] private static partial Regex Key();
    [GeneratedRegex(@"^[ \t]+-[ \t]?(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^[ \t]+([A-Za-z0-9_][A-Za-z0-9_ ./-]*):(.*)$")] private static partial Regex MapItem();
    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|([^\]]+))?\]\]")] private static partial Regex WikiLink();

    public const string SpecScheme = "spec:";

    public static FrontmatterBlock? Parse(string content)
    {
        var open = Open().Match(content);
        if (!open.Success) return null;
        var lines = content[open.Length..].Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var close = Array.FindIndex(lines, line => Close().IsMatch(line));
        if (close < 0) return null;
        var body = lines[..close];
        var raw = string.Join('\n', body);
        FrontmatterBlock Unreadable() => new([], raw, false, close + 2);
        var properties = new List<FrontmatterProperty>();
        for (var index = 0; index < body.Length; index++)
        {
            var line = body[index];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var match = Key().Match(line);
            if (!match.Success) return Unreadable();
            var key = match.Groups[1].Value.Trim();
            var after = match.Groups[2].Value;
            if (after.Trim().Length == 0)
            {
                if (FlowSequence(body, index + 1, "") is { } flow)
                {
                    properties.Add(new(key, Items: flow.Items));
                    index = flow.Next - 1;
                    continue;
                }
                var items = new List<string>();
                var cursor = index + 1;
                for (; cursor < body.Length && ListItem().Match(body[cursor]) is { Success: true } item; cursor++)
                {
                    var text = item.Groups[1].Value.Trim();
                    if (!Quoted(text) && text.Contains(": ", StringComparison.Ordinal)) return Unreadable();
                    items.Add(Scalar(text));
                }
                if (items.Count > 0)
                {
                    properties.Add(new(key, Items: items));
                    index = cursor - 1;
                    continue;
                }
                var entries = new List<KeyValuePair<string, string>>();
                for (; cursor < body.Length && MapItem().Match(body[cursor]) is { Success: true } entry; cursor++)
                {
                    var subKey = entry.Groups[1].Value.Trim();
                    var subAfter = entry.Groups[2].Value.Trim();
                    if (subAfter.Length == 0 || entries.Any(existing => existing.Key == subKey) || Structured(subAfter)) return Unreadable();
                    entries.Add(new(subKey, Scalar(subAfter)));
                }
                properties.Add(entries.Count > 0 ? new(key, Entries: entries) : new(key, Text: ""));
                index = cursor - 1;
                continue;
            }
            if (InlineList(after) is { } inline) { properties.Add(new(key, Items: inline)); continue; }
            if (after.Trim().StartsWith('['))
            {
                if (FlowSequence(body, index + 1, after) is not { } flow) return Unreadable();
                properties.Add(new(key, Items: flow.Items));
                index = flow.Next - 1;
                continue;
            }
            if (Structured(after.Trim())) return Unreadable();
            properties.Add(new(key, Text: Scalar(after)));
        }
        return new(properties, raw, true, close + 2);
    }

    /// <summary>The spec a document declares, or null for ordinary Markdown, whatever its file name.</summary>
    public static SpecIdentity? Spec(FrontmatterBlock? block)
    {
        if (block is null) return null;
        string? At(string key) => block.Properties.FirstOrDefault(property => property.Key == key)?.Text is { } text && text.Trim().Length > 0 ? text.Trim() : null;
        return At("id") is { } id && At("type") is { } type && SpecTypes.Contains(type) ? new(id, type, At("title")) : null;
    }

    /// <summary>Rewrites <c>[[id]]</c> and <c>[[id|label]]</c> into ordinary links under the <c>spec:</c> scheme.</summary>
    public static string LinkifyWikiLinks(string text) => WikiLink().Replace(text, match =>
    {
        var target = match.Groups[1].Value.Trim();
        if (target.Length == 0) return match.Value;
        var label = match.Groups[2].Success && match.Groups[2].Value.Trim().Length > 0 ? match.Groups[2].Value.Trim() : target;
        return $"[{label}]({SpecScheme}{Uri.EscapeDataString(target)})";
    });

    private static bool Quoted(string text) => text.Length >= 2 && (text[0] == '"' && text[^1] == '"' || text[0] == '\'' && text[^1] == '\'');

    private static bool Structured(string scalar) =>
        !Quoted(scalar) && (scalar.Contains(": ", StringComparison.Ordinal) || scalar.StartsWith('{') || scalar.StartsWith('&'));

    private static string Scalar(string text)
    {
        var trimmed = text.Trim();
        if (!Quoted(trimmed)) return trimmed;
        var inner = trimmed[1..^1];
        return trimmed[0] == '"' ? inner.Replace("\\\"", "\"", StringComparison.Ordinal) : inner.Replace("''", "'", StringComparison.Ordinal);
    }

    private static List<string>? InlineList(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) return null;
        var inner = trimmed[1..^1].Trim();
        if (inner.Contains('[') || inner.Contains('{')) return null;
        return inner.Length == 0 ? [] : inner.Split(',').Select(Scalar).ToList();
    }

    // A flow sequence spread across lines (`key:`, then `[`, its items, `]`) reads as the same list.
    private static (List<string> Items, int Next)? FlowSequence(string[] body, int start, string head)
    {
        var text = head.Trim();
        var cursor = start;
        while (!text.EndsWith(']'))
        {
            if (cursor >= body.Length) return null;
            var line = body[cursor].Trim();
            if (line.Length == 0 || line.StartsWith('#')) return null;
            text = text.Length == 0 ? line : text + " " + line;
            cursor++;
        }
        if (!text.StartsWith('[')) return null;
        return InlineList(TrailingComma().Replace(text, "]")) is { } items ? (items, cursor) : null;
    }

    [GeneratedRegex(@",[ \t]*\]$")] private static partial Regex TrailingComma();
}