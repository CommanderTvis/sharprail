using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using SharpRail.Scintilla;

namespace SharpRail.Checks;

/// <summary>
/// `--rendering-bench editor`: a 100,000-line document (mixed ASCII, accented and emoji text) in a 1000×700 headless
/// Skia window, unwrapped and wrapped. It reports open-to-first-frame (background work left queued), open until
/// no background work is due, until the content height settles (all lines wrapped), 200 typed characters in the middle of the
/// document with a frame after each (median/p95) and 120 scroll steps of 300 px (median/p95), with allocations.
/// </summary>
internal static class EditorBenchmark
{
    internal static void Run()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 100000; index++)
        {
            builder.Append(index % 10 == 7 ? "    var café = \"naïve 😀\"; // line " : "    int value = Compute(alpha, beta, gamma); // line ").Append(index);
            if (index % 50 == 0) builder.Append(" and a longer tail of ordinary source text that wraps across a narrower reading column");
            builder.Append('\n');
        }
        var text = builder.ToString();
        using (var warm = new ScintillaEditor("warm up\nsecond line", EditorChecks.Font))
        {
            var window = new Window { Width = 400, Height = 200, Content = warm };
            window.Show(); Frame(window); window.KeyTextInput("x"); Frame(window); window.Close();
        }
        var results = new List<object>();
        foreach (var wrap in new[] { false, true }) results.Add(Measure(text, wrap));
        Console.WriteLine(JsonSerializer.Serialize(results));
    }

    private static object Measure(string text, bool wrap)
    {
        Collect();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var editor = new ScintillaEditor(text, EditorChecks.Font);
        if (wrap) editor.WrapWidth = 800;
        var window = new Window { Width = 1000, Height = 700, Content = editor };
        window.Show();
        // The first frame runs input-and-above work only, leaving background wrapping queued, as a busy app would.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var openMs = timer.Elapsed.TotalMilliseconds;
        Frame(window);
        var settledMs = timer.Elapsed.TotalMilliseconds;
        var openAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
        // Background wrapping runs in dispatcher slices; it is complete once the content height holds for 100 ms.
        var height = editor.ContentHeight;
        var stableSince = timer.Elapsed;
        while (timer.Elapsed - stableSince < TimeSpan.FromMilliseconds(100))
        {
            Thread.Sleep(2);
            Frame(window);
            if (editor.ContentHeight != height) { height = editor.ContentHeight; stableSince = timer.Elapsed; }
            if (timer.Elapsed > TimeSpan.FromMinutes(1)) throw new InvalidOperationException("Wrapping never settled.");
        }
        var wrappedMs = (stableSince - TimeSpan.Zero).TotalMilliseconds;
        try
        {
            editor.Focus();
            editor.SelectRange(text.Length / 2, 0);
            Frame(window);
            var typing = Sample(200, () => window.KeyTextInput("x"), window, out var typingAllocated);
            if (editor.Text.Length != text.Length + 200) throw new InvalidOperationException("Typing did not reach the document.");
            editor.ScrollToPixel(0);
            Frame(window);
            var offset = 0.0;
            var scroll = Sample(120, () => editor.ScrollToPixel(offset += 300), window, out var scrollAllocated);
            return new
            {
                wrap,
                lines = 100000,
                bytes = Encoding.UTF8.GetByteCount(text),
                openToFirstFrameMs = openMs,
                openToSettledMs = settledMs,
                openToWrappedMs = wrappedMs,
                contentHeight = height,
                openAllocatedBytes = openAllocated,
                typeMedianMs = typing[typing.Count / 2],
                typeP95Ms = typing[typing.Count * 95 / 100],
                typeAllocatedBytes = typingAllocated,
                scrollMedianMs = scroll[scroll.Count / 2],
                scrollP95Ms = scroll[scroll.Count * 95 / 100],
                scrollAllocatedBytes = scrollAllocated,
            };
        }
        finally { window.Close(); editor.Dispose(); }
    }

    private static List<double> Sample(int count, Action step, Window window, out long allocated)
    {
        var frames = new List<double>(count);
        allocated = GC.GetTotalAllocatedBytes(true);
        for (var index = 0; index < count; index++)
        {
            var timer = Stopwatch.StartNew();
            step();
            Frame(window);
            frames.Add(timer.Elapsed.TotalMilliseconds);
        }
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        frames.Sort();
        return frames;
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
    }
}