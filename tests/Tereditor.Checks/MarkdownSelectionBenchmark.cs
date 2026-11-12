using System.Diagnostics;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Checks;

internal static class MarkdownSelectionBenchmark
{
    internal static void Run()
    {
        var fixture = Environment.GetEnvironmentVariable("SHARPRAIL_BENCH_MARKDOWN");
        var source = fixture is not null ? File.ReadAllText(fixture) : string.Join("\n\n", Enumerable.Range(0, 1900)
            .Select(index => $"Paragraph {index}: " + string.Concat(Enumerable.Repeat("Some **formatted text** to select while dragging through a large Markdown preview. ", 123))));
        var context = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 78, true);
        using var preview = new MarkdownPreview(source, "large.md", context);
        var window = new Window { Width = 800, Height = 600, Content = preview, Background = Ui.Surface };
        var notifications = 0;
        preview.SelectionChanged += text => { _ = preview.SelectedLines; notifications++; };
        window.Show();
        try
        {
            Frame(window);
            var blocks = preview.TextBlocks().ToArray();
            preview.RevealBlock(blocks[blocks.Length / 2]);
            Frame(window);
            var target = preview.GetVisualDescendants().OfType<SelectableTextBlock>()
                .First(block => block.TranslatePoint(default, preview) is { Y: >= 0 and < 500 });
            var start = target.TranslatePoint(new Point(2, 7), window)!.Value;
            var end = target.TranslatePoint(new Point(250, 40), window)!.Value;
            var pressAllocated = GC.GetTotalAllocatedBytes();
            var press = Stopwatch.StartNew();
            window.MouseDown(start, MouseButton.Left);
            var pressMs = press.Elapsed.TotalMilliseconds;
            pressAllocated = GC.GetTotalAllocatedBytes() - pressAllocated;
            var frames = new List<double>();
            var inputs = new List<double>();
            var allocated = GC.GetTotalAllocatedBytes();
            for (var step = 0; step < 120; step++)
            {
                var timer = Stopwatch.StartNew();
                window.MouseMove(new Point(end.X + step % 30, end.Y + step % 2 * 20), RawInputModifiers.LeftMouseButton);
                inputs.Add(timer.Elapsed.TotalMilliseconds);
                Frame(window);
                frames.Add(timer.Elapsed.TotalMilliseconds);
            }
            window.MouseUp(end, MouseButton.Left);
            if (preview.SelectedText.Length == 0 || notifications == 0) throw new InvalidOperationException("The benchmark did not select text.");
            var allocatedBytes = GC.GetTotalAllocatedBytes() - allocated;
            frames.Sort(); inputs.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                surface = "markdown-selection",
                characters = source.Length,
                blocks = blocks.Length,
                mountedBlocks = preview.GetVisualDescendants().OfType<SelectableTextBlock>().Count(),
                notifications,
                pressMs,
                pressAllocatedBytes = pressAllocated,
                selectedCharacters = preview.SelectedText.Length,
                inputMedianMs = inputs[60],
                inputP95Ms = inputs[114],
                frameMedianMs = frames[60],
                frameP95Ms = frames[114],
                allocatedBytes
            }));
        }
        finally { window.Close(); window.Content = null; }
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
    }
}