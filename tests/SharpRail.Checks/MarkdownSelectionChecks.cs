using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.UI.Rendering;

namespace SharpRail.Checks;

internal static class MarkdownSelectionChecks
{
    internal static void Run()
    {
        VirtualDrag();
        ShapedSelection();
        Console.WriteLine("PASS large Markdown reverse/shrinking/offscreen selections, copy and shaped-text invalidation under every theme");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
    }

    private static void VirtualDrag()
    {
        var source = string.Join("\n\n", Enumerable.Range(0, 2000).Select(index => $"Paragraph {index} with a [link](target.md) and **bold text**."));
        var context = new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 78, true);
        using var preview = new MarkdownPreview(source, "large.md", context);
        (int Start, int End)? reported = null;
        preview.SelectionChanged += _ => reported = preview.SelectedLines;
        var window = new Window { Width = 800, Height = 600, Content = preview, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window);
            var all = preview.TextBlocks().ToArray();
            preview.RevealBlock(all[1000]); Frame(window);
            var mounted = preview.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Where(block => block.TranslatePoint(default, preview) is { Y: >= 0 and < 400 })
                .OrderBy(block => Array.IndexOf(all, block)).ToArray();
            Require(mounted.Length >= 4, "The virtual drag fixture needs four visible paragraphs.");
            Point At(int index) => mounted[index].TranslatePoint(new Point(1, mounted[index].Bounds.Height / 2), window)!.Value;
            window.MouseDown(At(3), MouseButton.Left);
            window.MouseMove(At(0), RawInputModifiers.LeftMouseButton);
            Require(mounted.Take(3).All(block => block.SelectionEnd > block.SelectionStart) && preview.SelectedText.Contains("with a link", StringComparison.Ordinal),
                $"A reverse drag selects intervening paragraphs and their link labels: {preview.SelectedText}; {string.Join(";", mounted.Take(4).Select(block => $"{Array.IndexOf(all, block)}:{block.SelectionStart}-{block.SelectionEnd}"))}.");
            window.MouseMove(At(2), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(2), MouseButton.Left);
            Require(mounted.Take(2).All(block => block.SelectionStart == block.SelectionEnd) &&
                preview.SelectedLines == reported && reported == (Array.IndexOf(all, mounted[2]) * 2 + 1, Array.IndexOf(all, mounted[2]) * 2 + 1),
                "Shrinking a reverse drag clears old highlights and reports only the remaining paragraph's source line.");
            var selected = preview.SelectedText;
            var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.C, command, PhysicalKey.C, null); Dispatcher.UIThread.RunJobs();
            Require(window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult() == selected, "Copy preserves the shrunken document selection.");
            preview.RevealBlock(all[1100]); Frame(window);
            var target = all[1100].TranslatePoint(new Point(1, all[1100].Bounds.Height / 2), window)!.Value;
            window.MouseDown(target, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseUp(target, MouseButton.Left, RawInputModifiers.Shift);
            Require(preview.SelectedLines is { } lines && lines.Start < 2100 && lines.End >= 2100 &&
                preview.SelectedText.Contains("Paragraph 1050", StringComparison.Ordinal), "Shift+click extends through detached paragraphs after scrolling.");
            window.MouseDown(target + new Point(30, 0), MouseButton.Left);
            window.MouseUp(target + new Point(30, 0), MouseButton.Left);
            Require(preview.SelectedText.Length == 0 && preview.SelectedLines is null, "A new click clears offscreen selection too.");
        }
        finally { window.Close(); window.Content = null; }
    }

    private static void ShapedSelection()
    {
        var original = Ui.Theme;
        try
        {
            foreach (var theme in Themes.All)
            {
                Ui.Apply(theme);
                using var preview = new MarkdownPreview("Normal **bold** and *italic* words before a [link](target.md).", "styles.md",
                    new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 78, true));
                var window = new Window { Width = 600, Height = 200, Content = preview, Background = Ui.Surface };
                window.Show();
                try
                {
                    Frame(window);
                    var text = preview.TextBlocks().Single();
                    var geometry = text.TextLayout.HitTestTextRange(0, 6).ToArray();
                    var baseline = Pixels(window);
                    text.SelectionStart = 0; text.SelectionEnd = 6; Frame(window);
                    Require(!Pixels(window).SequenceEqual(baseline) && text.CanCopy &&
                        text.TextLayout.HitTestTextRange(0, 6).SequenceEqual(geometry), $"Selection paints without moving text under {theme.Id}.");
                    text.SelectionEnd = 13; Frame(window);
                    text.ClearSelection(); Frame(window);
                    Require(Pixels(window).SequenceEqual(baseline) && !text.CanCopy, $"Clearing selection restores styled glyphs under {theme.Id}.");
                    var run = text.Inlines!.OfType<Run>().First();
                    run.Text = "Changed content "; Frame(window);
                    Require(!Pixels(window).SequenceEqual(baseline), $"Inline edits invalidate shaped text under {theme.Id}.");
                    var width = text.TextLayout.Width;
                    text.FontSize = 22; Frame(window);
                    Require(text.TextLayout.Width > width, $"Font changes invalidate shaped text under {theme.Id}.");
                    var span = new Span();
                    span.Inlines.Add(new Run(" Nested text."));
                    text.Inlines!.Add(span); Frame(window);
                    var nested = Pixels(window);
                    var added = new Run(" Added text.");
                    span.Inlines.Add(added); Frame(window);
                    Require(!Pixels(window).SequenceEqual(nested), $"Nested inline additions invalidate shaped text under {theme.Id}.");
                    nested = Pixels(window);
                    added.Text = " Edited text."; Frame(window);
                    Require(!Pixels(window).SequenceEqual(nested), $"New nested inline edits invalidate shaped text under {theme.Id}.");
                    var changed = Pixels(window);
                    window.Content = null; Frame(window);
                    window.Content = preview; Frame(window);
                    Require(Pixels(window).SequenceEqual(changed), $"Detached text remounts without stale or missing glyphs under {theme.Id}.");
                }
                finally { window.Close(); window.Content = null; }
            }
        }
        finally { Ui.Apply(original); }
    }

    private static byte[] Pixels(Window window)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No selection frame.");
        var pixels = new byte[frame.PixelSize.Width * frame.PixelSize.Height * 4];
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(frame.PixelSize), pinned.AddrOfPinnedObject(), pixels.Length, frame.PixelSize.Width * 4); }
        finally { pinned.Free(); }
        return pixels;
    }
}