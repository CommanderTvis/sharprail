using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>Binds the kit's Markdown views to a workspace's host and the window's preferences.</summary>
public static class MarkdownContexts
{
    /// <param name="resolveSpecLink">Maps a <c>[[id]]</c> to its path; without one every spec link renders disabled.</param>
    public static MarkdownContext For(IProjectServices host, Preferences preferences, Action<string, string?> navigate, Func<string, string?>? resolveSpecLink = null) => new(
        async (path, token) =>
        {
            var document = await host.ReadFileAsync(path, token);
            return document.ImageData ?? await Task.Run(() => ImageBytes(document), token);
        },
        id => resolveSpecLink?.Invoke(id),
        navigate, preferences.FontSize, preferences.MarkdownLineWidth, preferences.MarkdownLineWidthBounded)
    {
        SourceWrapWidth = LineWidths.File(preferences.FileLineWidth, preferences.FileLineWidthBounded)
    };

    /// <summary>
    /// What a Markdown image decodes from: a raster's bytes, or an SVG drawn inert to PNG at its own size (the host answers
    /// SVG as text, and the picture decoder reads no vector). Null for a file that is no image; an SVG that cannot be drawn
    /// throws so the preview can say why.
    /// </summary>
    internal static byte[]? ImageBytes(FileDocument document)
    {
        if (document.ImageData is { } raster) return raster;
        if (document.Info?.MediaType != "image/svg+xml") return null;
        return Resources.VectorPictures.RasterBytes(document.Text, 1) ?? throw new IOException("The SVG is malformed or has no size.");
    }
}