using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.BranchGraph;
using SharpRail.Plugins.BranchGraph.UI;
using SharpRail.Plugins.SpecDialect;
using SharpRail.Plugins.UI.Kit.Markdown;
using SharpRail.UI;

namespace SharpRail.Checks;

internal static class RenderingBenchmarks
{
    internal static void Run(string surface, string root)
    {
        if (surface == "markdown-selection") { MarkdownSelectionBenchmark.Run(); return; }
        Directory.CreateDirectory(Path.Combine(".bench", "rendering"));
        const int count = 2000;
        var context = DispatchProxy.Create<IPluginUIContext, RenderingBenchmarkContext>();
        using var app = surface is "files" or "files-mixed" ? new E2eWorkspace(root, profileRoot: Path.Combine(root, "profile")) : null;
        var source = string.Join("\n\n", Enumerable.Range(0, count).Select(index =>
            $"Paragraph {index}: rendering **formatted text** with a [relative link](notes.txt) and enough words to wrap across the reading column."));
        var markdownContext = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 78, true);
        var document = MarkdownPreview.Parse(surface == "markdown-large" ? LargeMarkdown() : source);
        var merged = MarkdownDiff.Merge(source, source.Replace("formatted text", "updated formatted text", StringComparison.Ordinal));
        var diff = MarkdownPreview.Parse(merged);
        var focus = DiffFocus.Create(source, merged, diff, new HashSet<string>());
        var store = new SpecStore();
        store.SetSpecs("bench", Enumerable.Range(0, count).Select(index => new SpecGraphNode(index.ToString(), "module-design",
            $"Module {index} — rendering behavior", $"module-{index}.md", [], [], [], [])).ToArray());
        var sync = new SpecSync(_ => ValueTask.FromResult(new SpecGraphSnapshot(store.Specs("bench")!)), store);
        string[] extensions = ["cs", "ts", "md", "json", "py"];
        var files = Enumerable.Range(0, count).Select(index =>
        {
            var path = $"file-{index:D4}.{(surface == "files-mixed" ? extensions[index % extensions.Length] : "cs")}";
            return new ProjectFile(path, path, false, surface == "files-mixed" && index % 7 == 0);
        }).ToArray();
        Control CreateFiles()
        {
            var cache = (Dictionary<string, IReadOnlyList<ProjectFile>>)typeof(WorkbenchWindow).GetField("folderCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app!.Window)!;
            cache[""] = files;
            return (Control)typeof(WorkbenchWindow).GetMethod("FilesPanel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app.Window, null)!;
        }
        Control Create() => surface switch
        {
            "markdown" or "markdown-large" => new MarkdownPreview(document, "README.md", markdownContext),
            "markdown-diff" => new MarkdownPreview(diff, "README.md", markdownContext, renderDiagrams: false, focus: focus),
            "graph" => new GraphPanel(context, "bench"),
            "specs" => (Control)Activator.CreateInstance(typeof(SpecSync).Assembly.GetType("SharpRail.Plugins.SpecDialect.SpecsPanel")!, context, sync, "bench")!,
            "files" or "files-mixed" => CreateFiles(),
            _ => throw new ArgumentException("Expected markdown, markdown-large, markdown-diff, graph, files, files-mixed or specs.", nameof(surface))
        };
        using (var warm = new MarkdownPreview("# Warmup\n\nWarm **text**", "warm.md", markdownContext))
        {
            var window = new Window { Width = 800, Height = 600, Content = warm };
            window.Show(); Frame(window); window.Close(); window.Content = null;
        }
        Collect();
        var before = GC.GetTotalMemory(true);
        var allocated = GC.GetTotalAllocatedBytes();
        var timer = Stopwatch.StartNew();
        var view = Create();
        var constructMs = timer.Elapsed.TotalMilliseconds;
        var constructAllocated = GC.GetTotalAllocatedBytes() - allocated;
        var host = new Window { Width = surface.StartsWith("markdown", StringComparison.Ordinal) ? 800 : 360, Height = 600, Content = view, Background = Ui.Surface };
        host.Show(); Frame(host);
        var mountMs = timer.Elapsed.TotalMilliseconds;
        var mountAllocated = GC.GetTotalAllocatedBytes() - allocated;
        Collect();
        var retained = GC.GetTotalMemory(true) - before;
        var rss = Process.GetCurrentProcess().WorkingSet64;
        var visuals = view.GetVisualDescendants().Count();
        if (visuals < 10) throw new InvalidOperationException("The benchmark surface did not realize its content.");
        var logical = view.GetLogicalDescendants().Count();
        var scroll = view as ScrollViewer ?? view.GetVisualDescendants().OfType<ScrollViewer>().First();
        var scrollRange = scroll.Extent.Height - scroll.Viewport.Height;
        var frames = new List<double>();
        allocated = GC.GetTotalAllocatedBytes();
        for (var index = 0; index < 120; index++)
        {
            timer.Restart();
            // The large document jumps through its whole extent so that every kind of chunk is realized.
            var target = surface == "markdown-large" ? (scroll.Extent.Height - scroll.Viewport.Height) * (index + 1) / 120 : (index + 1) * 96;
            scroll.Offset = new Vector(0, Math.Min(target, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
            Frame(host);
            frames.Add(timer.Elapsed.TotalMilliseconds);
        }
        var scrollAllocated = GC.GetTotalAllocatedBytes() - allocated;
        if (scroll.Offset.Y <= 0) throw new InvalidOperationException($"The benchmark did not scroll: extent={scroll.Extent}, viewport={scroll.Viewport}.");
        using (var frame = host.CaptureRenderedFrame())
            (frame ?? throw new InvalidOperationException("The benchmark did not render a frame.")).Save(Path.Combine(".bench", "rendering", surface + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        frames.Sort();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            surface,
            count,
            constructMs,
            constructAllocated,
            mountMs,
            mountAllocated,
            retainedManagedBytes = retained,
            processResidentBytes = rss,
            visuals,
            logical,
            scrollRange,
            scrollOffset = scroll.Offset.Y,
            scrollFrameMedianMs = frames[frames.Count / 2],
            scrollFrameP95Ms = frames[(int)(frames.Count * .95)],
            scrollAllocatedBytes = scrollAllocated
        }));
        host.Close(); host.Content = null;
        (view as IDisposable)?.Dispose();
    }

    // 2,000 prose blocks around a 5,000-row table and a 20,000-line code block.
    private static string LargeMarkdown()
    {
        var text = new System.Text.StringBuilder();
        for (var index = 0; index < 1000; index++) text.Append($"Paragraph {index}: rendering **formatted text** with a [relative link](notes.txt) and enough words to wrap.\n\n");
        text.Append("| Name | Value | Notes |\n| --- | --- | --- |\n");
        for (var row = 0; row < 5000; row++) text.Append($"| Row {row} | {row * 7} | cell text for row {row} |\n");
        text.Append("\n```cs\n");
        for (var line = 0; line < 20000; line++) text.Append($"var value{line} = Compute({line}); // line {line}\n");
        text.Append("```\n\n");
        for (var index = 1000; index < 2000; index++) text.Append($"Paragraph {index}: rendering **formatted text** with a [relative link](notes.txt) and enough words to wrap.\n\n");
        return text.ToString();
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
    }

    private static void Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }
}

public class RenderingBenchmarkContext : DispatchProxy
{
    private readonly PluginHostProjection projection = new([], new Dictionary<string, IReadOnlyList<SharpRail.Plugins.Api.HostWorkspace>>
    {
        ["bench"] = [new("bench", "bench", "Bench", "main", "bench", true)]
    }, "bench", "bench", null, new("", false, 120, true, 78, true, []), new Dictionary<string, IReadOnlyList<TerminalTabInfo>>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, int>(), [], null);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
    {
        "Host" => projection,
        "WatchHost" => new Subscription(),
        "RequestAsync" => ValueTask.FromResult(new GitGraph(Enumerable.Range(0, 2000).Select(index => new GitGraphCommit(
            index.ToString("x40"), index.ToString("x7"), index < 1999 ? [(index + 1).ToString("x40")] : [],
            index % 20 == 0 ? ["feature/rendering"] : [], $"Commit {index}: improve rendering behavior", "Example Author", "2026-10-11")).ToArray(), [], false)),
        _ => throw new NotSupportedException(targetMethod.Name)
    };

    private sealed class Subscription : IDisposable { public void Dispose() { } }
}