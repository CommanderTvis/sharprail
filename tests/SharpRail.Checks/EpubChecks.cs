using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Epub;
using SharpRail.Plugins.Epub.Host;
using SharpRail.Plugins.Epub.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Markdown;
using SharpRail.UI.Panels;

using SkiaSharp;

namespace SharpRail.Checks;

// The EPUB Reader plugin: generated EPUB 3 (nav) and EPUB 2 (NCX) fixtures converted on the host, the plugin disabled
// by default, and once enabled the viewer's table of contents, in-book and external links, images and cover, both
// reading modes, Find across chapters, theme contrast, remote-host parity and a large book opening without a stall.
internal static class EpubChecks
{
    private const string Id = EpubContract.Id;
    private static readonly TimeSpan StallBudget = TimeSpan.FromMilliseconds(250);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        Conversion(epub3: true);
        Conversion(epub3: false);
        EndToEnd(Path.Combine(root, "epub-local"), remote: false);
        EndToEnd(Path.Combine(root, "epub-remote"), remote: true);
    }

    private static void Conversion(bool epub3)
    {
        var book = EpubConversion.Convert(Fixture(epub3), default).Book;
        var markdown = book.Markdown.Split('\n');
        Require(book.Title == "Fixture Book" && book.Authors == "Ada Example", $"Title and author come from the package: {book.Title} / {book.Authors}.");
        Require(book.Chapters.Count == 3, $"Every reading-order document is a chapter: {book.Chapters.Count}.");
        Require(book.Toc.Select(entry => entry.Title).SequenceEqual(["Chapter One", "Chapter Two", "Section B", "Chapter Three"]),
            "The " + (epub3 ? "nav document" : "NCX") + " is the table of contents: " + string.Join(", ", book.Toc.Select(entry => entry.Title)));
        int LineOf(string text) => Array.FindIndex(markdown, line => line.Contains(text, StringComparison.Ordinal)) + 1;
        Require(book.Toc.Single(entry => entry.Title == "Section B").Line == LineOf("## Section B"), "A fragment link in the TOC lands on its heading.");
        Require(book.Toc.Single(entry => entry.Title == "Chapter Two").Line == LineOf("# Chapter Two"), "A chapter link in the TOC lands on the chapter.");
        Require(markdown[0].StartsWith("![Cover](res/OEBPS/images/cover.png)", StringComparison.Ordinal), "The cover image opens the book: " + markdown[0]);
        Require(book.Markdown.Contains("](" + EpubContract.LinePrefix + LineOf("## Section B") + ")", StringComparison.Ordinal), "An in-book link names the line of its target.");
        Require(book.Markdown.Contains("](https://example.com/)", StringComparison.Ordinal), "An external link keeps its URL.");
        Require(book.Markdown.Contains("![Picture](res/OEBPS/images/picture.png)", StringComparison.Ordinal), "An image names its package path.");
        Require(markdown.Contains("- First item") && markdown.Contains("2\\. Second step") is false && markdown.Any(line => line.StartsWith("2. ", StringComparison.Ordinal)),
            "Lists keep their markers.");
        Require(!book.Markdown.Contains("color", StringComparison.OrdinalIgnoreCase) && !book.Markdown.Contains("<", StringComparison.Ordinal), "Book styling and markup are dropped.");
        Console.WriteLine($"PASS EPUB {(epub3 ? 3 : 2)} conversion: metadata, {(epub3 ? "nav" : "NCX")} TOC, cover, lists, in-book and external links, images, no book CSS");
    }

    private static void EndToEnd(string root, bool remote)
    {
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllBytes(Path.Combine(workspace, "closed.epub"), Fixture(epub3: true));
        File.WriteAllBytes(Path.Combine(workspace, "book.epub"), Fixture(epub3: true, filler: 12));
        File.WriteAllBytes(Path.Combine(workspace, "legacy.epub"), Fixture(epub3: false));
        File.WriteAllBytes(Path.Combine(workspace, "large.epub"), Fixture(epub3: true, filler: 2000));
        var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "epub-ui", Path.Combine(root, "state")) : null;
        try
        {
            if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            using var app = server is null ? new E2eWorkspace(workspace, profileRoot: Path.Combine(root, "profile"))
                : new E2eWorkspace(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                    "epub-ui", workspace, Path.Combine(root, "profile"), workspace);
            E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
            if (remote) app.ShowTool("files");
            Flow(app, remote);
            Console.WriteLine($"PASS EPUB reader {(remote ? "remote" : "local")} host: disabled by default, TOC, links, images, scroll and paged modes, Find, contrast, large book");
        }
        finally
        {
            if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }

    private static void Flow(E2eWorkspace app, bool remote)
    {
        var window = app.Window;
        // Disabled by default: no viewer claims .epub and the open path is unchanged.
        Require(!app.Workbench.PluginLoader.Registry.Active.Contains(Id), "The EPUB Reader is disabled by default.");
        app.Open("closed.epub", keep: true);
        E2eWorkspace.Settle(500);
        Require(!window.GetLogicalDescendants().OfType<EpubViewer>().Any(), "A disabled EPUB Reader mounts no viewer.");

        SetEnabled(app, true);
        app.Open("book.epub", keep: true);
        var viewer = Viewer(window);
        var preview = viewer.Preview!;
        var book = viewer.Book!;

        // Table of contents: one entry per nav point, and a click puts its heading at the top.
        var entries = viewer.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "EpubTocEntry").ToList();
        Require(entries.Count == 4, $"The sidebar lists the TOC: {entries.Count}.");
        app.Click(entries[1]);
        Settle();
        Require(Math.Abs(preview.Offset.Y - preview.LineOffset(book.Toc[1].Line)!.Value) < 1 && preview.Offset.Y > 0, "A TOC entry scrolls its chapter to the top.");
        Require(TopText(preview).Contains("Chapter Two", StringComparison.Ordinal), "The chapter's heading is at the top: " + TopText(preview));

        // Images and the cover render from the host's resources.
        app.Click(entries[0]);
        preview.Offset = default;
        E2eWorkspace.Until(() => preview.GetLogicalDescendants().OfType<Image>().Count(image => image.Name == "MarkdownImage" && image.Source is Bitmap) >= 2);

        // An in-book link jumps to its target; an external link is a link to its URL.
        var links = preview.GetLogicalDescendants().OfType<MarkdownLink>().ToList();
        var external = links.Single(link => Label(link) == "the web");
        Require(Equals(ToolTip.GetTip(external), "https://example.com/"), "An external link keeps its URL for the browser.");
        app.Click(links.Single(link => Label(link) == "Jump to B"));
        Settle();
        Require(TopText(preview).Contains("Section B", StringComparison.Ordinal), "An in-book link lands on its target: " + TopText(preview));

        // Book CSS never reaches the text: every rendered block uses the theme's text colour, readable in both themes.
        var original = Ui.Theme;
        try
        {
            foreach (var name in new[] { "dark", "light" })
            {
                Ui.Apply(SharpRail.UI.Rendering.Themes.Resolve(name));
                Settle();
                var ratio = Contrast(Ui.TextBrush.Color, Ui.Surface.Color);
                Require(ratio >= 4.5, $"Book text is readable on the {name} theme: contrast {ratio:0.0}.");
                Require(preview.GetLogicalDescendants().OfType<SelectableTextBlock>().All(text => text.Foreground is null || text.Foreground == Ui.TextBrush || text.Foreground == Ui.Muted),
                    "No book colour overrides the theme's text colour.");
                Require(viewer.Background == Ui.Surface, "The page uses the theme's surface.");
            }
        }
        finally { Ui.Apply(original); }

        // Find searches every chapter and steps through the matches with the selection highlighted.
        var find = FindAll(viewer);
        Require(find.Text == "1/3", "Find counts matches across chapters: " + find.Text);
        Press(window, Key.Enter, RawInputModifiers.None, PhysicalKey.Enter);
        Require(find.Text == "2/3", "Enter steps to the next match: " + find.Text);
        var selected = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(text => text.SelectionEnd > text.SelectionStart);
        Require(selected.SelectedText.Equals("needle", StringComparison.OrdinalIgnoreCase), "The current match is selected: " + selected.SelectedText);
        Press(window, Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter);
        Require(find.Text == "1/3", "Shift+Enter steps back: " + find.Text);
        Press(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);

        // Paged mode: viewport-sized pages, chapters starting pages, and turning with buttons and keys.
        app.Click(Named<Avalonia.Controls.Primitives.ToggleButton>(viewer, "EpubPagedMode"));
        E2eWorkspace.Until(() => viewer.PaginationComplete);
        var label = Named<TextBlock>(viewer, "EpubPage");
        Require(viewer.PageCount > 3, $"The book has pages: {viewer.PageCount}.");
        var pages = viewer.PageCount;
        viewer.GoToLine(1);
        Settle();
        Require(viewer.Page == 0 && label.Text == $"Page 1 of {pages}", "Paged mode starts on page 1: " + label.Text);
        app.Click(Named<Button>(viewer, "EpubNext"));
        Require(viewer.Page == 1 && label.Text == $"Page 2 of {pages}" && Math.Abs(preview.Offset.Y - Tops(viewer)[1]) < 1, "Next turns to page 2: " + label.Text);
        viewer.Focus();
        Press(window, Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight);
        Require(viewer.Page == 2, "The right arrow turns the page.");
        Press(window, Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft);
        app.Click(Named<Button>(viewer, "EpubPrevious"));
        Require(viewer.Page == 0, "Previous and the left arrow turn back.");
        foreach (var chapter in book.Chapters.Skip(1))
            Require(Tops(viewer).Any(top => Math.Abs(top - preview.LineOffset(chapter.Line, reveal: false).GetValueOrDefault(top)) < 1), "Every chapter starts a page.");
        CleanEdges(viewer, preview);

        // A TOC jump lands on the chapter's first page; a resize re-paginates and keeps the reader there.
        viewer.GoToLine(book.Toc.Single(entry => entry.Title == "Chapter Three").Line);
        Settle();
        Require(TopText(preview).Contains("Chapter Three", StringComparison.Ordinal), "A TOC jump in paged mode shows the chapter's page: " + TopText(preview));
        window.Width -= 360; window.Height -= 120;
        E2eWorkspace.Until(() => viewer.PaginationComplete && viewer.PageCount != pages);
        Settle();
        Require(TopText(preview).Contains("Chapter Three", StringComparison.Ordinal), "A resize keeps the reading location: " + TopText(preview));
        Require(Named<TextBlock>(viewer, "EpubPage").Text == $"Page {viewer.Page + 1} of {viewer.PageCount}", "The label follows the new pagination.");

        // Find in paged mode turns to the page holding the match.
        find = FindAll(viewer);
        Press(window, Key.Enter, RawInputModifiers.None, PhysicalKey.Enter);
        Settle();
        selected = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(text => text.SelectionEnd > text.SelectionStart);
        var at = selected.TranslatePoint(default, preview)!.Value.Y;
        Require(at >= -1 && at < preview.Viewport.Height, $"Find turns to the page with the match (at {at:0}).");
        Press(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
        window.Width += 360; window.Height += 120;
        app.Click(Named<Avalonia.Controls.Primitives.ToggleButton>(viewer, "EpubScrollMode"));
        Require(!viewer.Paged && Named<Border>(viewer, "EpubPageMask").IsVisible == false, "Scroll mode drops the page mask.");

        // The EPUB 2 package opens the same way.
        app.Open("legacy.epub", keep: true);
        var legacy = Viewer(window, viewer);
        Require(legacy.GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "EpubTocEntry") == 4, "The NCX table of contents fills the sidebar.");

        // A large book opens without holding the UI thread past the budget.
        var stall = Measure(() =>
        {
            app.Open("large.epub", keep: true);
            Viewer(window, legacy);
        });
        Require(stall < StallBudget, $"Opening a large book stalls the UI thread for {stall.TotalMilliseconds:0} ms (budget {StallBudget.TotalMilliseconds:0} ms).");
        var large = window.GetLogicalDescendants().OfType<EpubViewer>().Single(viewer => viewer.IsEffectivelyVisible);
        var sweep = Measure(() =>
        {
            app.Click(Named<Avalonia.Controls.Primitives.ToggleButton>(large, "EpubPagedMode"));
            E2eWorkspace.Until(() => large.PaginationComplete);
        });
        Require(sweep < StallBudget, $"Paginating a large book stalls the UI thread for {sweep.TotalMilliseconds:0} ms (budget {StallBudget.TotalMilliseconds:0} ms).");
        Console.WriteLine($"PASS EPUB large book ({(remote ? "remote" : "local")}): longest UI-thread stall {stall.TotalMilliseconds:0} ms opening, {sweep.TotalMilliseconds:0} ms paginating {large.PageCount} pages");
        SetEnabled(app, false);
        E2eWorkspace.Until(() => !window.GetLogicalDescendants().OfType<EpubViewer>().Any());
    }

    private static void SetEnabled(E2eWorkspace app, bool enabled)
    {
        var window = app.Window;
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
        E2eWorkspace.Until(() => settings.GetLogicalDescendants().OfType<Border>().Any(control => control.Name == "PluginRow_" + Id));
        var row = settings.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "PluginRow_" + Id);
        row.GetLogicalDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "PluginToggle").IsChecked = enabled;
        E2eWorkspace.Until(() => app.Workbench.PluginLoader.Registry.Active.Contains(Id) == enabled);
        settings.Close();
    }

    private static EpubViewer Viewer(Window window, EpubViewer? previous = null)
    {
        E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<EpubViewer>().Any(viewer => viewer != previous && viewer.IsEffectivelyVisible && viewer.Preview is not null));
        var viewer = window.GetLogicalDescendants().OfType<EpubViewer>().First(viewer => viewer != previous && viewer.IsEffectivelyVisible && viewer.Preview is not null);
        Settle();
        return viewer;
    }

    private static TextBlock FindAll(EpubViewer viewer)
    {
        var window = (Window)TopLevel.GetTopLevel(viewer)!;
        viewer.Focus();
        Press(window, Key.F, E2eWorkspace.Command, PhysicalKey.F);
        var bar = viewer.GetLogicalDescendants().OfType<FindBar>().Single();
        Require(bar.IsVisible, "Mod+F opens Find in the book.");
        bar.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "FindInput").Text = "needle";
        Settle();
        return bar.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "FindCount");
    }

    private static IReadOnlyList<double> Tops(EpubViewer viewer) => viewer.PageTops;

    // No text line crosses the bottom of the visible page: the page ends above any line it would cut.
    private static void CleanEdges(EpubViewer viewer, MarkdownPreview preview)
    {
        for (var index = 0; index < Math.Min(viewer.PageCount - 1, 6); index++)
        {
            viewer.GoToPage(index);
            Settle();
            var bottom = Tops(viewer)[index + 1] - Tops(viewer)[index];
            foreach (var text in preview.GetVisualDescendants().OfType<SelectableTextBlock>())
            {
                if (text.TranslatePoint(default, preview) is not { } point) continue;
                var top = point.Y + text.Padding.Top;
                foreach (var line in text.TextLayout.TextLines)
                {
                    Require(!(top < bottom - 0.5 && top + line.Height > bottom + 0.5), $"Page {index + 1} cuts a line at {bottom:0}: {text.Text}");
                    top += line.Height;
                }
            }
        }
    }

    private static TimeSpan Measure(Action action)
    {
        var longest = TimeSpan.Zero;
        var last = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Normal, (_, _) =>
        {
            if (last.Elapsed > longest) longest = last.Elapsed;
            last.Restart();
        });
        timer.Start();
        try
        {
            E2eWorkspace.Settle(100);
            longest = TimeSpan.Zero;
            last.Restart();
            action();
            E2eWorkspace.Settle(300);
        }
        finally { timer.Stop(); }
        return longest;
    }

    private static T Named<T>(Control root, string name) where T : Control => root.GetLogicalDescendants().OfType<T>().First(control => control.Name == name);

    private static string Label(MarkdownLink link) => ((TextBlock)link.Content!).Text ?? "";

    private static string TopText(MarkdownPreview preview) =>
        preview.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(text => (Text: text, Point: text.TranslatePoint(default, preview)))
            .Where(item => item.Point is { } point && point.Y >= -2 && point.Y < 80)
            .Select(item => item.Text.Text ?? string.Concat(item.Text.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text) ?? []))
            .FirstOrDefault() ?? "";

    private static void Press(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physical)
    {
        window.KeyPress(key, modifiers, physical, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Settle() => E2eWorkspace.Settle(250);

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value) { var c = value / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>
    /// A small book: three chapters with headings, lists, in-book and external links, an image and a cover, styled with
    /// CSS that would be unreadable on a dark theme. EPUB 3 carries a nav document, EPUB 2 an NCX. With
    /// <paramref name="filler"/>, every chapter gains that many paragraphs, for the large-book check.
    /// </summary>
    internal static byte[] Fixture(bool epub3, int filler = 0)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Text(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            void Bytes(string name, byte[] data)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
            Text("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Text("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            var fill = string.Concat(Enumerable.Range(0, filler).Select(index => $"<p>Filler paragraph {index} with enough words to wrap across the reading column and make the book long.</p>"));
            string Page(string title, string body) => $$"""<?xml version="1.0" encoding="utf-8"?><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>{{title}}</title><link rel="stylesheet" href="style.css"/><style>p { color: #000000; }</style></head><body style="background:#ffffff;color:#000000">{{body}}</body></html>""";
            Text("OEBPS/style.css", "body { color: #000; background: #fff; font-family: serif; }");
            Text("OEBPS/ch1.xhtml", Page("One", """<h1 id="one">Chapter One</h1><p style="color:#000">The <em>first</em> chapter has <strong>strong</strong> words&nbsp;and <code>code</code>.</p><ul><li>First item</li><li>Second item<ul><li>Nested item</li></ul></li></ul><ol><li>Step one</li><li>Second step</li></ol><p><a href="ch2.xhtml#section-b">Jump to B</a> or visit <a href="https://example.com/">the web</a>.</p><p><img src="images/picture.png" alt="Picture"/></p>""" + fill));
            Text("OEBPS/ch2.xhtml", Page("Two", """<h1>Chapter Two</h1><p>A needle in chapter two.</p>""" + fill + """<h2 id="section-b">Section B</h2><p>Section B is a link target.</p><blockquote><p>A quoted line.</p></blockquote>"""));
            Text("OEBPS/ch3.xhtml", Page("Three", """<h1>Chapter Three</h1><p>Another needle, and a third NEEDLE.</p>""" + "<pre>preformatted\n  text</pre>" + fill + "<hr/><p>The end.</p>"));
            Bytes("OEBPS/images/picture.png", Png(new SKColor(0x3b, 0x82, 0xf6)));
            Bytes("OEBPS/images/cover.png", Png(new SKColor(0xef, 0x44, 0x44)));
            var items = """<item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/><item id="ch2" href="ch2.xhtml" media-type="application/xhtml+xml"/><item id="ch3" href="ch3.xhtml" media-type="application/xhtml+xml"/><item id="css" href="style.css" media-type="text/css"/><item id="picture" href="images/picture.png" media-type="image/png"/>""";
            var spine = """<itemref idref="ch1"/><itemref idref="ch2"/><itemref idref="ch3"/>""";
            var metadata = """<dc:identifier id="uid">urn:uuid:5b128c0a-0000-4000-8000-000000000001</dc:identifier><dc:title>Fixture Book</dc:title><dc:creator>Ada Example</dc:creator><dc:language>en</dc:language>""";
            if (epub3)
            {
                Text("OEBPS/content.opf", $"""<?xml version="1.0" encoding="utf-8"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/">{metadata}<meta property="dcterms:modified">2026-01-01T00:00:00Z</meta></metadata><manifest>{items}<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="cover" href="images/cover.png" media-type="image/png" properties="cover-image"/></manifest><spine>{spine}</spine></package>""");
                Text("OEBPS/nav.xhtml", Page("Contents", """<nav epub:type="toc"><ol><li><a href="ch1.xhtml">Chapter One</a></li><li><a href="ch2.xhtml">Chapter Two</a><ol><li><a href="ch2.xhtml#section-b">Section B</a></li></ol></li><li><a href="ch3.xhtml">Chapter Three</a></li></ol></nav>"""));
            }
            else
            {
                Text("OEBPS/content.opf", $"""<?xml version="1.0" encoding="utf-8"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="uid"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">{metadata}<meta name="cover" content="cover"/></metadata><manifest>{items}<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/><item id="cover" href="images/cover.png" media-type="image/png"/></manifest><spine toc="ncx">{spine}</spine></package>""");
                static string Point(string id, int order, string title, string src, string children = "") =>
                    $"""<navPoint id="{id}" playOrder="{order}"><navLabel><text>{title}</text></navLabel><content src="{src}"/>{children}</navPoint>""";
                Text("OEBPS/toc.ncx", $"""<?xml version="1.0" encoding="utf-8"?><ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head><meta name="dtb:uid" content="urn:uuid:5b128c0a-0000-4000-8000-000000000001"/></head><docTitle><text>Fixture Book</text></docTitle><navMap>{Point("p1", 1, "Chapter One", "ch1.xhtml")}{Point("p2", 2, "Chapter Two", "ch2.xhtml", Point("p3", 3, "Section B", "ch2.xhtml#section-b"))}{Point("p4", 4, "Chapter Three", "ch3.xhtml")}</navMap></ncx>""");
            }
        }
        return output.ToArray();
    }

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(160, 100);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}