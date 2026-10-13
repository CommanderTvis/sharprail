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

    internal static string WithProperties(string content, IReadOnlyList<FrontmatterProperty> properties)
    {
        var block = Parse(content);
        if (block is not { Readable: true }) return content;
        var lines = content.Split('\n');
        var body = string.Join('\n', lines.Skip(block.Lines));
        if (properties.Count == 0) return body.StartsWith("\r\n", StringComparison.Ordinal) ? body[2..] : body.StartsWith('\n') ? body[1..] : body;
        var newline = lines[0].EndsWith('\r') ? "\r\n" : "\n";
        var serialized = new List<string>();
        foreach (var property in properties)
        {
            if (property.Items is { } items)
            {
                serialized.Add(property.Key + (items.Count == 0 ? ": []" : ":"));
                serialized.AddRange(items.Select(item => "  - " + WriteScalar(item)));
            }
            else if (property.Entries is { } entries)
            {
                serialized.Add(property.Key + (entries.Count == 0 ? ": {}" : ":"));
                serialized.AddRange(entries.Select(entry => "  " + entry.Key + ": " + WriteScalar(entry.Value)));
            }
            else serialized.Add(property.Key + ": " + WriteScalar(property.Text ?? ""));
        }
        return "---" + newline + string.Join(newline, serialized) + newline + "---" + (block.Lines < lines.Length ? newline + body : "");
    }

    private static string WriteScalar(string value) => value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '-' or '.' or '/')
        && value == value.Trim() && !new[] { "true", "false", "null", "~", "yes", "no" }.Contains(value, StringComparer.OrdinalIgnoreCase)
        && !value.StartsWith('-') ? value : JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}