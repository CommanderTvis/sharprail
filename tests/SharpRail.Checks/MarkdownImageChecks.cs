using System.Text;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Plugins.UI.Kit.Markdown;
using SharpRail.UI.Rendering;

using SkiaSharp;

namespace SharpRail.Checks;

/// <summary>Markdown images in SVG: the figure draws, and a picture that cannot is replaced by a visible reason.</summary>
internal static class MarkdownImageChecks
{
    // Sized by viewBox alone.
    private const string ViewBoxOnly = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 120 60\"><rect width=\"120\" height=\"60\" fill=\"#ff0000\"/></svg>";

    // Class rules, custom properties, a dark media rule and text, as a generated chart carries them.
    private const string Chart = """
        <svg xmlns="http://www.w3.org/2000/svg" class="bench" viewBox="0 0 300 120" width="300" height="120" role="img" aria-label="Chart"><style>
        .bench{--bg:#ffffff;--host:#4f7cff;--fg:#1c1c1e;font-family:-apple-system,"Segoe UI",Arial,sans-serif}
        @media(prefers-color-scheme:dark){.bench{--bg:#161618;--host:#5f8cff;--fg:#ececee}}
        .bench .bg{fill:var(--bg)}.bench text{fill:var(--fg);font-size:12px}.bench .host{fill:var(--host)}
        </style><rect class="bg" width="300" height="120"/><text x="20" y="24" class="title">Startup</text><rect class="host" x="20" y="40" width="200" height="30"/></svg>
        """;

    internal static void Run()
    {
        Case("viewBox-only SVG", ViewBoxOnly, new SKColor(255, 0, 0));
        Case("styled chart SVG", Chart, new SKColor(0x4f, 0x7c, 0xff));
        Failure("malformed SVG", "<svg xmlns=\"http://www.w3.org/2000/svg\"><unclosed></svg>");
        Failure("missing image", null);
        Console.WriteLine("PASS Markdown images: SVG figures draw from viewBox-only and styled documents; unreadable images show a visible reason");
    }

    private static PreviewResult Render(string? svg)
    {
        var bytes = svg is null ? null : Encoding.UTF8.GetBytes(svg);
        var context = new MarkdownContext((_, _) => ValueTask.FromResult(bytes is null ? null : MarkdownContexts.ImageBytes(new FileDocument("img/figure.svg", svg!) { Info = ContentClassifier.Classify(bytes, "img/figure.svg") })), _ => null, (_, _) => { }, 14, 100, true);
        using var preview = new MarkdownPreview(MarkdownPreview.Parse("![Figure caption](img/figure.svg)\n"), "doc/README.md", context);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Control? found = null;
        while (found is null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            found = preview.GetLogicalDescendants().OfType<Control>().FirstOrDefault(control => control.Name is "MarkdownImage" or "MarkdownImageError");
            if (found is null) Thread.Sleep(10);
        }
        SKBitmap? bitmap = null;
        if (found is Image { Source: Bitmap source })
        {
            using var stream = new MemoryStream();
            source.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Position = 0;
            bitmap = SKBitmap.Decode(stream);
        }
        return new(found?.Name, (found as TextBlock)?.Text, bitmap);
    }

    private sealed record PreviewResult(string? Kind, string? Message, SKBitmap? Bitmap);

    private static void Case(string name, string svg, SKColor expected)
    {
        var result = Render(svg);
        Require(result.Kind == "MarkdownImage" && result.Bitmap is { Width: > 0, Height: > 0 }, name + " must draw an image, got " + (result.Kind ?? "nothing") + " " + result.Message);
        var bitmap = result.Bitmap!;
        var drawn = false;
        for (var y = 0; y < bitmap.Height && !drawn; y++)
            for (var x = 0; x < bitmap.Width && !drawn; x++)
                drawn = Near(bitmap.GetPixel(x, y), expected);
        Require(drawn, name + " must paint its content colour " + expected);
    }


    private static bool Near(SKColor a, SKColor b) => a.Alpha > 200 && Math.Abs(a.Red - b.Red) < 8 && Math.Abs(a.Green - b.Green) < 8 && Math.Abs(a.Blue - b.Blue) < 8;

    private static void Failure(string name, string? svg)
    {
        var result = Render(svg);
        Require(result.Kind == "MarkdownImageError" && result.Message?.Contains("img/figure.svg", StringComparison.Ordinal) == true, name + " must show a visible error naming the path, got " + (result.Kind ?? "nothing") + " " + result.Message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}