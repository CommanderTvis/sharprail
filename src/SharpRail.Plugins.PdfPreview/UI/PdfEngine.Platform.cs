using Avalonia;

namespace SharpRail.Plugins.PdfPreview.UI;

internal static partial class PdfEngine
{
    public static async Task<IReadOnlyList<PdfPage>> ReadAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (PdfPlatform.Read is not { } platform) return await Task.Run(() => Read(bytes), cancellationToken);
        var pages = await platform(bytes);
        return pages.Select(page => new PdfPage(page.Width, page.Height, page.Text, page.Boxes.Select(box => new Rect(box[0], box[1], box[2], box[3])).ToArray())).ToArray();
    }

    public static async Task<IReadOnlyList<PdfRaster>> RenderAsync(byte[] bytes, double scale)
    {
        if (PdfPlatform.Render is not { } platform) return await Task.Run(() => Render(bytes, scale));
        return (await platform(bytes, scale)).Select(raster => new PdfRaster(raster.Pixels, raster.Width, raster.Height, raster.Stride)).ToArray();
    }

}