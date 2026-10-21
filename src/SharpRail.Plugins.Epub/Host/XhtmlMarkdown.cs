using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SharpRail.Plugins.Epub.Host;

/// <summary>
/// Converts one XHTML content document to Markdown blocks for the kit's renderer. Structure survives (headings,
/// paragraphs, lists, block quotes, preformatted text, tables, rules, images, links, emphasis, code); styling does
/// not: CSS, inline styles, fonts and colours are dropped so the theme decides how the book reads.
/// </summary>
internal sealed partial class XhtmlMarkdown
{
    /// <summary>One top-level Markdown block, with the element ids that resolve to it.</summary>
    internal sealed record Block(IReadOnlyList<string> Lines, IReadOnlyList<string> Ids);

    private static readonly HashSet<string> Skipped = ["head", "script", "style", "title", "noscript", "template", "math"];
    private static readonly HashSet<string> Containers =
        ["body", "div", "section", "article", "main", "header", "footer", "aside", "nav", "figure", "center", "html", "hgroup", "details", "summary", "address"];
    private static readonly HashSet<string> BlockTags =
        [.. Containers, "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre", "hr", "table", "dl", "dt", "dd", "figcaption", "svg"];

    private readonly Func<string, string> image;
    private readonly Func<string, string> link;

    /// <param name="image">Maps an image reference, relative to the document, to the URL the Markdown uses.</param>
    /// <param name="link">Maps a link target, relative to the document, to the URL the Markdown uses.</param>
    internal XhtmlMarkdown(Func<string, string> image, Func<string, string> link) { this.image = image; this.link = link; }

    internal IReadOnlyList<Block> Convert(string xhtml)
    {
        XElement? root;
        try { root = Parse(xhtml); }
        catch (XmlException) { root = null; }
        if (root is null)
        {
            // Not well-formed: keep the words, one paragraph per blank-line-separated run.
            var text = WebUtility.HtmlDecode(Tags().Replace(xhtml, " "));
            return [.. text.Split("\n\n").Select(Collapse).Where(part => part.Length > 0).Select(part => new Block([Escape(part)], []))];
        }
        var body = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "body") ?? root;
        return Blocks(body.Nodes(), [.. Ids(body)]);
    }

    private static XElement? Parse(string xhtml)
    {
        // EPUB 2 documents may lean on the XHTML DTD's named entities; XML knows only five, so decode the rest first.
        var text = NamedEntity().Replace(xhtml, match => match.Groups[1].Value is "amp" or "lt" or "gt" or "quot" or "apos"
            ? match.Value
            : WebUtility.HtmlDecode(match.Value) is var decoded && decoded != match.Value ? SecurityElement(decoded) : match.Value);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader).Root;
    }

    private static string SecurityElement(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;");

    private List<Block> Blocks(IEnumerable<XNode> nodes, List<string> leadIds)
    {
        var blocks = new List<Block>();
        var inline = new List<XNode>();
        void Add(IReadOnlyList<string> lines, IEnumerable<string> ids)
        {
            if (lines.Count == 0 || lines.All(string.IsNullOrWhiteSpace))
            {
                leadIds.AddRange(ids);
                return;
            }
            blocks.Add(new(lines, [.. leadIds, .. ids]));
            leadIds.Clear();
        }
        void Flush()
        {
            if (inline.Count == 0) return;
            var text = Inline(inline).Trim();
            Add(text.Length == 0 ? [] : text.Split('\n'), inline.OfType<XElement>().SelectMany(element => element.DescendantsAndSelf()).SelectMany(Ids));
            inline.Clear();
        }
        foreach (var node in nodes)
        {
            if (node is not XElement element) { inline.Add(node); continue; }
            var name = element.Name.LocalName.ToLowerInvariant();
            if (Skipped.Contains(name)) continue;
            if (!IsBlock(element)) { inline.Add(node); continue; }
            Flush();
            if (Containers.Contains(name) && element.Elements().Any(IsBlock))
            {
                var ids = Ids(element).ToList();
                var nested = Blocks(element.Nodes(), [.. leadIds, .. ids]);
                leadIds.Clear();
                blocks.AddRange(nested);
                continue;
            }
            Add(Render(element, name), element.DescendantsAndSelf().SelectMany(Ids));
        }
        Flush();
        if (leadIds.Count > 0 && blocks.Count > 0) blocks[^1] = blocks[^1] with { Ids = [.. blocks[^1].Ids, .. leadIds] };
        return blocks;
    }

    private static bool IsBlock(XElement element) =>
        BlockTags.Contains(element.Name.LocalName.ToLowerInvariant()) || element.Name.LocalName == "img" && element.Parent?.Elements().Count() == 1 && Collapse(element.Parent.Value).Length == 0;

    private static IEnumerable<string> Ids(XNode node) =>
        node is XElement element ? new[] { element.Attribute("id")?.Value, element.Name.LocalName == "a" ? element.Attribute("name")?.Value : null }.OfType<string>() : [];

    private List<string> Render(XElement element, string name)
    {
        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var title = OneLine(Inline(element.Nodes()));
                return title.Length == 0 ? [] : [new string('#', name[1] - '0') + " " + title];
            case "ul" or "ol":
                return List(element, name == "ol");
            case "blockquote":
                return Prefix(Join(Blocks(element.Nodes(), [])), "> ", "> ");
            case "pre":
                var code = element.Value.Replace("\r\n", "\n").TrimEnd('\n');
                var fence = new string('`', Math.Max(3, LongestRun(code, '`') + 1));
                return [fence, .. code.Split('\n'), fence];
            case "hr":
                return ["***"];
            case "table":
                return Table(element);
            case "dt":
                var term = OneLine(Inline(element.Nodes()));
                return term.Length == 0 ? [] : ["**" + term + "**"];
            case "svg":
                var picture = element.Descendants().FirstOrDefault(child => child.Name.LocalName == "image");
                var href = picture?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "href")?.Value;
                return href is null ? [] : ["![](" + image(href) + ")"];
            case "img":
                return [Inline([element])];
            default:
                if (element.Elements().Any(IsBlock)) return Join(Blocks(element.Nodes(), []));
                var text = Inline(element.Nodes()).Trim();
                return text.Length == 0 ? [] : [.. text.Split('\n')];
        }
    }

    private static List<string> Join(IEnumerable<Block> blocks)
    {
        var lines = new List<string>();
        foreach (var block in blocks)
        {
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(block.Lines);
        }
        return lines;
    }

    private static List<string> Prefix(List<string> lines, string first, string rest)
    {
        for (var index = 0; index < lines.Count; index++)
            lines[index] = (index == 0 ? first : rest) + lines[index];
        return lines;
    }

    private List<string> List(XElement list, bool ordered)
    {
        var lines = new List<string>();
        var number = int.TryParse(list.Attribute("start")?.Value, out var start) ? start : 1;
        foreach (var item in list.Elements().Where(child => child.Name.LocalName == "li"))
        {
            var body = Join(Blocks(item.Nodes(), []));
            if (body.Count == 0) body.Add("");
            var marker = ordered ? number++ + ". " : "- ";
            lines.AddRange(Prefix(body, marker, new string(' ', marker.Length)).Select(line => line.Trim().Length == 0 ? "" : line));
        }
        return lines;
    }

    private List<string> Table(XElement table)
    {
        var rows = table.Descendants().Where(child => child.Name.LocalName == "tr")
            .Select(row => row.Elements().Where(cell => cell.Name.LocalName is "td" or "th").Select(cell => OneLine(Inline(cell.Nodes())).Replace("|", "\\|")).ToList())
            .Where(row => row.Count > 0).ToList();
        if (rows.Count == 0) return [];
        var width = rows.Max(row => row.Count);
        string Row(List<string> cells) => "| " + string.Join(" | ", cells.Concat(Enumerable.Repeat("", width - cells.Count))) + " |";
        return [Row(rows[0]), "|" + string.Concat(Enumerable.Repeat(" --- |", width)), .. rows.Skip(1).Select(Row)];
    }

    private string Inline(IEnumerable<XNode> nodes)
    {
        var text = new StringBuilder();
        foreach (var node in nodes) Inline(node, text);
        // Collapse runs of spaces left by joined text nodes, but keep hard breaks.
        return string.Join('\n', text.ToString().Split('\n').Select(line => Spaces().Replace(line, " ")));
    }

    private void Inline(XNode node, StringBuilder text)
    {
        if (node is XText run) { text.Append(Escape(Whitespace().Replace(run.Value, " "))); return; }
        if (node is not XElement element) return;
        var name = element.Name.LocalName.ToLowerInvariant();
        if (Skipped.Contains(name)) return;
        switch (name)
        {
            case "br":
                text.Append("\\\n");
                return;
            case "img":
                var source = element.Attribute("src")?.Value;
                if (!string.IsNullOrEmpty(source)) text.Append("![").Append(Escape(element.Attribute("alt")?.Value ?? "")).Append("](").Append(image(source)).Append(')');
                return;
            case "em" or "i" or "cite" or "dfn" or "var":
                Wrap(element, "*", text);
                return;
            case "strong" or "b":
                Wrap(element, "**", text);
                return;
            case "code" or "kbd" or "tt" or "samp":
                var code = Collapse(element.Value);
                if (code.Length == 0) return;
                var ticks = new string('`', LongestRun(code, '`') + 1);
                text.Append(ticks).Append(code.StartsWith('`') || code.EndsWith('`') ? " " + code + " " : code).Append(ticks);
                return;
            case "a" when element.Attribute("href")?.Value is { Length: > 0 } href:
                var label = OneLine(Inline(element.Nodes()));
                if (label.Length == 0) { text.Append(Inline(element.Nodes())); return; }
                text.Append('[').Append(label).Append("](").Append(link(href)).Append(')');
                return;
            default:
                var block = BlockTags.Contains(name);
                if (block) text.Append(' ');
                foreach (var child in element.Nodes()) Inline(child, text);
                if (block) text.Append(' ');
                return;
        }
    }

    private void Wrap(XElement element, string marker, StringBuilder text)
    {
        var inner = OneLine(Inline(element.Nodes()));
        if (inner.Length == 0) return;
        var raw = Inline(element.Nodes());
        if (raw.StartsWith(' ')) text.Append(' ');
        text.Append(marker).Append(inner).Append(marker);
        if (raw.EndsWith(' ')) text.Append(' ');
    }

    private static string OneLine(string text) => Collapse(text.Replace("\\\n", " ").Replace('\n', ' '));

    internal static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    private static int LongestRun(string text, char character)
    {
        int longest = 0, run = 0;
        foreach (var current in text)
        {
            run = current == character ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }

    /// <summary>Escapes every character Markdown or the renderer's extensions could read as syntax.</summary>
    internal static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '|' or '~' or '^' or '=' or '$' or ':' or '&')
                escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex NamedEntity();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex Spaces();
}