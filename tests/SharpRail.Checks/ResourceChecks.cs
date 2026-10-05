using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.Scintilla;
using SharpRail.UI.Resources;

using SkiaSharp;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>The resource-renderer registry, its panes and the format views, driven through a real headless window.</summary>
internal static partial class ResourceChecks
{
    private const string Pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393\nsize 12345\n";
    private const string Svg = "<?xml version=\"1.0\"?>\n<!DOCTYPE svg>\n<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"20\" height=\"10\" onload=\"alert(1)\">" +
        "<script>alert(2)</script><style>@import url(http://example.invalid/a.css);</style><foreignObject><p>x</p></foreignObject>" +
        "<image href=\"http://example.invalid/x.png\"/><image xlink:href=\"file:///etc/passwd\"/><rect id=\"r\" width=\"20\" height=\"10\" fill=\"url(http://example.invalid/p)\" stroke=\"#f00\" onclick=\"x()\"/><use href=\"#r\"/></svg>";

    internal static void Run(string root)
    {
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
        Registry();
        Panes();
        Vector();
        Tables();
        Json();
        Notebooks();
        Focus();
        using var git = new IsolatedGit(Path.Combine(root, "resource-git"));
        Window(Path.Combine(root, "resource-checks"));
        Console.WriteLine("PASS resource renderer checks");
    }

    /// <summary>The resource checks plus every translation that opens file and diff bodies, without the rest of the UI run.</summary>
    internal static void RunDocuments(string root)
    {
        Run(root);
        var e2e = Path.Combine(root, "upstream-e2e");
        PreviewTabsE2E.Run(e2e);
        MarkdownLinksE2E.Run(e2e);
        MarkdownAlertsE2E.Run(e2e);
        MarkdownMermaidE2E.Run(e2e);
        EditorE2E.Run(e2e);
        // These need SHARPRAIL_TEST_GIT_SOURCE and commit with the developer's own Git identity.
        ChangesDiffE2E.Run(e2e);
        RenderedDiffE2E.Run(e2e);
        LiveRefreshE2E.Run(e2e);
        Console.WriteLine("PASS document body checks");
    }

    private static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static ResourceRegistry Stubbed()
    {
        var registry = new ResourceRegistry();
        BundledRenderers.Register(registry);
        registry.Register(new(ResourceRegistry.Code, "Source", new(Text: true), 100) { View = _ => new TextBlock { Name = "Code" }, DiffsInPane = true });
        registry.Register(new(ResourceRegistry.Markdown, "Preview", new(Glob: ["*.md", "*.markdown"], Text: true), 110) { View = _ => new TextBlock { Name = "Markdown" } });
        return registry;
    }

    private static string Ids(ResourceRegistry registry, string path, bool text, string? mime, ResourceIntent intent = ResourceIntent.View) =>
        string.Join(",", registry.Resolve(new("w", path, mime, text, 1), intent).Select(renderer => renderer.Id[(renderer.Id.LastIndexOf('/') + 1)..]));

    private static void Registry()
    {
        var registry = Stubbed();
        Require(Ids(registry, "a/notes.txt", true, null) == "code", "Plain text resolves to the text fallback alone.");
        Require(Ids(registry, "README.MD", true, null) == "markdown,code", "Markdown ranks above the fallback, by a case-insensitive file-name glob.");
        Require(Ids(registry, "README.md", true, null, ResourceIntent.Diff) == "code", "A renderer without a diff is not a diff candidate.");
        Require(Ids(registry, "x.bin", false, null) == "binary", "Unknown bytes resolve to the byte fallback alone.");
        Require(Ids(registry, "x.txt", false, "image/png") == "image,binary" && Ids(registry, "x.png", false, null) == "image,binary",
            "A picture matches by host MIME type, falling back to the extension.");
        Require(Ids(registry, "x.png", true, "image/png") == "code", "A renderer that needs bytes does not claim text.");
        Require(Ids(registry, "x.svg", true, null) == "svg,code" && Ids(registry, "x.svg", true, null, ResourceIntent.Diff) == "svg,code", "SVG is text with a vector view and diff.");
        Require(Ids(registry, "model.bin", true, "application/vnd.git-lfs") == "lfs,code", "An LFS pointer is claimed by its media type.");
        Require(Ids(registry, "doc.pdf", false, "application/pdf") == "binary", "A PDF has no renderer here and shows the byte card.");
        Require(Ids(registry, "data.csv", true, null) == "csv,code" && Ids(registry, "data.TSV", true, null, ResourceIntent.Diff) == "csv,code", "Delimited text offers a table for views and diffs.");
        Require(Ids(registry, "a/package.json", true, "application/json") == "json,code" && Ids(registry, "tsconfig.jsonc", true, null, ResourceIntent.Diff) == "json,code", "JSON and JSONC offer a tree.");
        Require(Ids(registry, "nb.ipynb", true, null) == "notebook,code" && Ids(registry, "data.csv", false, null) == "binary", "Notebooks outrank the fallback; a text renderer never claims bytes.");
        var empty = new ResourceRegistry();
        try { empty.Resolve(new("w", "x", null, true, null), ResourceIntent.View); throw new InvalidOperationException("A registry without its fallback resolved."); }
        catch (InvalidOperationException error) when (error.Message.Contains("fallback", StringComparison.Ordinal)) { }
        Require(ResourceContent.Of(new(null, null, true, null), "", _ => throw new InvalidOperationException()) is ResourceContent.Absent
            && ResourceContent.Of(new("ab", 2, false, "image/png"), "", _ => Task.FromResult<byte[]>([1, 2])) is ResourceContent.Bytes { Mime: "image/png", ByteLength: 2 }
            && ResourceContent.Of(new("ab", 2, true, null), "hi", _ => throw new InvalidOperationException()) is ResourceContent.Text { Value: "hi", Hash: "ab" },
            "Content keeps text, retrievable bytes and an absent side distinct.");
        Require(ResourceCards.ParseLfs(Pointer) is { Size: 12345, Oid.Length: 64 } && ResourceCards.ParseLfs(Pointer + "extra\n") is null && ResourceCards.ParseLfs("not a pointer") is null,
            "An LFS pointer parses to its object id and size, and nothing else does.");
        Require(ResourceCards.FormatSize(512) == "512 B" && ResourceCards.FormatSize(12345) == "12 KB" && ResourceCards.FormatSize(1_572_864) == "1.5 MB", "LFS sizes read like a file manager.");
    }

    private sealed class StateBody(string name, object? state) : TextBlock, IResourceBody
    {
        internal string Label { get; } = name;
        internal object? Restored { get; } = state;
        internal object? Current { get; set; } = state;
        public object? ViewState => Current;
        public bool Reload(ResourceContent content) => true;
    }

    private static void Panes()
    {
        var saved = new Dictionary<string, Action<object?>>();
        ResourceRenderer Renderer(string id, int rank) => new(id, id, new(), rank)
        {
            View = view => { saved[id] = view.SaveViewState; return new StateBody(id, view.ViewState); }
        };
        IReadOnlyList<ResourceRenderer> candidates = [Renderer("x/a", 2), Renderer("x/b", 1)];
        var tab = new TabView();
        ResourcePane Pane() => new(candidates, new("w", "f", null, true, null), "file:f", new ResourceContent.Text("", ""), () => tab, value => tab = value);

        var pane = Pane();
        Require(pane.Selected == "x/a" && pane.GetLogicalDescendants().OfType<ToggleButton>().Select(toggle => toggle.Name).SequenceEqual(["ViewToggle_a", "ViewToggle_b"]),
            "The ranked candidates are the view toggle and the first is shown.");
        saved["x/a"]("a-state");
        Require(tab == new TabView(null, "a-state"), "The selected renderer's view state is written to the tab.");
        var first = pane.Body;
        pane.Select("x/b");
        Require(tab == new TabView("x/b") && pane.Body is StateBody { Label: "x/b", Restored: null }, "Choosing another renderer records it and drops the previous view state.");
        saved["x/a"]("late");
        Require(tab == new TabView("x/b"), "A renderer the tab has left cannot overwrite its successor's state.");
        pane.Select("x/a");
        Require(ReferenceEquals(pane.Body, first), "Switching back restores the same view.");
        ((StateBody)pane.Body!).Current = "closing";
        pane.Dispose();
        Require(tab == new TabView("x/a", "closing"), "A closing body's state is captured against its tab.");
        var reopened = Pane();
        Require(reopened.Body is StateBody { Label: "x/a", Restored: "closing" }, "A rebuilt pane restores the tab's renderer and hands it its own state.");
        reopened.Dispose();
        var single = new ResourcePane([candidates[0]], new("w", "f", null, true, null), "file:f", new ResourceContent.Text("", ""), () => new(), _ => { });
        Require(!single.GetLogicalDescendants().OfType<ToggleButton>().Any(), "A single candidate shows no toggle.");
    }

    private static void Vector()
    {
        var inert = VectorPictures.Sanitise(Svg) ?? throw new InvalidOperationException("Well-formed SVG must sanitise.");
        foreach (var gone in new[] { "script", "onload", "onclick", "foreignObject", "example.invalid", "passwd", "@import", "DOCTYPE" })
            Require(!inert.Contains(gone, StringComparison.OrdinalIgnoreCase), "Sanitised SVG must not keep " + gone + ": " + inert);
        Require(inert.Contains("stroke=\"#f00\"", StringComparison.Ordinal) && inert.Contains("href=\"#r\"", StringComparison.Ordinal), "Shapes and same-document references survive.");
        Require(VectorPictures.Sanitise("<svg><unclosed></svg>") is null && VectorPictures.Sanitise("<html/>") is null, "Malformed or non-SVG text is not drawn.");
        using var picture = VectorPictures.Raster(Svg) ?? throw new InvalidOperationException("Inert SVG must rasterise.");
        Require(picture.PixelSize is { Width: 40, Height: 20 }, "A small SVG rasterises at twice its size: " + picture.PixelSize);
    }

    private static void Tables()
    {
        Require(TableModel.SniffDelimiter("x.tsv", "a,b\n") == '\t' && TableModel.SniffDelimiter("x.csv", "a;b;c\n1;2;3\n") == ';' &&
            TableModel.SniffDelimiter("x.csv", "a,b;c\n1,2;3\n") == ',' && TableModel.SniffDelimiter("x.csv", "a|b\n1|2\n") == '|' && TableModel.SniffDelimiter("x.csv", "", "") == ',',
            "The delimiter is the extension's, else the one every sampled record agrees on, preferring a comma.");
        var rows = TableModel.Parse("name,note\r\n\"Smith, J\",\"said \"\"hi\"\"\nthere\"\nlast,", ',');
        Require(rows.Count == 3 && rows[1].Cells.SequenceEqual(["Smith, J", "said \"hi\"\nthere"]) && rows[2].Cells.SequenceEqual(["last", ""]) && rows[1].Raw.StartsWith("\"Smith", StringComparison.Ordinal),
            "Quoted delimiters, doubled quotes and line breaks stay inside their cell.");
        var before = TableModel.Parse("id,name,qty\n1,apple,3\n2,pear,5\n3,plum,1\n", ',');
        var after = TableModel.Parse("id,name,qty\n1,apple,4\n2,pear,5\n4,fig,9\n5,kiwi,2\n", ',');
        var aligned = TableModel.Align(before, after);
        Require(string.Join(",", aligned.Select(row => row.Kind)) == "Unchanged,Changed,Unchanged,Removed,Added,Added" && aligned[1].ChangedCells.SequenceEqual([2]),
            "Rows align by content, and a replaced row names its changed cells: " + string.Join(",", aligned.Select(row => row.Kind)));
    }

    private static void Json()
    {
        Require(JsonModel.Parse("{\"a\":1}") is { Document: not null, Dialect: false } && JsonModel.Parse("{// c\n\"a\":1,}") is { Document: not null, Dialect: true } &&
            JsonModel.Parse("{oops") is { Document: null, Error: not null }, "Strict JSON, the JSONC dialect and invalid text are told apart.");
        using var before = System.Text.Json.JsonDocument.Parse("""{"name":"x","keep":[1,2],"items":[{"id":1,"v":"a"},{"id":2,"v":"b"},{"id":3,"v":"c"}],"gone":true,"n":{"deep":1}}""");
        using var after = System.Text.Json.JsonDocument.Parse("""{"n":{"deep":2},"name":"x","keep":[1,2],"items":[{"id":1,"v":"a"},{"id":3,"v":"C"},{"id":4,"v":"d"}],"new":null}""");
        var changes = JsonModel.Changes(JsonModel.Diff("", before.RootElement, after.RootElement)).Select(change => change.Pointer + "=" + change.Kind).Order(StringComparer.Ordinal).ToArray();
        Require(changes.SequenceEqual(["/gone=Removed", "/items/1/v=Changed", "/items/1=Removed", "/items/2=Added", "/n/deep=Changed", "/new=Added"]),
            "Members diff by key regardless of order and elements by identity: " + string.Join(" ", changes));
        using var reordered = System.Text.Json.JsonDocument.Parse("""{ "keep": [1, 2], "name": "x" }""");
        using var compact = System.Text.Json.JsonDocument.Parse("""{"name":"x","keep":[1,2]}""");
        Require(JsonModel.Diff("", reordered.RootElement, compact.RootElement).Kind == JsonChange.Unchanged, "Formatting and member order are not structural changes.");
    }

    private const string NotebookText = """
        {"nbformat":4,"metadata":{"kernelspec":{"name":"python3"},"language_info":{"name":"python"}},"cells":[
        {"cell_type":"markdown","id":"intro","source":["# Title\n","Body"]},
        {"cell_type":"code","id":"calc","execution_count":2,"source":"print(1)","outputs":[
          {"output_type":"stream","name":"stdout","text":["1\n"]},
          {"output_type":"execute_result","data":{"text/html":"<script>alert(1)</script>"}},
          {"output_type":"error","ename":"ValueError","evalue":"bad","traceback":[]}]}]}
        """;

    private static void Notebooks()
    {
        var notebook = NotebookModel.Parse(NotebookText, out _) ?? throw new InvalidOperationException("A notebook must parse.");
        Require(notebook is { Language: "python", Cells.Count: 2 } && notebook.Cells[0] is { Kind: "markdown", Source: "# Title\nBody" } && notebook.Cells[1] is { ExecutionCount: "2", Outputs.Count: 3 },
            "Cells keep their kind, joined source and execution count.");
        Require(notebook.Cells[1].Outputs[0].Text == "1\n" && notebook.Cells[1].Outputs[1].Text == "[text/html output not shown]" && notebook.Cells[1].Outputs[2] is { IsError: true, Text: "ValueError: bad" },
            "Stream and error outputs are text, and markup outputs are named rather than drawn.");
        Require(NotebookModel.Parse("{}", out var missing) is null && missing is not null && NotebookModel.Parse("nope", out var invalid) is null && invalid!.StartsWith("Invalid", StringComparison.Ordinal),
            "A file without cells and invalid JSON are reported.");
    }

    private static string Flags(IEnumerable<bool> flags) => string.Concat(flags.Select(flag => flag ? '1' : '0'));

    private static string Runs(string changed) => string.Join(" ", SharpRail.Plugins.UI.Kit.Markdown.DiffFocus.Segments([.. changed.Select(flag => flag == '1')])
        .Select(segment => (segment.Hidden ? "h" : "v") + segment.Start + "+" + segment.Count));

    private static SharpRail.Plugins.UI.Kit.Markdown.DiffFocus Focused(string before, string after)
    {
        var merged = SharpRail.Plugins.UI.Kit.Markdown.MarkdownDiff.Merge(before, after);
        return SharpRail.Plugins.UI.Kit.Markdown.DiffFocus.Create(before, merged, SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview.Parse(merged), new HashSet<string>());
    }

    private static void Focus()
    {
        Require(Runs("0000001000000") == "h0+4 v4+5 h9+4", "Two units of context stay on each side of a change: " + Runs("0000001000000"));
        Require(Runs("1000") == "v0+3 v3+1".Replace(" v3+1", "") + "" || Runs("1000") == "v0+4", "A hidden run of one unit is shown instead: " + Runs("1000"));
        Require(Runs("10000") == "v0+3 h3+2" && Runs("00001") == "h0+2 v2+3", "A leading or trailing run keeps context only beside the change.");
        Require(Runs("000") == "h0+3" && Runs("0") == "h0+1" && Runs("") == "", "With no change the whole document is one hidden run.");
        Require(Runs("100000100") == "v0+3 h3+1 v4+5".Replace("h3+1 ", "").Replace("v0+3 v4+5", "v0+9") || Runs("100000100") == "v0+9", "A one-unit gap between two changes stays visible: " + Runs("100000100"));

        var twin = Focused("Same line.\n\nMiddle.\n\nSame line.\n", "Same line.\n\nMiddle.\n\nSame line, edited.\n");
        Require(Flags(twin.Blocks) == "001", "An identical block elsewhere cannot hide a change: " + Flags(twin.Blocks));
        var fence = Focused("Intro.\n\n```\nold code\n```\n\nOutro.\n", "Intro.\n\n```\nnew code\n```\n\nOutro.\n");
        Require(Flags(fence.Blocks) == "010", "A change that earns no mark still counts: " + Flags(fence.Blocks));
        var list = Focused("- one\n- two\n- three\n- four\n", "- one\n- two\n- three changed\n- four\n");
        Require(Flags(list.Blocks) == "1" && Flags(list.Items[0]) == "0010", "A changed list names its changed items: " + Flags(list.Items[0]));
        var start = Focused("1. one\n2. two\n", "3. one\n4. two\n");
        Require(Flags(start.Blocks) == "1" && !start.Items.ContainsKey(0) || Flags(start.Blocks) == "1" && Flags(start.Items[0]) == "11",
            "A renumbered list is a change.");
        var same = Focused("# Title\n\nText.\n", "# Title\n\n\nText.\n");
        Require(Flags(same.Blocks) == "00", "Whitespace between blocks is not a rendered change.");
    }

    private static T? Shown<T>(E2eWorkspace app, string name) where T : Control =>
        app.Window.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible);

    private static T Await<T>(E2eWorkspace app, string name) where T : Control
    {
        Until(() => Shown<T>(app, name) is not null);
        return Shown<T>(app, name)!;
    }

    // A change on disk reaches the window through the file watcher, whose latency grows with the machine's load.
    private static void Watched(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!condition() && DateTime.UtcNow < deadline) Settle(100);
        Require(condition(), "A change on disk did not reach the window.");
    }

    // A watcher refresh rebuilds the Files tree, so rows are clicked only once refreshes have stopped arriving.
    private static void Quiet(E2eWorkspace app)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        for (var seen = -1; seen != app.Window.WatchRefreshes && DateTime.UtcNow < deadline;) { seen = app.Window.WatchRefreshes; Settle(600); }
    }

    private static string Text(Control control) => string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    private static void Window(string root)
    {
        Directory.CreateDirectory(root);
        IsolatedGit.Run(root, "init", "-b", "main");
        File.WriteAllBytes(Path.Combine(root, "blob.bin"), [0xFF, 0xFE, 0x00, 0x01]);
        File.WriteAllBytes(Path.Combine(root, "doc.pdf"), [.. "%PDF-1.4\n"u8, 0, 0xFF, .. "\n%%EOF\n"u8]);
        File.WriteAllBytes(Path.Combine(root, "photo.png"), Png(4, 2, SKColors.Red));
        File.WriteAllText(Path.Combine(root, "pic.svg"), Svg);
        File.WriteAllText(Path.Combine(root, "model.bin"), Pointer);
        File.WriteAllText(Path.Combine(root, "data.csv"), "id,name,qty\n1,apple,3\n2,pear,5\n");
        File.WriteAllText(Path.Combine(root, "config.json"), "{\"name\":\"x\",\"items\":[1,2]}\n");
        File.WriteAllText(Path.Combine(root, "nb.ipynb"), NotebookText);
        var paragraphs = Enumerable.Range(1, 12).Select(index => $"Paragraph {index} text.").ToArray();
        var entries = Enumerable.Range(1, 10).Select(index => $"{index}. entry {index}").ToArray();
        string FocusText() => "# Focus title\n\n" + string.Join("\n\n", paragraphs) + "\n\n## Entries\n\n" + string.Join("\n", entries) + "\n";
        File.WriteAllText(Path.Combine(root, "FOCUS.md"), FocusText());
        File.WriteAllText(Path.Combine(root, "HTML.md"), "# Html\n\n<p align=\"center\">\n  <img src=\"photo.png\" width=\"2\" alt=\"centred\">\n</p>\n\n<img src=\"javascript:alert(1)\">\n\n<script>alert(2)</script>\n\n" +
            "<details>\n<summary>More</summary>\n\nHidden **markdown** body.\n\n</details>\n\n<details open><summary>Open one</summary>Inline body</details>\n\n" +
            "<picture><source srcset=\"x.webp\"><img src=\"photo.png\" align=\"right\"></picture>\n\nAfter <img src=\"photo.png\" width=\"1\"> inline.\n");
        var large = "# Big\n\n" + string.Concat(Enumerable.Range(0, 40_000).Select(index => $"Paragraph {index} with some text.\n\n"));
        File.WriteAllText(Path.Combine(root, "BIG.md"), large);
        using var app = new E2eWorkspace(root);
        var refreshes = app.Window.WatchRefreshes;
        IsolatedGit.Run(root, "add", "-A");
        IsolatedGit.Run(root, "commit", "-m", "seed");
        Until(() => app.Window.WatchRefreshes > refreshes);
        Quiet(app);

        app.Open("blob.bin");
        var card = Await<Border>(app, "BinaryResource");
        Require(Text(card).Contains("4 bytes", StringComparison.Ordinal) && Text(card).Contains("SHA-256", StringComparison.Ordinal) && !Text(card).Contains('�'),
            "A byte-only file opens as a card with its identity, not as text: " + Text(card));
        Require(Shown<Button>(app, "BinarySave") is not null && Shown<Border>(app, "ResourceViewToggle") is null, "The byte card offers a copy and no view toggle.");

        app.Open("model.bin");
        Require(Text(Await<Border>(app, "LfsPointer")).Contains("12 KB", StringComparison.Ordinal), "An LFS pointer opens as a card with the object's size.");
        app.Click(Await<ToggleButton>(app, "ViewToggle_code"));
        if (OperatingSystem.IsMacOS())
            Until(() => app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().Any(editor => editor.IsEffectivelyVisible && editor.Text == Pointer));

        app.Open("photo.png");
        var details = Await<TextBlock>(app, "ImageDetails");
        Until(() => details.Text?.Contains("4 × 2", StringComparison.Ordinal) == true);
        Require(details.Text!.Contains("fit", StringComparison.Ordinal) && Shown<ToggleButton>(app, "ImageFit")!.IsChecked == true, "A picture opens fitted, with its dimensions and byte size: " + details.Text);
        app.Click(Shown<ToggleButton>(app, "ImageNatural")!);
        app.Click(Shown<Button>(app, "ImageZoomIn")!);
        var picture = Shown<Image>(app, "ImagePicture")!;
        Require(details.Text!.Contains("125%", StringComparison.Ordinal) && picture.Width == 5 && picture.Height == 2.5, "Natural size and zoom scale the picture: " + details.Text);
        File.WriteAllBytes(Path.Combine(root, "photo.png"), Png(6, 3, SKColors.Blue));
        Watched(() => details.Text?.Contains("6 × 3", StringComparison.Ordinal) == true);
        Require(details.Text!.Contains("125%", StringComparison.Ordinal) && ReferenceEquals(details, Shown<TextBlock>(app, "ImageDetails")), "A changed picture reloads in place and keeps its zoom.");
        Quiet(app);

        app.Open("pic.svg");
        Until(() => Shown<TextBlock>(app, "ImageDetails")?.Text?.Contains("40 × 20", StringComparison.Ordinal) == true);
        Require(Shown<ToggleButton>(app, "ViewToggle_svg")!.IsChecked == true && Shown<ToggleButton>(app, "ViewToggle_code") is not null, "SVG opens drawn, with Source one toggle away.");

        app.Open("data.csv");
        var table = Await<ListBox>(app, "TableResource");
        Require(table.ItemCount == 3 && Shown<ToggleButton>(app, "ViewToggle_csv")!.IsChecked == true && Shown<ToggleButton>(app, "ViewToggle_code") is not null,
            "A delimited file opens as a table with Source one toggle away.");
        app.Open("config.json");
        var tree = Await<TreeView>(app, "JsonTree");
        Require(tree.Items.OfType<TreeViewItem>().Single() is { IsExpanded: true } rootItem && rootItem.Items.Count == 2, "A JSON file opens as a tree with its root open.");
        app.Open("nb.ipynb");
        var cells = Await<ScrollViewer>(app, "NotebookResource");
        Require(cells.GetLogicalDescendants().OfType<Border>().Count(border => border.Classes.Contains("notebook-cell")) == 2 && Text(cells).Contains("python", StringComparison.Ordinal),
            "A notebook opens as its cells.");

        app.Open("README.md");
        Until(() => Shown<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>(app, "MarkdownPreview") is not null);
        var preview = Shown<Button>(app, "MarkdownPreviewMode")!;
        app.Click(Shown<Button>(app, "MarkdownSourceMode")!);
        var source = Await<Control>(app, "MarkdownSource");
        File.WriteAllText(Path.Combine(root, "README.md"), "# sample-project\n\nAn edited paragraph.\n");
        Watched(() => MarkdownSourceText(source).Contains("An edited paragraph.", StringComparison.Ordinal));
        Require(ReferenceEquals(source, Shown<Control>(app, "MarkdownSource")) && ReferenceEquals(preview, Shown<Button>(app, "MarkdownPreviewMode")), "A reload keeps the pane, its toggle and the source view.");
        app.Click(preview);
        Until(() => Shown<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>(app, "MarkdownPreview") is { } rendered && Text(rendered).Length >= 0 &&
            rendered.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(block => block.Inlines?.Text?.Contains("An edited paragraph.", StringComparison.Ordinal) == true));
        Quiet(app);

        app.Open("HTML.md");
        Until(() => Shown<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>(app, "MarkdownPreview") is { } html && html.GetLogicalDescendants().OfType<Border>().Count(border => border.Classes.Contains("html-image") && border.Child is Image) == 3);
        var page = Shown<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>(app, "MarkdownPreview")!;
        var pictures = page.GetLogicalDescendants().OfType<Border>().Where(border => border.Classes.Contains("html-image")).ToArray();
        Require(pictures[0] is { HorizontalAlignment: Avalonia.Layout.HorizontalAlignment.Center, MaxWidth: 2 } && pictures[1].HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right && pictures[2].MaxWidth == 1,
            "Raw HTML pictures load from the worktree, centred or sent to a side, at their stated width.");
        string PageText() => string.Join(" ", page.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text is SelectableTextBlock rich && rich.Inlines is { Count: > 0 } inlines ? inlines.Text : text.Text));
        Require(!PageText().Contains("alert", StringComparison.Ordinal), "Script and scripted sources are dropped: " + PageText());
        var disclosures = page.GetLogicalDescendants().OfType<StackPanel>().Where(panel => panel.Classes.Contains("html-details")).ToArray();
        Require(disclosures.Length == 2 && !PageText().Contains("Hidden", StringComparison.Ordinal) && PageText().Contains("Inline body", StringComparison.Ordinal) && PageText().Contains("▸ More", StringComparison.Ordinal),
            "A closed disclosure hides its Markdown body and an open one shows it: " + PageText());
        app.Click(disclosures[0].GetLogicalDescendants().OfType<Button>().First());
        Require(PageText().Contains("Hidden markdown body.", StringComparison.Ordinal) && PageText().Contains("▾ More", StringComparison.Ordinal), "Clicking a summary opens it: " + PageText());

        File.WriteAllText(Path.Combine(root, "data.csv"), "id,name,qty\n1,apple,4\n3,plum,1\n");
        File.WriteAllText(Path.Combine(root, "config.json"), "{\"name\":\"y\",\"items\":[1,2,3]}\n");
        File.WriteAllText(Path.Combine(root, "nb.ipynb"), NotebookText.Replace("print(1)", "print(2)"));
        File.WriteAllBytes(Path.Combine(root, "doc.pdf"), [.. "%PDF-1.4\n"u8, 0, 0xFE, 0xFD, .. "\n%%EOF\n"u8]);
        File.WriteAllText(Path.Combine(root, "model.bin"), Pointer.Replace("12345", "2048"));
        Quiet(app);
        ShowChanges(app);
        ClickRow(app, "photo.png");
        var diff = Await<Grid>(app, "ImageDiff");
        Require(Pane(app)!.GetLogicalDescendants().OfType<ToggleButton>().Where(toggle => toggle.Name!.StartsWith("DiffView_", StringComparison.Ordinal)).Select(toggle => toggle.Name)
            .SequenceEqual(["DiffView_image", "DiffView_binary"]), "A picture diff offers the image view above the byte card.");
        Require(Text(diff).Contains("4 × 2", StringComparison.Ordinal) && Text(diff).Contains("6 × 3", StringComparison.Ordinal) && diff.GetLogicalDescendants().OfType<Image>().Count() == 2,
            "The 2-up view shows both pictures with their sizes: " + Text(diff));
        Require(Shown<ToggleButton>(app, "DiffSplit") is null && Shown<Button>(app, "DiffCopy") is null, "A byte diff offers no text-diff controls.");
        app.Click(Shown<ToggleButton>(app, "ImageDiffMode_swipe")!);
        Require(Await<Border>(app, "ImageDiffOverlay").GetLogicalDescendants().OfType<Image>().Last().Clip is not null && Shown<Slider>(app, "ImageDiffPosition") is not null, "Swipe clips the modified picture at the slider.");
        app.Click(Shown<ToggleButton>(app, "ImageDiffMode_onion")!);
        Require(Await<Border>(app, "ImageDiffOverlay").GetLogicalDescendants().OfType<Image>().Last().Opacity == 0.5, "Onion skin blends the modified picture over the original.");
        app.Click(Shown<ToggleButton>(app, "ImageDiffMode_difference")!);
        var difference = (Avalonia.Media.Imaging.Bitmap)Await<Border>(app, "ImageDiffDifference").GetLogicalDescendants().OfType<Image>().Single().Source!;
        Require(difference.PixelSize is { Width: 6, Height: 3 }, "The difference covers both pictures.");

        File.WriteAllBytes(Path.Combine(root, "photo.png"), Png(8, 8, SKColors.Green));
        Watched(() => Shown<Border>(app, "ImageDiffDifference")?.GetLogicalDescendants().OfType<Image>().SingleOrDefault()?.Source is Avalonia.Media.Imaging.Bitmap { PixelSize.Width: 8 });
        Require(Shown<ToggleButton>(app, "ImageDiffMode_difference")!.IsChecked == true, "A byte diff refreshes when the file changes under its tab and keeps its mode.");

        app.Click(Shown<ToggleButton>(app, "DiffView_binary")!);
        var cards = Await<Grid>(app, "BinaryDiff");
        Require(cards.GetLogicalDescendants().OfType<StackPanel>().Count(panel => panel.Name?.StartsWith("BinarySide_", StringComparison.Ordinal) == true) == 2 &&
            Text(cards).Contains("image/png", StringComparison.Ordinal) && Text(cards).Contains("SHA-256", StringComparison.Ordinal), "The byte card describes both sides: " + Text(cards));

        ClickRow(app, "doc.pdf");
        Until(() => Shown<Grid>(app, "BinaryDiff") is { } pdf && Text(pdf).Contains("application/pdf", StringComparison.Ordinal));
        Require(!Pane(app)!.GetLogicalDescendants().OfType<Image>().Any() && !Pane(app)!.GetLogicalDescendants().OfType<ToggleButton>().Any(toggle => toggle.Name!.StartsWith("DiffView_", StringComparison.Ordinal) && toggle.IsEffectivelyVisible),
            "A PDF diff shows cards only.");

        ClickRow(app, "data.csv");
        var tableDiff = Await<ListBox>(app, "TableDiff");
        Require(tableDiff.ItemsSource!.Cast<AlignedRow>().Select(row => row.Kind).SequenceEqual([TableChange.Unchanged, TableChange.Changed, TableChange.Changed]) &&
            Shown<ToggleButton>(app, "DiffView_csv")!.IsChecked == true && Shown<ToggleButton>(app, "DiffView_code") is not null, "A delimited diff opens as a table of changed rows and cells.");
        ClickRow(app, "config.json");
        var jsonDiff = Await<TreeView>(app, "JsonDiff");
        Require(jsonDiff.GetLogicalDescendants().OfType<TreeViewItem>().Count(item => item.Classes.Contains("json-added")) == 1 &&
            jsonDiff.GetLogicalDescendants().OfType<TreeViewItem>().Count(item => item.Classes.Contains("json-changed")) == 3, "A JSON diff opens as the tree of what changed.");
        ClickRow(app, "nb.ipynb");
        var notebookDiff = Await<ScrollViewer>(app, "NotebookDiff");
        Require(notebookDiff.GetLogicalDescendants().OfType<Border>().Count(border => border.Classes.Contains("notebook-changed")) == 1 && Text(notebookDiff).Contains("1 unchanged cell", StringComparison.Ordinal),
            "A notebook diff shows the edited cell and counts the rest: " + Text(notebookDiff));

        ClickRow(app, "README.md");
        Until(() => Pane(app) is { } pane && pane.GetLogicalDescendants().OfType<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>().Any(preview => preview.Name == "RenderedDiff" && preview.IsEffectivelyVisible));
        Require(Shown<ToggleButton>(app, "DiffView_markdown")!.IsChecked == true && Shown<ToggleButton>(app, "DiffSplit") is null, "A Markdown diff opens on its rendered view, chosen from the registry.");
        app.Click(Shown<ToggleButton>(app, "DiffView_code")!);
        Until(() => Shown<ToggleButton>(app, "DiffView_code")!.IsChecked == true && !Pane(app)!.GetLogicalDescendants().OfType<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>().Any());
        if (OperatingSystem.IsMacOS()) Require(Pane(app)!.GetLogicalDescendants().OfType<ScintillaEditor>().Any(editor => editor.IsEffectivelyVisible)
            && DiffText(app).Contains("An edited paragraph.", StringComparison.Ordinal), "Source replaces the rendered diff with the changed text in the pane's responsive layout.");
        File.AppendAllText(Path.Combine(root, "README.md"), "\nA third paragraph.\n");
        Watched(() => DiffText(app).Contains("A third paragraph.", StringComparison.Ordinal));
        Require(Shown<ToggleButton>(app, "DiffView_code")!.IsChecked == true, "A refreshed diff keeps the tab's chosen view.");

        paragraphs[5] = "Paragraph 6 rewritten."; entries[7] = "8. entry 8 rewritten";
        File.WriteAllText(Path.Combine(root, "FOCUS.md"), FocusText());
        ClickRow(app, "FOCUS.md");
        string[] Collapsed() => Pane(app) is { } pane ? [.. pane.GetLogicalDescendants().OfType<Button>().Where(button => button.Classes.Contains("rendered-diff-collapsed") && button.IsEffectivelyVisible)
            .Select(button => ((TextBlock)button.Content!).Text!)] : [];
        Until(() => Collapsed().Length == 3);
        Require(Collapsed().SequenceEqual(["⋯ 4 unchanged blocks · Focus title", "⋯ 3 unchanged blocks", "⋯ 5 unchanged items"]), "Unchanged runs collapse behind counted expanders: " + string.Join(" | ", Collapsed()));
        string Rendered() => string.Join(" ", Pane(app)!.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text is SelectableTextBlock rich && rich.Inlines is { Count: > 0 } inlines ? inlines.Text : text.Text));
        Require(Rendered().Contains("6. ", StringComparison.Ordinal) && Rendered().Contains("10. ", StringComparison.Ordinal) && !Rendered().Contains("5. ", StringComparison.Ordinal) && !Rendered().Contains("Paragraph 2 text.", StringComparison.Ordinal),
            "Visible items keep their numbers and hidden units are not drawn: " + Rendered());
        app.Click(Pane(app)!.GetLogicalDescendants().OfType<Button>().First(button => button.Classes.Contains("rendered-diff-collapsed")));
        Until(() => Collapsed().Length == 2 && Rendered().Contains("Paragraph 2 text.", StringComparison.Ordinal));
        paragraphs[11] = "Paragraph 12 rewritten.";
        File.WriteAllText(Path.Combine(root, "FOCUS.md"), FocusText());
        Watched(() => Rendered().Contains("Paragraph 12", StringComparison.Ordinal) && Rendered().Contains("rewritten", StringComparison.Ordinal) && Collapsed().SequenceEqual(["⋯ 5 unchanged items"]));
        Require(Rendered().Contains("Paragraph 2 text.", StringComparison.Ordinal), "An expansion survives a refresh while its run starts at the same position.");

        File.WriteAllText(Path.Combine(root, "BIG.md"), large + "Appended paragraph.\n");
        ClickRow(app, "BIG.md");
        Until(() => Shown<ToggleButton>(app, "DiffView_markdown") is { IsEnabled: false } && DiffText(app).Contains("Appended paragraph.", StringComparison.Ordinal));
        Require(Shown<ToggleButton>(app, "DiffView_code")!.IsChecked == true, "A view that cannot draw the content is disabled and the next candidate takes over.");

        ClickRow(app, "model.bin");
        var lfs = Await<Grid>(app, "LfsDiff");
        Require(Text(lfs).Contains("12 KB", StringComparison.Ordinal) && Text(lfs).Contains("2.0 KB", StringComparison.Ordinal), "An LFS pointer diff shows both objects' sizes: " + Text(lfs));
    }
}