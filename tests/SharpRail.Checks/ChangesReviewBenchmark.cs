using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI;
using SharpRail.UI.Rendering;

namespace SharpRail.Checks;

/// <summary>
/// `--rendering-bench changes-review`: a stacked Changes review of 200 files whose first file is a 20,000-line diff
/// (1,500 changed lines, the rest context), in an 1000×700 headless Skia window. It reports open-to-first-frame with
/// that file's source drawn, 120 scroll steps of 300 px (median/p95) and allocations of both phases.
/// </summary>
internal static class ChangesReviewBenchmark
{
    internal static void Run()
    {
        var big = new StringBuilder("@@ -1,20000 +1,20000 @@\n");
        for (var index = 0; index < 20000; index++)
        {
            if (index % 25 == 0 && index / 25 < 750) big.Append("-old line ").Append(index).Append('\n').Append("+new line ").Append(index).Append('\n');
            else big.Append(" context line ").Append(index).Append(" with some ordinary source text").Append('\n');
        }
        var largeDiff = big.ToString();
        string Small(string path) => "@@ -1,7 +1,7 @@\n a\n b\n c\n-before " + path + "\n+after " + path + "\n d\n e\n f\n";
        var files = Enumerable.Range(0, 200).Select(index => new GitChange(index == 0 ? "large.txt" : $"file-{index:D3}.txt", "M", " ", null, 1, 1)).ToArray();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var openAllocated = GC.GetTotalAllocatedBytes(true);
        var open = Stopwatch.StartNew();
        var pane = new ChangesReviewPane("Changes", new ChangesReviewState(), true,
            _ => Task.FromResult(new GitSnapshot(true, "main", files, [], [])),
            (file, natural, triage, _) => Task.FromResult(new DiffView(file.Path == "large.txt" ? largeDiff : Small(file.Path), file.Path, 800,
                revert: _ => Task.CompletedTask, triage: triage, naturalHeight: natural, embedded: true)),
            _ => { }, () => { }, () => { }, () => true, () => false, _ => { });
        var window = new Window { Width = 1000, Height = 700, Content = pane, Background = Ui.Surface };
        window.Show();
        try
        {
            bool Drawn() => pane.DiffFor("large.txt")?.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Any(editor => editor.Bounds.Height > 0) == true;
            while (!Drawn())
            {
                Frame(window);
                if (open.Elapsed > TimeSpan.FromMinutes(2)) throw new InvalidOperationException("The review never drew the large diff.");
            }
            Frame(window);
            var openMs = open.Elapsed.TotalMilliseconds;
            openAllocated = GC.GetTotalAllocatedBytes(true) - openAllocated;
            var scroll = pane.GetLogicalDescendants().OfType<ScrollViewer>().Single(view => view.Name == "ReviewScroll");
            var frames = new List<double>();
            var allocated = GC.GetTotalAllocatedBytes(true);
            for (var step = 0; step < 120; step++)
            {
                var timer = Stopwatch.StartNew();
                scroll.Offset = new Vector(0, scroll.Offset.Y + 300);
                Frame(window);
                frames.Add(timer.Elapsed.TotalMilliseconds);
            }
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            if (scroll.Offset.Y <= 0) throw new InvalidOperationException("The review did not scroll.");
            frames.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                surface = "changes-review",
                files = files.Length,
                largeLines = 20000,
                openToFirstFrameMs = openMs,
                openAllocatedBytes = openAllocated,
                scrollMedianMs = frames[60],
                scrollP95Ms = frames[114],
                scrollAllocatedBytes = allocated,
                finalOffset = scroll.Offset.Y,
                editors = pane.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Count()
            }));
        }
        finally { window.Close(); pane.Dispose(); }
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
    }
}