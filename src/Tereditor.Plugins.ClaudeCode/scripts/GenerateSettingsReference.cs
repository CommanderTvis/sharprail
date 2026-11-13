// Regenerates ClaudeSettingsReference.cs from Claude Code's settings reference: every documented key with its anchor,
// the index's one-line summary, its scope, type, default and closed value set, and every heading anchor of the page.
// Run by hand from this directory and read the diff: `dotnet run --file GenerateSettingsReference.cs`, or `-- --check`
// to compare. The reference is the source rather than the schemastore schema (json.schemastore.org/claude-code-settings.json),
// which on 2026-11-13 lacked about sixty documented keys.
using System.Text;
using System.Text.RegularExpressions;

const string page = "https://code.claude.com/docs/en/settings-reference";
var target = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", "ClaudeSettingsReference.cs"));

using var http = new HttpClient();
async Task<string?> Read(string url)
{
    using var response = await http.GetAsync(url);
    if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();
    Console.Error.WriteLine($"could not read {url}: {(int)response.StatusCode}");
    return null;
}
// The Markdown carries the index and the entries; the HTML carries the heading ids a link can land on.
if (await Read(page + ".md") is not { } markdown || await Read(page) is not { } html) return 1;
markdown = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);

static string Plain(string text) =>
    Regex.Replace(Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1"), @"\*\*|`", "").Trim();

// The first clause: up to a sentence end or a semicolon, so a long explanation does not become the label.
static string Clause(string text)
{
    var cut = Regex.Match(text, @";|\.\s");
    var clause = (cut.Success ? text[..cut.Index] : text).TrimEnd(':', '.', ' ');
    if (clause.Count(character => character == '"') % 2 == 1) clause = clause.TrimEnd('"', ' ');
    return clause.Length > 140 ? clause[..139].TrimEnd() + "…" : clause;
}

var anchors = Regex.Matches(html, "<h[2-6][^>]*\\sid=\"([^\"]+)\"").Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();
var index = Regex.Matches(markdown, @"^\| \[`([^`]+)`\]\(#([^)]+)\) \| (.*) \| (.*?) \| (.*?) \|$", RegexOptions.Multiline);
var rows = new List<(string Key, string Anchor, string Summary, string Scope, string Kind, string Type, string? Default, List<string>? Values)>();
foreach (Match row in index)
{
    var key = row.Groups[1].Value;
    var anchor = row.Groups[2].Value;
    if (!anchors.Contains(anchor))
    {
        Console.Error.WriteLine($"{key} links to #{anchor}, which the page has no heading for");
        return 1;
    }
    var heading = Regex.Match(markdown, $@"^#{{2,4}} `{Regex.Escape(key)}`\n", RegexOptions.Multiline);
    if (!heading.Success)
    {
        Console.Error.WriteLine($"{key} is in the index but has no entry");
        return 1;
    }
    var end = Regex.Match(markdown[(heading.Index + heading.Length)..], @"^#{1,3} ", RegexOptions.Multiline);
    var entry = end.Success ? markdown.Substring(heading.Index + heading.Length, end.Index) : markdown[(heading.Index + heading.Length)..];
    var typeLine = Regex.Match(entry, @"^\* \*\*Type\*\*: (.*)$", RegexOptions.Multiline);
    var defaultLine = Regex.Match(entry, @"^\* \*\*Default\*\*: (.*)$", RegexOptions.Multiline);
    var type = typeLine.Success ? typeLine.Groups[1].Value : "";
    var kind = type switch
    {
        _ when type.Contains(" in the [`", StringComparison.Ordinal) => "", // an item of another key's array, not a key of its own
        _ when type.StartsWith("Boolean", StringComparison.Ordinal) => "boolean",
        _ when type.StartsWith("number", StringComparison.Ordinal) || type.StartsWith("integer", StringComparison.Ordinal) => "number",
        _ when type.StartsWith("string", StringComparison.Ordinal) || type.StartsWith("the string", StringComparison.Ordinal) => "string",
        _ when type.StartsWith("array", StringComparison.Ordinal) && !type.Contains("object", StringComparison.Ordinal) => "array", // a list of text
        _ when type.StartsWith("object", StringComparison.Ordinal) || type.StartsWith("array", StringComparison.Ordinal) => "object", // structured, edited as a file
        _ => type.Length > 0 ? "object" : "" // a removed key documents no type
    };
    List<string>? values = null;
    if (kind == "string")
    {
        // A closed set is listed as bullets after "one of:", or inline as quoted literals with nothing open beside them.
        if (type.EndsWith("one of:", StringComparison.Ordinal))
        {
            var after = entry[(typeLine.Index + typeLine.Length)..];
            values = [.. after.Split('\n').Skip(1).TakeWhile(line => line.StartsWith("  * ", StringComparison.Ordinal))
                .Select(line => Regex.Match(line, "^  \\* `\"([^\"`]+)\"`")).Where(match => match.Success).Select(match => match.Groups[1].Value)];
        }
        else if (Regex.IsMatch(type, "^(string, (one of )?|the string )`\"") && !Regex.IsMatch(Clause(type), @"\b(or|any) (a|an|the|full)\b"))
            values = [.. Regex.Matches(Clause(type), "`\"([^\"`]+)\"`").Select(match => match.Groups[1].Value)];
        if (values is { Count: 0 }) values = null;
    }
    rows.Add((key, anchor, Plain(row.Groups[3].Value), Plain(row.Groups[5].Value), kind, Clause(Plain(type)),
        defaultLine.Success ? Clause(Plain(defaultLine.Groups[1].Value)) : null, values));
}
if (rows.Count < 150)
{
    Console.Error.WriteLine($"only {rows.Count} keys parsed; the reference's shape changed, so fix the parser");
    return 1;
}

static string Quote(string? text) => text is null ? "null"
    : "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
var rendered = new StringBuilder();
rendered.Append($$"""
    // The settings keys Claude Code documents, as its settings reference lists them: each key's anchor on that page, the
    // index's one-line summary, the files it may go in, and the first clause of its type and default. The full
    // explanation stays where Anthropic maintains it; every row links there. An unlisted key means "newer than this
    // list", never "invalid".
    // Source: {{page}} (its .md form for the text, the page itself for the heading anchors).
    // Generated by `dotnet run --file scripts/GenerateSettingsReference.cs`, run by hand and its diff read; see SPEC.md.

    namespace Tereditor.Plugins.ClaudeCode;

    public static partial class ClaudeSettingsReference
    {
        public const string DocsUrl = "{{page}}";

        /// <summary>Every documented key, in the reference's index order.</summary>
        public static readonly IReadOnlyList<ClaudeDocumentedSetting> All =
        [

    """);
foreach (var row in rows)
    rendered.Append($"        new({Quote(row.Key)}, {Quote(row.Anchor)}, {Quote(row.Summary)}, {Quote(row.Scope)}, {Quote(row.Kind)}, {Quote(row.Type)}, {Quote(row.Default)}, " +
        (row.Values is null ? "null" : "[" + string.Join(", ", row.Values.Select(Quote)) + "]") + "),\n");
rendered.Append("    ];\n\n    /// <summary>The ids of every heading on the page, so a link is checked offline against an anchor that exists.</summary>\n");
rendered.Append("    public static readonly IReadOnlySet<string> Anchors = new HashSet<string>(StringComparer.Ordinal)\n    {\n");
foreach (var chunk in anchors.Chunk(6)) rendered.Append("        " + string.Join(", ", chunk.Select(Quote)) + ",\n");
rendered.Append("    };\n}");

if (args.Contains("--check"))
{
    var current = File.Exists(target) ? File.ReadAllText(target) : "";
    if (current != rendered.ToString())
    {
        Console.Error.WriteLine("ClaudeSettingsReference.cs is out of date with the reference");
        return 1;
    }
    return 0;
}
File.WriteAllText(target, rendered.ToString());
Console.WriteLine($"{rows.Count} keys, {anchors.Count} anchors -> {target}");
return 0;
