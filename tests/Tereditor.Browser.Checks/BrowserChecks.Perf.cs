using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using SharpRail.Scintilla;
using SharpRail.UI.Rendering;

namespace SharpRail.Browser.Checks;

// `restty.mjs --perf`: the editor and diff workloads of the desktop editor benchmark, run inside the browser.
internal static partial class BrowserChecks
{
    private static Control? perfControl;
    private static object? perfPreviousContent;

    [JSExport]
    public static async Task<string> OpenEditorBench(int lines)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < lines; index++)
        {
            builder.Append(index % 10 == 7 ? "    var café = \"naïve 😀\"; // line " : "    int value = Compute(alpha, beta, gamma); // line ").Append(index);
            if (index % 50 == 0) builder.Append(" and a longer tail of ordinary source text that wraps across a narrower reading column");
            builder.Append('\n');
        }
        var text = builder.ToString();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RestorePerfCore();
            var clock = Stopwatch.StartNew();
            var editor = new ScintillaEditor(text);
            var construct = clock.Elapsed.TotalMilliseconds;
            Attach(editor);
            var attach = clock.Elapsed.TotalMilliseconds - construct;
            editor.Focus();
            editor.SelectRange(text.Length / 2, 0);
            return JsonSerializer.Serialize(new { Lines = lines, Bytes = Encoding.UTF8.GetByteCount(text), ConstructMs = construct, AttachMs = attach });
        });
    }

    [JSExport]
    public static async Task<int> EditorBenchLength() =>
        await Dispatcher.UIThread.InvokeAsync(() => ((ScintillaEditor)perfControl!).Text.Length);

    [JSExport]
    public static async Task ScrollEditorBench(double offset)
    {
        await Dispatcher.UIThread.InvokeAsync(() => ((ScintillaEditor)perfControl!).ScrollToPixel(offset));
    }

    /// <summary>
    /// Types <paramref name="count"/> characters (or scrolls 300 px per step) and renders the editor synchronously
    /// after each, timing managed and Skia work without the browser's frame pacing; returns the median and p95.
    /// </summary>
    [JSExport]
    public static async Task<string> EditorCpuBench(int count, bool scroll)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var editor = (ScintillaEditor)perfControl!;
            var size = new PixelSize(Math.Max(1, (int)editor.Bounds.Width), Math.Max(1, (int)editor.Bounds.Height));
            using var target = new RenderTargetBitmap(size);
            var samples = new double[count];
            for (var index = 0; index < count; index++)
            {
                var clock = Stopwatch.StartNew();
                if (scroll) editor.ScrollToPixel((index + 1) * 300);
                else editor.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "y" });
                target.Render(editor);
                samples[index] = clock.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples);
            return JsonSerializer.Serialize(new { median = samples[count / 2], p95 = samples[count * 95 / 100] });
        });
    }

    [JSExport]
    public static async Task<string> OpenDiffBench(int hunks)
    {
        var builder = new StringBuilder("diff --git a/big.txt b/big.txt\n--- a/big.txt\n+++ b/big.txt\n");
        for (var hunk = 0; hunk < hunks; hunk++)
        {
            var start = 1 + hunk * 40;
            builder.Append($"@@ -{start},10 +{start},10 @@\n");
            for (var line = 0; line < 6; line++) builder.Append(" context line ").Append(start + line).Append(" with some ordinary source text\n");
            for (var line = 0; line < 2; line++) builder.Append("-old value = Compute(alpha, beta); // ").Append(start + line).Append('\n');
            for (var line = 0; line < 2; line++) builder.Append("+new value = Compute(alpha, beta, gamma); // ").Append(start + line).Append('\n');
        }
        var text = builder.ToString();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RestorePerfCore();
            var clock = Stopwatch.StartNew();
            var view = new DiffView(text, "big.txt", 800);
            var construct = clock.Elapsed.TotalMilliseconds;
            Attach(view);
            return JsonSerializer.Serialize(new { Hunks = hunks, Characters = text.Length, ConstructMs = construct, AttachMs = clock.Elapsed.TotalMilliseconds - construct });
        });
    }

    [JSExport]
    public static async Task RestorePerf()
    {
        await Dispatcher.UIThread.InvokeAsync(RestorePerfCore);
    }

    private static void Attach(Control control)
    {
        var window = Workbench.ActiveWindow!;
        perfPreviousContent = window.Content;
        perfControl = control;
        window.Content = control;
        window.UpdateLayout();
    }

    private static void RestorePerfCore()
    {
        if (perfControl is null) return;
        Workbench.ActiveWindow!.Content = perfPreviousContent;
        (perfControl as IDisposable)?.Dispose();
        perfControl = null;
        perfPreviousContent = null;
    }
}