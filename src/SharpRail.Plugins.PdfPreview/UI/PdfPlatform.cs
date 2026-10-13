namespace SharpRail.Plugins.PdfPreview.UI;

/// <summary>One page read by a platform's PDFium runtime, with boxes as x, y, width, height.</summary>
public sealed record PdfPlatformPage(double Width, double Height, string Text, double[][] Boxes);

/// <summary>A platform's PDFium bitmap in premultiplied BGRA format.</summary>
public sealed record PdfPlatformRaster(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>Supplies PDFium from a separate WASM instance when it cannot share the managed runtime's native heap.</summary>
public static class PdfPlatform
{
    public static Func<byte[], Task<IReadOnlyList<PdfPlatformPage>>>? Read { internal get; set; }
    public static Func<byte[], double, Task<IReadOnlyList<PdfPlatformRaster>>>? Render { internal get; set; }
}