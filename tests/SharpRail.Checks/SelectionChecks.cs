using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class SelectionChecks
{
    internal static void Run(IProjectServices host)
    {
        var original = Ui.Theme;
        foreach (var theme in Themes.All)
        {
            Ui.Apply(theme);
            using var preview = new MarkdownPreview("A selectable paragraph.", "selection.md", MarkdownContexts.For(host, new Preferences(), (_, _) => { }));
            var input = new TextBox { Text = "Selectable input" };
            var window = new Window { Width = 400, Height = 250, Content = new StackPanel { Children = { input, preview } } };
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var paragraph = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
            paragraph.SelectionStart = 0; paragraph.SelectionEnd = 10;
            input.SelectionStart = 0; input.SelectionEnd = 10;
            // Avalonia highlights glyph runs rather than whole line boxes, so the preview keeps the full selection
            // colour; upstream's 40% canvas mix left dark-theme selections nearly invisible.
            var expectedText = theme.Id switch
            {
                "light" => Color.FromArgb(56, 107, 87, 255),
                "dark" => Color.FromArgb(38, 106, 200, 255),
                _ => theme["selection"]
            };
            var foreground = theme.Colors["selectionForeground"];
            E2E.E2eWorkspace.Require(input.SelectionBrush is SolidColorBrush textBrush && textBrush.Color == expectedText &&
                paragraph.SelectionBrush is SolidColorBrush previewBrush && previewBrush.Color == expectedText,
                $"Text selection in {theme.Id} must use the theme selection colour in inputs and the Markdown preview.");
            E2E.E2eWorkspace.Require(foreground is null
                ? input.SelectionForegroundBrush is null && paragraph.SelectionForegroundBrush is null
                : input.SelectionForegroundBrush is ISolidColorBrush inputText && inputText.Color == foreground &&
                    paragraph.SelectionForegroundBrush is ISolidColorBrush previewText && previewText.Color == foreground,
                $"Selected text in {theme.Id} must retain its foreground unless the manifest overrides it.");
            window.Close();
        }
        Ui.Apply(original);
        Console.WriteLine("PASS input and Markdown selection appearance under every bundled theme");
        AcrossBlocks(host);
    }

    private static void AcrossBlocks(IProjectServices host)
    {
        using var preview = new MarkdownPreview("First paragraph here.\n\nSecond paragraph here.\n\nThird paragraph here.", "across.md",
            MarkdownContexts.For(host, new Preferences(), (_, _) => { }));
        var window = new Window { Width = 500, Height = 300, Content = preview };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var blocks = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().ToArray();
        Point At(SelectableTextBlock block, double x) => block.TranslatePoint(new Point(x, block.Bounds.Height / 2), window)!.Value;
        window.MouseDown(At(blocks[0], 1), MouseButton.Left);
        window.MouseMove(At(blocks[1], blocks[1].Bounds.Width / 2), RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(blocks[2], 1), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(blocks[2], 1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        E2E.E2eWorkspace.Require(preview.SelectedText.StartsWith("First paragraph here.\n\nSecond paragraph here.", StringComparison.Ordinal) &&
            blocks[2].SelectionStart == blocks[2].SelectionEnd,
            $"A drag must select through every paragraph between its ends, got '{preview.SelectedText}'.");
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.C, command, PhysicalKey.C, null); Dispatcher.UIThread.RunJobs();
        var copied = window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult();
        E2E.E2eWorkspace.Require(copied == preview.SelectedText, $"Copy must take the whole cross-paragraph selection, got '{copied}'.");
        window.MouseDown(At(blocks[1], 1), MouseButton.Left); window.MouseUp(At(blocks[1], 1), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        E2E.E2eWorkspace.Require(blocks.All(block => block.SelectionStart == block.SelectionEnd), "A new click must clear the document selection.");
        window.KeyPress(Key.A, command, PhysicalKey.A, null);
        E2E.E2eWorkspace.Require(preview.SelectedText.Contains("Third paragraph here.", StringComparison.Ordinal) &&
            preview.SelectedText.StartsWith("First", StringComparison.Ordinal), "Select all must cover the whole document.");
        window.Close();
        Console.WriteLine("PASS Markdown selection drags, copies and selects all across paragraphs");
    }
}