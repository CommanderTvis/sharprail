using System.Text.Json;

namespace SharpRail.Plugins.UI.Kit.Markdown;

public static partial class Frontmatter
{
    internal static bool ValidKey(string key)
    {
        var match = Key().Match(key + ":");
        return match.Success && match.Groups[1].Value == key;
    }

    internal static string Shape(FrontmatterProperty property) => property.Items is not null ? "Sequence" : property.Entries is not null ? "Mapping" : "Text";

    internal static FrontmatterProperty Convert(FrontmatterProperty property, string shape)
    {
        if (Shape(property) == shape) return property;
        var items = property.Items ?? property.Entries?.Select(entry => entry.Key + ": " + entry.Value).ToArray()
            ?? (string.IsNullOrWhiteSpace(property.Text) ? [] : new[] { property.Text! });
        if (shape == "Text")
        {
            var text = property.Items is not null ? "[" + string.Join(", ", items) + "]"
                : "{" + string.Join(", ", items) + "}";
            return new(property.Key, Text: text);
        }
        if (shape == "Sequence") return new(property.Key, Items: items);
        var pairs = items.Select((item, index) => item.IndexOf(": ", StringComparison.Ordinal) is var split && split > 0
            ? new KeyValuePair<string, string>(item[..split], item[(split + 2)..]) : new((index + 1).ToString(), item)).ToArray();
        if (pairs.Select(pair => pair.Key).Distinct().Count() != pairs.Length || pairs.Any(pair => !ValidKey(pair.Key)))
            pairs = items.Select((item, index) => new KeyValuePair<string, string>((index + 1).ToString(), item)).ToArray();
        return new(property.Key, Entries: pairs);
    }

    /// <summary>
    /// Writes <paramref name="properties"/> back into the leading frontmatter block and keeps everything else byte for
    /// byte: the body, leading comments, and the source lines (quoting, comments, spacing) of every property whose value
    /// did not change. Only changed or new properties are re-serialized. An unreadable block is returned untouched.
    /// </summary>
    internal static string WithProperties(string content, IReadOnlyList<FrontmatterProperty> properties)
    {
        var block = Parse(content);
        if (block is not { Readable: true }) return content;
        var lines = content.Split('\n');
        var body = string.Join('\n', lines.Skip(block.Lines));
        if (properties.Count == 0) return body.StartsWith("\r\n", StringComparison.Ordinal) ? body[2..] : body.StartsWith('\n') ? body[1..] : body;
        var newline = lines[0].EndsWith('\r') ? "\r\n" : "\n";
        // The source lines of each original property: from its key line up to the next top-level key line.
        var inner = lines.Skip(1).Take(block.Lines - 2).ToArray();
        var starts = inner.Select((line, index) => (line, index)).Where(pair => Key().IsMatch(pair.line)).Select(pair => pair.index).ToArray();
        var head = inner.Take(starts.Length > 0 ? starts[0] : inner.Length).ToArray();
        var segments = new Dictionary<string, string[]>();
        for (var n = 0; n < starts.Length; n++)
        {
            var end = n + 1 < starts.Length ? starts[n + 1] : inner.Length;
            segments[Key().Match(inner[starts[n]]).Groups[1].Value.Trim()] = inner[starts[n]..end];
        }
        var originals = block.Properties.ToDictionary(property => property.Key);
        var output = new List<string>(head);
        foreach (var property in properties)
        {
            if (originals.TryGetValue(property.Key, out var original) && Equal(original, property) && segments.TryGetValue(property.Key, out var kept))
            {
                output.AddRange(kept);
                continue;
            }
            if (property.Items is { } items)
            {
                output.Add(property.Key + (items.Count == 0 ? ": []" : ":"));
                output.AddRange(items.Select(item => "  - " + WriteScalar(item)));
            }
            else if (property.Entries is { } entries)
            {
                output.Add(property.Key + (entries.Count == 0 ? ": {}" : ":"));
                output.AddRange(entries.Select(entry => "  " + entry.Key + ": " + WriteScalar(entry.Value)));
            }
            else output.Add(property.Key + ": " + WriteScalar(property.Text ?? ""));
        }
        // Kept lines carry their own trailing \r; fresh ones take the document's line ending.
        var text = string.Join(newline, output.Select(line => line.TrimEnd('\r')));
        return "---" + newline + text + newline + "---" + (block.Lines < lines.Length ? newline + body : "");
    }

    private static bool Equal(FrontmatterProperty left, FrontmatterProperty right) => Shape(left) == Shape(right) && left.Text == right.Text
        && (left.Items ?? []).SequenceEqual(right.Items ?? []) && (left.Entries ?? []).SequenceEqual(right.Entries ?? []);

    private static string WriteScalar(string value) => value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-' or '.' or '/')
        && value == value.Trim() && !new[] { "true", "false", "null", "~", "yes", "no" }.Contains(value, StringComparer.OrdinalIgnoreCase)
        && !value.StartsWith('-') ? value : JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}