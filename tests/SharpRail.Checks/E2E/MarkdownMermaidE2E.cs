using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;


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

        // Inline, a tall diagram stops at the cap; zoom scales the drawing inside the clipped box, never the box.
        Control Inline(string name) => preview.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == name);
        var box = Inline("MermaidInline");
        var drawn = diagrams[0].Bounds.Size;
        Require(box.Bounds.Height <= 480 && drawn.Height > box.Bounds.Height && box.ClipToBounds,
            "A tall inline diagram must stop at the height cap and clip the rest.");
        for (var i = 0; i < 6; i++) app.Click((Button)Inline("MermaidZoomIn"));
        Until(() => ((TextBlock)Inline("MermaidZoomLevel")).Text != "100%" && diagrams[0].Bounds.Width > drawn.Width * 1.5);
        Require(box.Bounds.Height <= 480, "Zooming must enlarge the drawing, not the box.");
        var before = Canvas.GetTop(diagrams[0]);
        var centre = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseDown(centre, MouseButton.Left);
        app.Window.MouseMove(centre - new Point(80, 40));
        app.Window.MouseUp(centre - new Point(80, 40), MouseButton.Left);
        Until(() => Canvas.GetTop(diagrams[0]) < before);
        app.Click((Button)Inline("MermaidZoomReset"));
        Until(() => ((TextBlock)Inline("MermaidZoomLevel")).Text == "100%" && Math.Abs(diagrams[0].Bounds.Width - drawn.Width) < 1);

        app.Click(preview.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "MermaidFullscreen"));
        static bool IsFullscreen(DialogWindow window) => window.GetLogicalDescendants().OfType<ScrollViewer>().Any(viewer => viewer.Name == "MermaidPanZoom");
        Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any(IsFullscreen));
        var dialog = app.Window.OwnedWindows.OfType<DialogWindow>().Single(IsFullscreen);
        Until(() => dialog.GetLogicalDescendants().OfType<Image>().Any(image => image.Source is not null && image.Bounds.Width > 0));
        var detail = dialog.GetLogicalDescendants().OfType<Image>().Single(image => image.Source is not null);
        Control Part(string name) => dialog.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == name);
        var fitted = detail.Bounds.Width;
        Require(((TextBlock)Part("MermaidZoomLevel")).Text == "100%", "The full-screen diagram must open fitted to the viewer width.");
        app.Click((Button)Part("MermaidZoomIn"));
        Until(() => ((TextBlock)Part("MermaidZoomLevel")).Text == "115%" && detail.Bounds.Width > fitted * 1.1);
        app.Click((Button)Part("MermaidZoomReset"));
        Until(() => ((TextBlock)Part("MermaidZoomLevel")).Text == "100%" && Math.Abs(detail.Bounds.Width - fitted) < 1);

        var viewer = (ScrollViewer)Part("MermaidPanZoom");
        string Level() => ((TextBlock)Part("MermaidZoomLevel")).Text!;
        bool Pinch(double magnification)
        {
            var pinch = new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent, viewer, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true),
                dialog, new Point(40, 40), 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, new Vector(magnification, magnification));
            viewer.RaiseEvent(pinch);
            return pinch.Handled;
        }
        Require(Pinch(0.5), "The viewer must claim a trackpad pinch.");
        Until(() => Level() == "150%" && detail.Bounds.Width > fitted * 1.45);
        for (var step = 0; step < 8; step++) Pinch(0.5);
        Until(() => Level() == "600%");
        for (var step = 0; step < 8; step++) Pinch(-0.5);
        Until(() => Level() == "25%");
        app.Click((Button)Part("MermaidZoomReset"));
        Until(() => Level() == "100%");
        Console.WriteLine("PASS Mermaid viewer pinch zooms about the fitted width and stays within 25-600%");

        app.Click((Button)Part("MermaidZoomIn"));
        app.Click((Button)Part("MermaidZoomIn"));
        app.Click((Button)Part("MermaidZoomIn"));
        Until(() => viewer.Extent.Width > viewer.Viewport.Width + 100);
        var grab = viewer.TranslatePoint(new Point(viewer.Bounds.Width / 2, viewer.Bounds.Height / 2), dialog)!.Value;
        var tall = viewer.Extent.Height > viewer.Viewport.Height + 100;
        dialog.MouseMove(grab);
        Require(viewer.Offset == default, "Moving without a pressed button must not pan the diagram.");
        dialog.MouseDown(grab, MouseButton.Left);
        dialog.MouseMove(grab + new Vector(-60, -40));
        Until(() => Math.Abs(viewer.Offset.X - 60) < 1 && (!tall || Math.Abs(viewer.Offset.Y - 40) < 1));
        dialog.MouseMove(grab + new Vector(-90, -40));
        Until(() => Math.Abs(viewer.Offset.X - 90) < 1);
        dialog.MouseMove(grab + new Vector(400, 400));
        Until(() => viewer.Offset == default);
        dialog.MouseMove(grab + new Vector(370, 400));
        Until(() => Math.Abs(viewer.Offset.X - 30) < 1);
        dialog.MouseUp(grab + new Vector(370, 400), MouseButton.Left);
        dialog.MouseMove(grab);
        Settle(100);
        Require(Math.Abs(viewer.Offset.X - 30) < 1, "Releasing the button must end the pan.");
        dialog.MouseDown(grab, MouseButton.Right);
        dialog.MouseMove(grab + new Vector(-50, 0));
        dialog.MouseUp(grab + new Vector(-50, 0), MouseButton.Right);
        Settle(100);
        Require(Math.Abs(viewer.Offset.X - 30) < 1, "Only the primary button pans.");
        app.Click((Button)Part("MermaidZoomReset"));
        Until(() => Level() == "100%" && viewer.Offset == default);
        Console.WriteLine("PASS Mermaid viewer drag pans the zoomed diagram, clamps at its edges and resets");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !app.Window.OwnedWindows.OfType<DialogWindow>().Any());

        app.Click(app.Find<Button>("MarkdownSourceMode"));
        var source = app.Find<Control>("MarkdownSource");
        Require(MarkdownSourceText(source).Contains("flowchart TD; Start --> Finish", StringComparison.Ordinal),
            "Source mode must show the Mermaid source.");
        Console.WriteLine("PASS upstream markdown-mermaid.spec.ts: renders mermaid fences as diagrams in the rendered markdown view (inline diagram capped, zoomed and panned in its box)");
    }
}