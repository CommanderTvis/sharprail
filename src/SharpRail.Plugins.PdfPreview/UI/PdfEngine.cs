using System.Runtime.InteropServices;
using System.Text;

using Avalonia;

namespace SharpRail.Plugins.PdfPreview.UI;

/// <summary>A page's size in points, its text, and each character's box in points from the top-left corner.</summary>
internal sealed record PdfPage(double Width, double Height, string Text, IReadOnlyList<Rect> Boxes);

/// <summary>One page rasterized to premultiplied BGRA.</summary>
internal sealed record PdfRaster(byte[] Pixels, int Width, int Height, int Stride);

internal sealed class PdfException(string message) : Exception(message);

/// <summary>
/// PDFium through its C API. The library is not thread-safe, so every call runs under one lock, off the UI thread;
/// a document is opened from its bytes for each read and closed again, so nothing native outlives the call.
/// </summary>
internal static unsafe partial class PdfEngine
{
    private const string Library = "pdfium";
    private const int RenderAnnotations = 0x01;
    private static readonly Lock Gate = new();
    private static bool initialized;

    public static IReadOnlyList<PdfPage> Read(byte[] bytes) => WithDocument(bytes, document =>
    {
        var pages = new PdfPage[FPDF_GetPageCount(document)];
        for (var index = 0; index < pages.Length; index++)
        {
            var page = FPDF_LoadPage(document, index);
            if (page == 0) throw new PdfException($"Page {index + 1} could not be read.");
            try
            {
                double width = FPDF_GetPageWidthF(page), height = FPDF_GetPageHeightF(page);
                var textPage = FPDFText_LoadPage(page);
                try
                {
                    var count = textPage == 0 ? 0 : FPDFText_CountChars(textPage);
                    var text = new StringBuilder();
                    var boxes = new List<Rect>();
                    for (var character = 0; character < count; character++)
                    {
                        var unicode = FPDFText_GetUnicode(textPage, character);
                        Rune rune;
                        var consumed = 1;
                        if (unicode is >= 0xD800 and <= 0xDBFF && character + 1 < count &&
                            FPDFText_GetUnicode(textPage, character + 1) is >= 0xDC00 and <= 0xDFFF and var low)
                        {
                            rune = new Rune((char)unicode, (char)low);
                            consumed = 2;
                        }
                        else if (unicode == 0 || !Rune.TryCreate((int)unicode, out rune)) continue;
                        var box = default(Rect);
                        if (FPDFText_GetCharBox(textPage, character, out var left, out var right, out var bottom, out var top) != 0)
                        {
                            var sizeX = (int)Math.Ceiling(width * 100);
                            var sizeY = (int)Math.Ceiling(height * 100);
                            FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, left, bottom, out var x1, out var y1);
                            FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, right, top, out var x2, out var y2);
                            box = new Rect(Math.Min(x1, x2) * width / sizeX, Math.Min(y1, y2) * height / sizeY,
                                Math.Abs(x2 - x1) * width / sizeX, Math.Abs(y2 - y1) * height / sizeY);
                        }
                        text.Append(rune.ToString());
                        for (var unit = 0; unit < rune.Utf16SequenceLength; unit++) boxes.Add(box);
                        character += consumed - 1;
                    }
                    pages[index] = new(width, height, text.ToString(), boxes);
                }
                finally { if (textPage != 0) FPDFText_ClosePage(textPage); }
            }
            finally { FPDF_ClosePage(page); }
        }
        return pages;
    });

    /// <param name="scale">Device pixels per point.</param>
    public static IReadOnlyList<PdfRaster> Render(byte[] bytes, double scale) => WithDocument(bytes, document =>
    {
        var rasters = new PdfRaster[FPDF_GetPageCount(document)];
        for (var index = 0; index < rasters.Length; index++)
        {
            var page = FPDF_LoadPage(document, index);
            if (page == 0) throw new PdfException($"Page {index + 1} could not be read.");
            try
            {
                var width = Math.Max(1, (int)Math.Ceiling(FPDF_GetPageWidthF(page) * scale));
                var height = Math.Max(1, (int)Math.Ceiling(FPDF_GetPageHeightF(page) * scale));
                var bitmap = FPDFBitmap_Create(width, height, 1);
                if (bitmap == 0) throw new PdfException("The page is too large to draw at this zoom.");
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, new CULong(0xFFFFFFFF));
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, RenderAnnotations);
                    var stride = FPDFBitmap_GetStride(bitmap);
                    var pixels = new byte[stride * height];
                    new ReadOnlySpan<byte>((void*)FPDFBitmap_GetBuffer(bitmap), pixels.Length).CopyTo(pixels);
                    rasters[index] = new(pixels, width, height, stride);
                }
                finally { FPDFBitmap_Destroy(bitmap); }
            }
            finally { FPDF_ClosePage(page); }
        }
        return rasters;
    });

    private static T WithDocument<T>(byte[] bytes, Func<nint, T> read)
    {
        lock (Gate)
        {
            if (!initialized) { FPDF_InitLibrary(); initialized = true; }
            fixed (byte* data = bytes)
            {
                var document = FPDF_LoadMemDocument64(data, (nuint)bytes.Length, null);
                if (document == 0) throw new PdfException(Describe(FPDF_GetLastError().Value));
                try { return read(document); }
                finally { FPDF_CloseDocument(document); }
            }
        }
    }

    private static string Describe(nuint error) => error switch
    {
        2 => "the file could not be opened",
        3 => "it is not a PDF, or it is damaged",
        4 => "it is protected by a password",
        5 => "it uses an unsupported security scheme",
        6 => "a page could not be read",
        _ => "PDFium could not load it"
    };

    [LibraryImport(Library)] private static partial void FPDF_InitLibrary();
    [LibraryImport(Library)] private static partial nint FPDF_LoadMemDocument64(byte* data, nuint size, byte* password);
    [LibraryImport(Library)] private static partial CULong FPDF_GetLastError();
    [LibraryImport(Library)] private static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Library)] private static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Library)] private static partial nint FPDF_LoadPage(nint document, int index);
    [LibraryImport(Library)] private static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Library)] private static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Library)] private static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Library)]
    private static partial int FPDF_PageToDevice(nint page, int x, int y, int width, int height, int rotate,
        double pageX, double pageY, out int deviceX, out int deviceY);
    [LibraryImport(Library)] private static partial nint FPDFBitmap_Create(int width, int height, int alpha);
    [LibraryImport(Library)] private static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, CULong color);
    [LibraryImport(Library)] private static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotate, int flags);
    [LibraryImport(Library)] private static partial nint FPDFBitmap_GetBuffer(nint bitmap);
    [LibraryImport(Library)] private static partial int FPDFBitmap_GetStride(nint bitmap);
    [LibraryImport(Library)] private static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Library)] private static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Library)] private static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Library)] private static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Library)] private static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Library)] private static partial int FPDFText_GetCharBox(nint textPage, int index, out double left, out double right, out double bottom, out double top);
}