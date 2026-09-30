using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class MarkdownMermaidE2E
{
    private static string TextOf(SelectableTextBlock block) => string.Concat(block.Inlines?.OfType<Run>().Select(run => run.Text) ?? []);

    internal static void Run(string root)
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP macOS Mermaid rendering"); return; }
        using var app = new E2eWorkspace(Path.Combine(root, "markdown-mermaid"));
        app.Open("DIAGRAM.md", true);
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Until(() => preview.GetLogicalDescendants().OfType<Image>().Any(image => image.Name == "MermaidDiagram" && image.Bounds.Width > 0));
        var diagrams = preview.GetLogicalDescendants().OfType<Image>().Where(image => image.Name == "MermaidDiagram").ToArray();
        Require(diagrams.Length == 1 && diagrams[0].Source is { Size.Width: > 0 } && diagrams[0].IsEffectivelyVisible,
            "Exactly one valid Mermaid fence must render as a visible diagram.");
        Settle(300);
        using var frame = app.Window.CaptureRenderedFrame()!;
        byte[] Pixel(Point point)
        {
            var at = diagrams[0].TranslatePoint(point, app.Window)!.Value * app.Window.RenderScaling;
            var pixel = new byte[4];
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { frame.CopyPixels(new PixelRect((int)at.X, (int)at.Y, 1, 1), pinned.AddrOfPinnedObject(), 4, 4); }
            finally { pinned.Free(); }
            return pixel;
        }
        var corner = Pixel(new Point(1, 1));
        var surface = Pixel(new Point(-4, 1));
        Require(corner.SequenceEqual(surface), "The diagram must leave its background transparent so it sits on the preview surface.");
        Until(() => preview.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "MermaidError"));
        var errors = preview.GetLogicalDescendants().OfType<Control>().Where(control => control.Name == "MermaidError").ToArray();
        Require(errors.Length == 1, "Exactly one invalid Mermaid fence must render an error.");
        var errorText = string.Join('\n', errors[0].GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text)
            .Concat(errors[0].GetLogicalDescendants().OfType<SelectableTextBlock>().Select(TextOf)));
        Require(errorText.Contains("broken", StringComparison.Ordinal), "The diagram error must show the failing source.");
        Require(preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(text => TextOf(text).Contains("plain-fence-stays-code", StringComparison.Ordinal)),
            "A non-Mermaid fence must stay a code block.");

        app.Click(preview.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "MermaidFullscreen"));
        static bool IsFullscreen(DialogWindow window) => window.GetLogicalDescendants().OfType<ScrollViewer>().Any(viewer => viewer.Name == "MermaidFullscreenViewer");
        Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any(IsFullscreen));
        var dialog = app.Window.OwnedWindows.OfType<DialogWindow>().Single(IsFullscreen);
        Until(() => dialog.GetLogicalDescendants().OfType<Image>().Any(image => image.Source is not null && image.Bounds.Width > 0));
        var detail = dialog.GetLogicalDescendants().OfType<Image>().Single(image => image.Source is not null);
        Control Part(string name) => dialog.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == name);
        var fitted = detail.Bounds.Width;
        Require(((TextBlock)Part("MermaidZoomLevel")).Text == "100%", "The full-screen diagram must open fitted to the viewer width.");
        app.Click((Button)Part("MermaidZoomIn"));
        Until(() => ((TextBlock)Part("MermaidZoomLevel")).Text == "125%" && detail.Bounds.Width > fitted * 1.2);
        app.Click((Button)Part("MermaidZoomReset"));
        Until(() => ((TextBlock)Part("MermaidZoomLevel")).Text == "100%" && Math.Abs(detail.Bounds.Width - fitted) < 1);
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !app.Window.OwnedWindows.OfType<DialogWindow>().Any());

        app.Click(app.Find<Button>("MarkdownSourceMode"));
        var source = app.Find<ScrollViewer>("MarkdownSource");
        Require(source.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(text => TextOf(text).Contains("flowchart TD; Start --> Finish", StringComparison.Ordinal)),
            "Source mode must show the Mermaid source.");
        Console.WriteLine("PASS upstream markdown-mermaid.spec.ts: renders mermaid fences as diagrams in the rendered markdown view");
    }
}
