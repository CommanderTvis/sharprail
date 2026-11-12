using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>
/// Checks and themes hand-written SVG before it is drawn. Nothing in an accepted document can run or reach outside
/// it: scripts, event handlers, foreign objects, DTDs and every reference that is not a <c>#fragment</c> or an inline
/// raster image are refused, so loading it never touches the network or the filesystem. Theme placeholders
/// (<c>currentColor</c> and <c>var(--fg|--bg|--accent|--muted|--border)</c>) become the pane's colours.
/// </summary>
internal static partial class SvgSanitizer
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxElements = 10_000;

    /// <summary>The pane colours a document can name, as hex: foreground, background, accent, muted text and border.</summary>
    public sealed record Palette(string Foreground, string Background, string Accent, string Muted, string Border);

    public sealed record Result(string? Svg, string? Error);

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> RefusedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "iframe", "object", "embed", "audio", "video", "handler", "listener", "set",
        "animate", "animateMotion", "animateTransform", "animateColor", "discard"
    };

    private static readonly HashSet<string> RasterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp"
    };

    /// <summary>The pane's current colours.</summary>
    public static Palette Current() => new(Hex(Ui.TextBrush), Hex(Ui.Surface), Hex(Ui.Accent), Hex(Ui.Muted), Hex(Ui.BorderBrush));

    /// <summary>Validates and themes a document; pure, so it may run off the UI thread.</summary>
    public static Result Sanitize(string svg, Palette palette)
    {
        if (Encoding.UTF8.GetByteCount(svg) > MaxBytes) return Refuse($"the SVG is larger than {MaxBytes / (1024 * 1024)} MB.");
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
            using var reader = XmlReader.Create(new StringReader(svg), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException e) { return Refuse("the SVG is not well-formed XML (DTDs and entities are not allowed): " + e.Message); }
        if (document.Root is not { } root || root.Name.LocalName != "svg" || (root.Name.Namespace != SvgNs && root.Name.Namespace != XNamespace.None))
            return Refuse("the root element must be <svg>.");
        if (document.Nodes().OfType<XProcessingInstruction>().Any() || root.DescendantNodes().OfType<XProcessingInstruction>().Any())
            return Refuse("processing instructions such as xml-stylesheet are not allowed.");
        var elements = root.DescendantsAndSelf().ToList();
        if (elements.Count > MaxElements) return Refuse($"the SVG has more than {MaxElements} elements.");
        foreach (var element in elements)
        {
            var name = element.Name.LocalName;
            if (RefusedElements.Contains(name)) return Refuse($"<{name}> is not allowed.");
            if (element.Name.Namespace != SvgNs && element.Name.Namespace != XNamespace.None) return Refuse($"<{name}> is outside the SVG namespace.");
            foreach (var attribute in element.Attributes())
            {
                var local = attribute.Name.LocalName;
                if (attribute.IsNamespaceDeclaration) continue;
                if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return Refuse($"event handler attribute {local} on <{name}> is not allowed.");
                if (local == "href" && !SafeReference(attribute.Value, name)) return Refuse($"<{name}> {attribute.Name.LocalName}=\"{Clip(attribute.Value)}\" is not allowed: only #fragment references and data: raster images are.");
                if (UnsafeCss(attribute.Value) is { } css) return Refuse($"<{name}> {local}: {css}");
            }
            if (name == "style" && UnsafeCss(element.Value) is { } sheet) return Refuse("<style>: " + sheet);
        }
        // Agents often omit xmlns; the renderer needs it.
        foreach (var element in elements.Where(element => element.Name.Namespace == XNamespace.None))
            element.Name = SvgNs + element.Name.LocalName;
        if (root.Attribute("color") is null) root.SetAttributeValue("color", palette.Foreground);
        foreach (var element in elements)
        {
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)) attribute.Value = Theme(attribute.Value, palette);
            if (element.Name.LocalName == "style") element.Value = Theme(element.Value, palette);
        }
        return new Result(document.ToString(SaveOptions.DisableFormatting), null);
    }

    private static bool SafeReference(string value, string element)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('#')) return true;
        if (element != "image" || !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        var comma = trimmed.IndexOf(',');
        if (comma < 0) return false;
        var type = trimmed[5..comma].Split(';')[0].Trim();
        return RasterTypes.Contains(type);
    }

    /// <summary>Why CSS text could reach outside the document, or null when it cannot.</summary>
    private static string? UnsafeCss(string text)
    {
        if (text.Contains("@import", StringComparison.OrdinalIgnoreCase)) return "@import is not allowed.";
        if (text.Contains("expression(", StringComparison.OrdinalIgnoreCase)) return "expression() is not allowed.";
        if (text.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) return "javascript: URLs are not allowed.";
        foreach (Match match in UrlPattern().Matches(text))
        {
            var target = match.Groups[1].Value.Trim().Trim('"', '\'').Trim();
            if (!target.StartsWith('#')) return $"url({Clip(target)}) is not allowed: only url(#id) references are.";
        }
        return null;
    }

    private static string Theme(string text, Palette palette)
    {
        if (!text.Contains("currentColor", StringComparison.OrdinalIgnoreCase) && !text.Contains("var(", StringComparison.Ordinal)) return text;
        var themed = VariablePattern().Replace(text, match => match.Groups[1].Value switch
        {
            "fg" => palette.Foreground,
            "bg" => palette.Background,
            "accent" => palette.Accent,
            "muted" => palette.Muted,
            "border" => palette.Border,
            _ => match.Groups[2].Success ? match.Groups[2].Value.Trim() : match.Value
        });
        return CurrentColorPattern().Replace(themed, palette.Foreground);
    }

    private static Result Refuse(string error) => new(null, error);

    private static string Clip(string value) => value.Length > 60 ? value[..60] + "…" : value;

    private static string Hex(Avalonia.Media.ISolidColorBrush brush) => $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";

    [GeneratedRegex(@"url\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"var\(\s*--([A-Za-z0-9_-]+)\s*(?:,([^)]*))?\)")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"\bcurrentColor\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentColorPattern();
}