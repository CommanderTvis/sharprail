using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia;
using Avalonia.Threading;

using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Browser.Checks;

internal static partial class BrowserChecks
{
    private static MarkdownPreview? prosePreview;
    private static object? prosePreviousContent;

    [JSExport]
    public static async Task<string> MeasureProse(int paragraphs)
    {
        var text = "# Plain prose benchmark\n\n" + string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(index => "Paragraph " + index + ". " + string.Join(" ", Enumerable.Repeat("A readable paragraph explains the behavior of a workspace, its files, and the controls used to navigate between documents.", 5))));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var document = await Task.Run(() => MarkdownPreview.Parse(text));
        var parse = clock.Elapsed.TotalMilliseconds;
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RestoreProseCore();
            clock.Restart();
            var context = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 100, true);
            prosePreview = new MarkdownPreview(document, "prose.md", context);
            var construct = clock.Elapsed.TotalMilliseconds;
            var window = Workbench.ActiveWindow!;
            prosePreviousContent = window.Content;
            clock.Restart();
            window.Content = prosePreview;
            window.UpdateLayout();
            return JsonSerializer.Serialize(new
            {
                paragraphs,
                Characters = text.Length,
                ParseMs = parse,
                ConstructMs = construct,
                AttachMeasureMs = clock.Elapsed.TotalMilliseconds,
                DynamicCode = RuntimeFeature.IsDynamicCodeSupported,
                JsonReflection = JsonSerializer.IsReflectionEnabledByDefault
            });
        });
    }

    [JSExport]
    public static async Task<string> ProseState()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var preview = prosePreview!;
            var body = (MarkdownBlockList)preview.Content!;
            var sizes = (System.Collections.IDictionary)typeof(MarkdownBlockList).GetField("sizes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(body)!;
            return JsonSerializer.Serialize(new
            {
                MeasuredBlocks = sizes.Count,
                MountedBlocks = body.Children.Count,
                Extent = preview.Extent.Height,
                Viewport = preview.Viewport.Height,
                Offset = preview.Offset.Y
            });
        });
    }

    [JSExport]
    public static async Task ScrollProse(double fraction)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var preview = prosePreview!;
            preview.Offset = new Vector(0, (preview.Extent.Height - preview.Viewport.Height) * fraction);
            Workbench.ActiveWindow!.UpdateLayout();
        });
    }

    [JSExport]
    public static async Task RestoreProse()
    {
        await Dispatcher.UIThread.InvokeAsync(RestoreProseCore);
    }

    private static void RestoreProseCore()
    {
        if (prosePreview is null) return;
        Workbench.ActiveWindow!.Content = prosePreviousContent;
        prosePreview.Dispose();
        prosePreview = null;
        prosePreviousContent = null;
    }
}
