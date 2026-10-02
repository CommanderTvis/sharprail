using System.Net;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Remote;
using SharpRail.Plugins.PdfPreview.UI;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

// The PDF preview builtin plugin: PDFium reading and rasterizing the fork's minimal PDF, and the fork's
// pdf-preview e2e (render, zoom re-rasterizing larger, selectable text, no Markdown chrome, following the file's
// bytes through a rewrite and a rename, the reload button) against the app with its in-process host.
internal static class PdfPreviewChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The fork's e2e fixture: one 200x120pt page with one line of Helvetica, offsets computed for the text given.
    internal static byte[] MinimalPdf(string text = "ThinkRail PDF") => PagesPdf([text]);

    private static byte[] PagesPdf(string[] texts, int rotation = 0, string? cropBox = null, bool unicode = false)
    {
        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, texts.Length).Select(index => $"{3 + index * 3} 0 R"))}] /Count {texts.Length} >>"
        ];
        foreach (var text in texts)
        {
            var content = $"BT /F1 18 Tf 20 60 Td ({text}) Tj ET";
            var page = objects.Count + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 120] /Rotate {rotation} {(cropBox is null ? "" : "/CropBox [" + cropBox + "]")} /Contents {page + 1} 0 R /Resources << /Font << /F1 {page + 2} 0 R >> >> >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            objects.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica {(unicode ? $"/ToUnicode {3 + texts.Length * 3} 0 R" : "")} >>");
        }
        if (unicode)
        {
            const string map = "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Fixture def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n2 beginbfchar\n<41> <D83DDE80>\n<42> <00E9>\nendbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";
            objects.Add($"<< /Length {map.Length} >>\nstream\n{map}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    internal static void Run(string root)
    {
        Engine();
        UnicodeSelection();
        DragScrolling();
        EndToEnd(Path.Combine(root, "pdf-preview-local"), remote: false);
        EndToEnd(Path.Combine(root, "pdf-preview-remote"), remote: true);
        DelayedWatch(root);
    }

    private static void DelayedWatch(string root)
    {
        var workspace = Path.Combine(root, "pdf-delayed-watch");
        Directory.CreateDirectory(workspace);
        var target = Path.Combine(workspace, "built.pdf");
        File.WriteAllBytes(target, MinimalPdf("Before watch"));
        TaskCompletionSource ready = null!;
        using var app = new E2eWorkspace(workspace, openFiles: false, prepare: host => ready = host.HoldWatch());
        var opening = app.Window.OpenDocumentAsync("built.pdf", true);
        E2eWorkspace.Until(() => opening.IsCompleted && FirstPageText(app.Window).Contains("Before watch", StringComparison.Ordinal));
        opening.GetAwaiter().GetResult();
        File.WriteAllBytes(target, MinimalPdf("Built before watch"));
        ready.SetResult();
        E2eWorkspace.Until(() => FirstPageText(app.Window).Contains("Built before watch", StringComparison.Ordinal));
        Console.WriteLine("PASS PDF live preview catches a rewrite between initial read and file-watch readiness");
    }

    private static void UnicodeSelection()
    {
        var selection = new PdfSelection();
        var page = new PdfPage(100, 100, "A🚀B", Enumerable.Repeat(new Rect(0, 0, 10, 10), 4).ToArray());
        var layer = new PdfTextLayer(page, selection);
        selection.Start(layer, 1);
        Require(layer.SelectedText == "🚀", "Selecting a surrogate's first unit copies the full Unicode character.");
        selection.Start(layer, 2);
        Require(layer.SelectedText == "🚀", "Selecting a surrogate's second unit cannot copy a broken character.");
    }

    private static void Engine()
    {
        var page = PdfEngine.Read(MinimalPdf()).Single();
        Require(page is { Width: 200, Height: 120 } && page.Text.Contains("ThinkRail PDF", StringComparison.Ordinal), "PDFium reads the page size and its text.");
        var first = page.Text.IndexOf('T');
        Require(page.Boxes[first] is { Width: > 0, Height: > 0 } box && box.Left is > 15 and < 30 && box.Bottom is > 50 and < 65,
            "A character's box is in points from the top-left corner: " + page.Boxes[first]);
        Require(PdfEngine.Render(MinimalPdf(), 2).Single() is { Width: 400, Height: 240 }, "A page rasterizes at the requested scale.");
        var rotated = PdfEngine.Read(PagesPdf(["Turned"], rotation: 90)).Single();
        Require(rotated is { Width: 120, Height: 200 } && rotated.Boxes[0] is { Left: > 50 and < 65, Top: > 15 and < 30, Height: > 5, Width: > 5 },
            "Rotated text boxes follow the rendered page: " + rotated.Boxes[0]);
        var cropped = PdfEngine.Read(PagesPdf(["Cropped"], cropBox: "10 20 180 110")).Single();
        Require(cropped is { Width: 170, Height: 90 } && cropped.Boxes[0] is { Left: > 5 and < 20, Bottom: > 40 and < 55 },
            "Cropped text boxes follow the visible page origin: " + cropped.Boxes[0]);
        var unicode = PdfEngine.Read(PagesPdf(["AB"], unicode: true)).Single();
        Require(unicode.Text == "🚀é" && unicode.Boxes.Count == unicode.Text.Length && unicode.Boxes[0] == unicode.Boxes[1] && unicode.Boxes[2].Left > unicode.Boxes[0].Left,
            "Native Unicode extraction preserves astral characters, accents and one box per UTF16 unit: " + unicode.Text);
        var refused = false;
        try { PdfEngine.Read("not a pdf"u8.ToArray()); }
        catch (PdfException error) { refused = error.Message.Length > 0; }
        Require(refused, "Bytes that are not a PDF are refused with a reason.");
        Console.WriteLine("PASS PDF preview engine: page size, text and character boxes, rasterizing at a scale, refusing a non-PDF");
    }

    private static void DragScrolling()
    {
        var selection = new PdfSelection();
        var pages = new StackPanel { Spacing = 12 };
        for (var index = 0; index < 8; index++)
        {
            var text = $"Page {index + 1}";
            pages.Children.Add(new PdfTextLayer(new PdfPage(200, 120, text,
                Enumerable.Range(0, text.Length).Select(character => new Rect(20 + character * 12, 50, 12, 18)).ToArray()), selection)
            { Width = 200, Height = 120 });
        }
        var scroll = new ScrollViewer { Content = pages };
        var window = new Window { Width = 300, Height = 220, Content = scroll };
        try
        {
            window.Show();
            E2eWorkspace.Settle();
            var first = (PdfTextLayer)pages.Children[0];
            var start = first.TranslatePoint(new Point(20, 60), window)!.Value;
            var below = scroll.TranslatePoint(new Point(100, scroll.Bounds.Height + 30), window)!.Value;
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left); window.MouseMove(below);
            E2eWorkspace.Until(() => scroll.Offset.Y > 500 && selection.SelectedText.Contains("Page 5", StringComparison.Ordinal));
            window.MouseUp(below, MouseButton.Left);
            var stopped = scroll.Offset;
            E2eWorkspace.Settle(100);
            Require(scroll.Offset == stopped, "Releasing a drag stops scrolling.");
            var last = (PdfTextLayer)pages.Children[7];
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            E2eWorkspace.Settle();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var end = last.TranslatePoint(new Point(90, 60), window)!.Value;
            Require(end.Y > 0 && end.Y < window.Bounds.Height, "The backward drag starts on the visible last page.");
            var above = scroll.TranslatePoint(new Point(0, -30), window)!.Value;
            window.MouseMove(end); window.MouseDown(end, MouseButton.Left); window.MouseMove(above);
            E2eWorkspace.Until(() => scroll.Offset.Y < 50 && selection.SelectedText.Contains("Page 1", StringComparison.Ordinal));
            scroll.Content = null;
            E2eWorkspace.Settle();
            var detached = scroll.Offset;
            E2eWorkspace.Settle(100);
            Require(scroll.Offset == detached, "Detaching the selection layer stops its scroll timer.");
            window.MouseUp(above, MouseButton.Left);
            Console.WriteLine("PASS PDF selection scrolling: stationary captured pointer crosses hidden pages in both directions, release and detach stop scrolling");
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return scope.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
    }

    private static bool Has(Control scope, string name)
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return scope.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == name && item.IsEffectivelyVisible);
    }

    private static int RasterWidth(Control window) => Find<Image>(window, "PdfPage").Source is Bitmap { PixelSize.Width: var width } ? width : 0;

    private static string FirstPageText(Control window) => window.GetLogicalDescendants().OfType<PdfTextLayer>().FirstOrDefault()?.Text ?? "";

    private static void EndToEnd(string root, bool remote)
    {
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var target = Path.Combine(workspace, "sample.pdf");
        File.WriteAllBytes(target, MinimalPdf());
        File.WriteAllBytes(Path.Combine(workspace, "other.pdf"), MinimalPdf("Other PDF"));
        File.WriteAllText(Path.Combine(workspace, "notes.txt"), "PDF navigation fixture.");
        var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "pdf-ui", Path.Combine(root, "state")) : null;
        try
        {
            if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            using var app = server is null ? new E2eWorkspace(workspace, profileRoot: Path.Combine(root, "profile"))
                : new E2eWorkspace(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                    "pdf-ui", workspace, Path.Combine(root, "profile"), workspace);
            E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
            if (remote) app.Click(app.Find<Button>("Tab_files"));
            EndToEnd(app, target);
            Console.WriteLine($"PASS PDF preview {(remote ? "remote" : "local")} host lifecycle and live file revisions");
        }
        finally
        {
            if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }

    private static void EndToEnd(E2eWorkspace app, string target)
    {
        var window = app.Window;
        var loader = app.Workbench.PluginLoader;
        E2eWorkspace.Until(() => loader.Registry.Active.Contains("pdf-preview"));

        // A PDF renders through PDFium, and zooming rasterizes it again, larger.
        app.Open("sample.pdf", keep: true);
        E2eWorkspace.Until(() => app.Tabs.Any(tab => tab.Kind == "viewer" && tab.Path == "sample.pdf"));
        E2eWorkspace.Until(() => Has(window, "PdfPage") && RasterWidth(window) > 0);
        var before = RasterWidth(window);
        Require(Find<TextBlock>(window, "PdfZoomLevel").Text == "100%" && Find<TextBlock>(window, "PdfPageCount").Text == "1 page", "The toolbar starts at 100% and counts the page.");
        app.Click(Find<Button>(window, "PdfZoomIn"));
        Require(Find<TextBlock>(window, "PdfZoomLevel").Text != "100%", "Zooming in changes the level.");
        E2eWorkspace.Until(() => RasterWidth(window) > before);
        app.Click(Find<Button>(window, "PdfZoomReset"));
        Require(Find<TextBlock>(window, "PdfZoomLevel").Text == "100%", "Reset returns to 100%.");
        E2eWorkspace.Until(() => RasterWidth(window) == before);

        var preview = window.GetLogicalDescendants().OfType<PdfPreview>().Single();
        var viewport = Find<ScrollViewer>(window, "PdfPreviewScroll");
        var wheelPoint = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, 80), window)!.Value;
        window.MouseWheel(wheelPoint, new Vector(0, -1));
        Require(preview.Scale == 1, "An unmodified wheel scrolls without zooming the PDF.");
        var scrollBefore = viewport.Offset;
        window.MouseWheel(wheelPoint, new Vector(0, 1), RawInputModifiers.Control);
        Require(preview.Scale > 1 && preview.Scale < 1.2 && viewport.Offset == scrollBefore, "Control-wheel zooms by a bounded step and consumes scrolling.");
        window.MouseWheel(wheelPoint, new Vector(0, -1), RawInputModifiers.Meta);
        Require(Math.Abs(preview.Scale - 1) < 0.001, "Command-wheel reverses the same zoom step.");
        for (var index = 0; index < 20; index++) app.Click(Find<Button>(window, "PdfZoomIn"), freshGesture: false);
        Require(preview.Scale == 6, "Toolbar zoom is capped at 600%.");
        for (var index = 0; index < 45; index++) app.Click(Find<Button>(window, "PdfZoomOut"), freshGesture: false);
        Require(preview.Scale == 0.25, "Toolbar zoom is bounded at 25%.");
        app.Click(Find<Button>(window, "PdfZoomReset"));
        E2eWorkspace.Until(() => RasterWidth(window) == before && preview.Scale == 1);

        // The image is a picture of the page; the text a reader copies is the layer over it, selected by dragging.
        var layer = window.GetLogicalDescendants().OfType<PdfTextLayer>().Single();
        Require(layer.Text.Contains("ThinkRail PDF", StringComparison.Ordinal), "The text layer carries the page's words.");
        var input = TopLevel.GetTopLevel(layer)!;
        layer.BringIntoView();
        Dispatcher.UIThread.RunJobs(); input.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var from = layer.TranslatePoint(new Point(2, layer.Bounds.Height * 0.45), input)!.Value;
        var to = layer.TranslatePoint(new Point(layer.Bounds.Width - 2, layer.Bounds.Height * 0.45), input)!.Value;
        input.MouseMove(from); input.MouseDown(from, MouseButton.Left);
        input.MouseMove(to); input.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Require(layer.SelectedText.Contains("ThinkRail PDF", StringComparison.Ordinal), "Dragging across the line selects its text: " + layer.SelectedText);

        // A PDF is not text: the Markdown chrome does not belong to it, and it owns its own toolbar instead.
        Require(!Has(window, "MarkdownPreviewMode") && !Has(window, "MarkdownPreview") && Has(window, "PdfToolbar"), "A PDF shows its toolbar and no Markdown chrome.");

        // A rewritten PDF shows its new bytes without reopening the tab.
        File.WriteAllBytes(target, MinimalPdf("Recompiled now"));
        E2eWorkspace.Until(() => FirstPageText(window).Contains("Recompiled now", StringComparison.Ordinal));
        // The way a compiler does it: the file goes away and comes back under a rename.
        File.Delete(target);
        File.WriteAllBytes(target + ".tmp", MinimalPdf("Second pass"));
        File.Move(target + ".tmp", target);
        E2eWorkspace.Until(() => FirstPageText(window).Contains("Second pass", StringComparison.Ordinal));
        // And the toolbar can ask again, for the bytes a watch never told it about.
        File.WriteAllBytes(target, MinimalPdf("By hand"));
        app.Click(Find<Button>(window, "PdfReload"));
        E2eWorkspace.Until(() => FirstPageText(window).Contains("By hand", StringComparison.Ordinal));

        var previousImage = Find<Image>(window, "PdfPage");
        app.Open("notes.txt", keep: true);
        E2eWorkspace.Until(() => previousImage.Source is null);
        app.Open("sample.pdf", keep: true);
        E2eWorkspace.Until(() => FirstPageText(window).Contains("By hand", StringComparison.Ordinal) && RasterWidth(window) > 0);
        Require(Find<TextBlock>(window, "PdfZoomLevel").Text == "100%", "A remounted PDF reloads after releasing its raster and retains zoom.");
        File.WriteAllBytes(target, "not a PDF"u8.ToArray());
        E2eWorkspace.Until(() => Has(window, "PdfPreviewError"));
        Require(!Has(window, "PdfToolbar"), "An unreadable PDF replaces its toolbar with an error and retry.");
        app.Click(Find<Button>(window, "PdfRetry"));
        E2eWorkspace.Until(() => Has(window, "PdfPreviewError"));
        File.WriteAllBytes(target, MinimalPdf("Recovered PDF"));
        E2eWorkspace.Until(() => FirstPageText(window).Contains("Recovered PDF", StringComparison.Ordinal) && RasterWidth(window) > 0);

        File.WriteAllBytes(target, PagesPdf(["First page", "Second page"]));
        E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<PdfTextLayer>().Count() == 2);
        var layers = window.GetLogicalDescendants().OfType<PdfTextLayer>().ToArray();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var firstPoint = layers[0].TranslatePoint(new Point(2, layers[0].Bounds.Height * 0.45), window)!.Value;
        var lastPoint = layers[1].TranslatePoint(new Point(layers[1].Bounds.Width - 2, layers[1].Bounds.Height * 0.45), window)!.Value;
        window.MouseMove(firstPoint); window.MouseDown(firstPoint, MouseButton.Left);
        window.MouseMove(lastPoint); window.MouseUp(lastPoint, MouseButton.Left);
        Require(layers[0].SelectedText.Contains("First page", StringComparison.Ordinal) && layers[0].SelectedText.Contains("Second page", StringComparison.Ordinal),
            "A captured drag crosses page boundaries and selects both pages: " + layers[0].SelectedText);
        window.MouseMove(lastPoint); window.MouseDown(lastPoint, MouseButton.Left);
        window.MouseMove(firstPoint); window.MouseUp(firstPoint, MouseButton.Left);
        Require(layers[1].SelectedText.Contains("First page", StringComparison.Ordinal) && layers[1].SelectedText.Contains("Second page", StringComparison.Ordinal),
            "Backward selection crosses page boundaries too.");
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        var copied = window.Clipboard!.TryGetTextAsync();
        E2eWorkspace.Until(() => copied.IsCompleted);
        Require(copied.GetAwaiter().GetResult() == layers[1].SelectedText, "Copy carries the selection across both pages.");
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
        window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
        Require(layers[1].SelectedText.Contains("First page", StringComparison.Ordinal) && layers[1].SelectedText.Contains("Second page", StringComparison.Ordinal),
            "Select All includes the entire PDF, not just the focused page.");

        File.WriteAllBytes(target, PagesPdf(["AB"], unicode: true));
        E2eWorkspace.Until(() => FirstPageText(window) == "🚀é");
        var unicodeLayer = window.GetLogicalDescendants().OfType<PdfTextLayer>().Single();
        unicodeLayer.Focus();
        window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
        window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        window.KeyRelease(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
        var unicodeCopy = window.Clipboard!.TryGetTextAsync();
        E2eWorkspace.Until(() => unicodeCopy.IsCompleted);
        Require(unicodeCopy.GetAwaiter().GetResult() == "🚀é", "The native PDF's astral character and accent survive selection and clipboard copying.");

        // With the plugin off, its viewer tab offers the file as text, and a PDF opens as text.
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
        Find<ToggleSwitch>(Find<Border>(settings, "PluginRow_pdf-preview"), "PluginToggle").IsChecked = false;
        E2eWorkspace.Until(() => !loader.Registry.Active.Contains("pdf-preview"));
        settings.Close();
        E2eWorkspace.Until(() => !window.OwnedWindows.Any());
        E2eWorkspace.Until(() => Has(window, "FileViewerGone"));
        app.Open("other.pdf", keep: true);
        E2eWorkspace.Until(() => app.Tabs.Any(tab => tab.Kind == "file" && tab.Path == "other.pdf"));
        Console.WriteLine("PASS PDF preview plugin end to end: render, zoom re-rasterizing, selectable text, live rewrite and rename, reload, off opens as text");
    }
}