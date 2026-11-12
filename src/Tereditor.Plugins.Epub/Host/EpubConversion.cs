using System.Text;
using System.Text.RegularExpressions;

using VersOne.Epub;
using VersOne.Epub.Options;

namespace SharpRail.Plugins.Epub.Host;

/// <summary>A parsed package: the converted book and its byte resources by package path.</summary>
internal sealed record ConvertedBook(EpubBook Book, IReadOnlyDictionary<string, byte[]> Resources);

/// <summary>Reads an EPUB 2 or 3 package with VersOne.Epub and joins its reading order into one Markdown document.</summary>
internal static partial class EpubConversion
{
    internal static ConvertedBook Convert(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var book = EpubReader.ReadBook(stream, new EpubReaderOptions(EpubReaderOptionsPreset.RELAXED))
            ?? throw new InvalidDataException("Not an EPUB package.");
        var resources = (book.Content?.AllFiles?.Local ?? []).OfType<EpubLocalByteContentFile>()
            .GroupBy(file => file.FilePath, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Content, StringComparer.Ordinal);
        var lines = new List<string>();
        var chapters = new List<EpubChapter>();
        var targets = new List<(string File, string? Anchor)>();
        var anchors = new Dictionary<(string File, string Id), int>();
        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Append(IReadOnlyList<string> block)
        {
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(block);
        }
        var cover = book.Content?.Cover?.FilePath;
        if (cover is not null && !book.ReadingOrder.Any(chapter => chapter.Content.Contains(Path.GetFileName(cover), StringComparison.Ordinal)))
            Append(["![Cover](" + EpubContract.ResourcePrefix + EscapePath(cover) + ")"]);
        foreach (var chapter in book.ReadingOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = chapter.FilePath;
            var converter = new XhtmlMarkdown(
                href => Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme != "file"
                    ? absolute.AbsoluteUri : EpubContract.ResourcePrefix + EscapePath(Resolve(file, href).File),
                href =>
                {
                    if (href.StartsWith('#')) { targets.Add((file, href[1..])); return Placeholder(targets.Count - 1); }
                    if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)) return absolute.AbsoluteUri.Replace("(", "%28").Replace(")", "%29").Replace(" ", "%20");
                    targets.Add(Resolve(file, href));
                    return Placeholder(targets.Count - 1);
                });
            var start = lines.Count + (lines.Count > 0 ? 2 : 1);
            starts[file] = start;
            chapters.Add(new(file, start));
            foreach (var block in converter.Convert(chapter.Content))
            {
                var line = lines.Count + (lines.Count > 0 ? 2 : 1);
                foreach (var id in block.Ids) anchors.TryAdd((file, id), line);
                Append(block.Lines);
            }
        }
        int Line(string file, string? anchor) =>
            anchor is { Length: > 0 } && anchors.TryGetValue((file, anchor), out var line) ? line : starts.GetValueOrDefault(file, 1);
        var markdown = Placeholders().Replace(string.Join('\n', lines), match =>
        {
            var (file, anchor) = targets[int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
            return EpubContract.LinePrefix + Line(file, anchor);
        });
        var toc = new List<EpubTocEntry>();
        void Walk(IEnumerable<EpubNavigationItem> items, int depth)
        {
            foreach (var item in items)
            {
                var file = item.HtmlContentFile?.FilePath;
                var line = file is not null ? Line(file, item.Link?.Anchor) : item.NestedItems.Count > 0 ? -1 : 1;
                var index = toc.Count;
                toc.Add(new(XhtmlMarkdown.Collapse(item.Title ?? ""), depth, line));
                Walk(item.NestedItems, depth + 1);
                if (line < 0) toc[index] = toc[index] with { Line = toc.Count > index + 1 ? toc[index + 1].Line : 1 };
            }
        }
        Walk(book.Navigation ?? [], 0);
        if (toc.Count == 0)
            toc.AddRange(chapters.Select((chapter, index) => new EpubTocEntry(Path.GetFileNameWithoutExtension(chapter.Href), 0, chapter.Line)));
        var authors = string.Join(", ", book.AuthorList ?? []);
        return new(new(book.Title ?? "", authors, markdown, chapters, toc), resources);
    }

    private static string Placeholder(int index) => EpubContract.LinePrefix + "@" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@";

    /// <summary>Resolves a reference made in <paramref name="document"/> to a package path and fragment.</summary>
    internal static (string File, string? Anchor) Resolve(string document, string href)
    {
        var parts = href.Split('#', 2);
        var target = Uri.UnescapeDataString(parts[0]);
        var segments = new List<string>();
        if (!target.StartsWith('/')) segments.AddRange(document.Split('/')[..^1]);
        foreach (var part in target.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(part);
        }
        var file = target.Length == 0 ? document : string.Join('/', segments);
        return (file, parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null);
    }

    internal static string EscapePath(string path)
    {
        var escaped = new StringBuilder();
        foreach (var segment in path.Split('/'))
        {
            if (escaped.Length > 0) escaped.Append('/');
            escaped.Append(Uri.EscapeDataString(segment));
        }
        return escaped.ToString();
    }

    [GeneratedRegex(@"line/@(\d+)@")]
    private static partial Regex Placeholders();
}