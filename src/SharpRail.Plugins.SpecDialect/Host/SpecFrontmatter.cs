using System.Text.Json;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SharpRail.Plugins.SpecDialect;

/// <summary>An indexed spec: its path and frontmatter fields. The text stays on disk; grep and edits read it when they run.</summary>
internal sealed record SpecFile(string Path, SpecFields Frontmatter)
{
    public string Id => Frontmatter.Scalar("id")!;
    public string Type => Frontmatter.Scalar("type")!;
    public string? Title => Frontmatter.Scalar("title");
    public SpecGraphNode Node => new(Id, Type, Title ?? Id, Path, Frontmatter.List("depends-on"),
        Frontmatter.List("references"), Frontmatter.List("implements"), Frontmatter.List("tags"))
    { Parent = Frontmatter.Scalar("parent"), Status = Frontmatter.Scalar("status") };
}

/// <summary>A spec's frontmatter values, without the YAML tree or the text they were parsed from.</summary>
internal sealed class SpecFields(IReadOnlyDictionary<string, object> values)
{
    public Dictionary<string, object> Values => new(values);
    public string? Scalar(string key) => values.GetValueOrDefault(key) is string { Length: > 0 } value ? value : null;
    public IReadOnlyList<string> List(string key) => values.GetValueOrDefault(key) is string[] list ? list : Scalar(key) is { } value ? [value] : [];
    public IReadOnlyList<string> Targets(string kind) => kind == "parent" ? Scalar(kind) is { } parent ? [parent] : [] : List(kind);
}

internal sealed class SpecFrontmatter(YamlMappingNode mapping, string yaml, string body, bool bom, string newline)
{
    public static readonly string[] LinkKinds = ["parent", "depends-on", "references", "implements"];
    public static readonly string[] ListFields = ["depends-on", "references", "implements", "covers", "tags"];

    public bool IsSpec => Scalar("id") is not null && Scalar("type") is not null;
    private YamlNode? Field(string key) => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    public string? Scalar(string key) => Field(key) is YamlScalarNode { Value: { Length: > 0 } value } node && !IsNull(node) ? value : null;
    public IReadOnlyList<string> List(string key) => Field(key) is YamlSequenceNode sequence
        ? sequence.Children.Where(node => !IsNull(node)).Select(Value).ToArray()
        : Scalar(key) is { } value ? [value] : [];
    public IReadOnlyList<string> Targets(string kind) => kind == "parent" ? Scalar(kind) is { } parent ? [parent] : [] : List(kind);
    public SpecFields Fields => new(Values);
    public Dictionary<string, object> Values => mapping.Children.Where(pair => pair.Key is YamlScalarNode && !IsNull(pair.Value) && pair.Value is not YamlMappingNode)
        .ToDictionary(pair => ((YamlScalarNode)pair.Key).Value!, pair => pair.Value is YamlSequenceNode
            ? (object)List(((YamlScalarNode)pair.Key).Value!).ToArray() : Value(pair.Value));

    private static bool IsNull(YamlNode node) => node is YamlScalarNode scalar &&
        (scalar.Tag == "tag:yaml.org,2002:null" || scalar.Style is ScalarStyle.Plain or ScalarStyle.Any && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL");
    private static string Value(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value ?? "" : "[object Object]";

    public static SpecFrontmatter? Parse(string content)
    {
        var bom = content.StartsWith('\uFEFF');
        var text = bom ? content[1..] : content;
        var first = text.IndexOf('\n');
        if (first < 0 || text[..first].Trim() != "---") return null;
        var cursor = first + 1;
        while (cursor < text.Length)
        {
            var end = text.IndexOf('\n', cursor);
            if (end < 0) end = text.Length;
            if (text[cursor..end].Trim() == "---")
            {
                var yaml = text[(first + 1)..cursor];
                try
                {
                    var stream = new YamlStream();
                    stream.Load(new StringReader(yaml));
                    var root = stream.Documents.Count == 0 ? new YamlMappingNode() : stream.Documents[0].RootNode;
                    if (root is YamlScalarNode scalar && IsNull(scalar)) root = new YamlMappingNode();
                    return root is YamlMappingNode map && stream.Documents.Count <= 1
                        ? new(map, yaml, end < text.Length ? text[(end + 1)..] : "", bom, first > 0 && text[first - 1] == '\r' ? "\r\n" : "\n") : null;
                }
                catch (Exception error) when (error is YamlException or InvalidOperationException or ArgumentException) { return null; }
            }
            cursor = end + 1;
        }
        return null;
    }

    public static string Serialize(IReadOnlyDictionary<string, object> fields) => "---\n" +
        string.Concat(fields.Where(pair => pair.Value is string text ? text.Length > 0 : ((IReadOnlyList<string>)pair.Value).Count > 0)
            .Select(pair => Quote(pair.Key) + ": " + Render(pair.Value) + "\n")) + "---\n";

    private static string Quote(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_./ -]*$") &&
        !new[] { "null", "true", "false", "yes", "no", "on", "off" }.Contains(value.ToLowerInvariant())
            ? value : JsonSerializer.Serialize(value);
    private static string Render(object value) => value is string text ? Quote(text) : "[" + string.Join(", ", ((IReadOnlyList<string>)value).Select(Quote)) + "]";

    public (string? Content, string? Error) Update(SpecUpdateParams edit)
    {
        var replacements = new Dictionary<string, object?>();
        foreach (var (key, value) in edit.Set ?? [])
        {
            if (key == "id") return (null, "Cannot rename a spec's id via set.");
            if (ListFields.Contains(key)) return (null, $"Use addList/removeList to edit the list field \"{key}\".");
            replacements[key] = value;
        }
        foreach (var key in edit.Remove ?? [])
        {
            if (key is "id" or "type") return (null, $"Cannot remove protected field \"{key}\".");
            replacements[key] = null;
        }
        foreach (var field in ListFields)
        {
            var add = edit.AddList?.GetValueOrDefault(field) ?? [];
            var remove = edit.RemoveList?.GetValueOrDefault(field) ?? [];
            if (add.Count == 0 && remove.Count == 0) continue;
            var list = (add.Count == 0 ? List(field) : List(field).Concat(add).Distinct()).Where(value => !remove.Contains(value)).ToArray();
            replacements[field] = list.Length > 0 ? list : null;
        }
        if (replacements.TryGetValue("type", out var type) && type is not string { Length: > 0 })
            return (null, "Update would leave the file without a valid id and type.");

        // Edit only YAML node spans so unchanged fields, comments and prose keep their original text.
        var changes = new List<(int Start, int End, string Text)>();
        foreach (var (key, value) in replacements)
        {
            var pair = mapping.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode scalar && scalar.Value == key);
            if (pair.Key is null)
            {
                if (value is not null) changes.Add((yaml.Length, yaml.Length, Quote(key) + ": " + Render(value) + newline));
                continue;
            }
            var start = checked((int)pair.Value.Start.Index);
            var end = End(pair.Value);
            var comments = Comments(start, end);
            if (value is not null) changes.Add((start, end, Render(value) + comments + (yaml[start..end].EndsWith('\n') ? newline : "")));
            else changes.Add((checked((int)pair.Key.Start.Index), end, comments));
        }
        var updated = yaml;
        foreach (var change in changes.OrderByDescending(change => change.Start))
            updated = updated[..change.Start] + change.Text + updated[change.End..];
        var result = (bom ? "\uFEFF" : "") + "---" + newline + updated + "---" + newline + body;
        return Parse(result)?.IsSpec == true ? (result, null) : (null, "Update would leave the file without a valid id and type.");
    }

    private string Comments(int start, int end)
    {
        var scanner = new Scanner(new StringReader(yaml), skipComments: false);
        var comments = new List<string>();
        while (scanner.MoveNext())
            if (scanner.Current is YamlDotNet.Core.Tokens.Comment comment && comment.Start.Index >= start && comment.End.Index <= end)
                comments.Add("# " + comment.Value);
        return comments.Count == 0 ? "" : newline + string.Join(newline, comments) + newline;
    }

    private int End(YamlNode node)
    {
        if (node is YamlScalarNode) return checked((int)node.End.Index);
        // Representation nodes keep a collection's opening mark as End; the closing event has its real span.
        var parser = new Parser(new StringReader(yaml));
        while (parser.MoveNext())
        {
            if (parser.Current is not (SequenceStart or MappingStart) || parser.Current.Start.Index != node.Start.Index) continue;
            var depth = 1;
            while (parser.MoveNext())
            {
                if (parser.Current is SequenceStart or MappingStart) depth++;
                if (parser.Current is SequenceEnd or MappingEnd && --depth == 0)
                {
                    var end = checked((int)parser.Current.End.Index);
                    return node is YamlSequenceNode { Style: SequenceStyle.Flow } or YamlMappingNode { Style: MappingStyle.Flow }
                        && end < yaml.Length && yaml[end] is ']' or '}' ? end + 1 : end;
                }
            }
        }
        throw new InvalidOperationException("Missing YAML collection end.");
    }
}