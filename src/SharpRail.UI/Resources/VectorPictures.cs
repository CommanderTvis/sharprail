using System.Xml;
using System.Xml.Linq;

using Avalonia.Media.Imaging;

using SkiaSharp;

using Svg.Skia;

namespace SharpRail.UI.Resources;

/// <summary>
/// SVG drawn inert: the document is reduced to shapes that reference nothing outside itself, then rasterised. Script,
/// event handlers, foreign content and every external reference are removed before anything parses it as a picture.
/// </summary>
internal static class VectorPictures
{
    private const int MaxTextLength = 4 * 1024 * 1024;
    private const float MaxSide = 4096;
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase) { "script", "foreignObject", "iframe", "object", "embed", "audio", "video", "animate", "animateTransform", "animateMotion", "set", "handler", "listener" };

    /// <summary>The inert document, or null when the text is not well-formed SVG.</summary>
    internal static string? Sanitise(string svg)
    {
        if (svg.Length > MaxTextLength) return null;
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { return null; }
        if (document.Root is not { Name.LocalName: "svg" } root) return null;
        document.DocumentType?.Remove();
        foreach (var node in document.DescendantNodes().Where(node => node is XProcessingInstruction).ToArray()) node.Remove();
        foreach (var element in root.DescendantsAndSelf().ToArray())
        {
            if (element != root && (Dropped.Contains(element.Name.LocalName) || element.Name.LocalName == "style" && External(element.Value)))
            { element.Remove(); continue; }
            foreach (var attribute in element.Attributes().ToArray())
            {
                var name = attribute.Name.LocalName;
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name == "base" ||
                    name == "href" && !Local(attribute.Value) || name != "href" && External(attribute.Value))
                    attribute.Remove();
            }
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    // A fragment of this document, or a raster picture carried inline.
    private static bool Local(string href)
    {
        var value = href.Trim();
        return value.StartsWith('#') || new[] { "data:image/png", "data:image/jpeg", "data:image/gif", "data:image/webp" }
            .Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // Any url() that is not a fragment reference, an @import, or a scripting scheme.
    private static bool External(string value)
    {
        if (value.Contains("@import", StringComparison.OrdinalIgnoreCase) || value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) return true;
        for (var index = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase); index >= 0; index = value.IndexOf("url(", index + 4, StringComparison.OrdinalIgnoreCase))
            if (!value[(index + 4)..].TrimStart(' ', '\'', '"').StartsWith('#')) return true;
        return false;
    }

    internal static Task<Bitmap?> RasterAsync(ResourceContent content, CancellationToken token) =>
        content is ResourceContent.Text text ? Task.Run(() => Raster(text.Value), token) : Task.FromResult<Bitmap?>(null);

    internal static Bitmap? Raster(string svg)
    {
        if (Sanitise(svg) is not { } inert) return null;
        try
        {
            using var picture = new SKSvg();
            if (picture.FromSvg(inert) is not { CullRect: { Width: > 0, Height: > 0 } bounds }) return null;
            var scale = Math.Min(2, MaxSide / Math.Max(bounds.Width, bounds.Height));
            using var stream = new MemoryStream();
            if (!picture.Save(stream, SKColors.Transparent, SKEncodedImageFormat.Png, 100, scale, scale)) return null;
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
}