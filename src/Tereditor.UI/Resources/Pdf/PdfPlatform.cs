namespace SharpRail.UI.Resources.Pdf;

/// <summary>One page read by a platform's PDFium runtime, with boxes as x, y, width, height.</summary>
internal sealed record PdfPlatformPage(double Width, double Height, string Text, double[][] Boxes);

/// <summary>A platform's PDFium bitmap in premultiplied BGRA format.</summary>
internal sealed record PdfPlatformRaster(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>Supplies PDFium from a separate WASM instance when it cannot share the managed runtime's native heap.</summary>
internal static class PdfPlatform
{
    public static Func<byte[], Task<IReadOnlyList<PdfPlatformPage>>>? Read { internal get; set; }
    public static Func<byte[], double, Task<IReadOnlyList<PdfPlatformRaster>>>? Render { internal get; set; }
}