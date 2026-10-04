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
        KeepsSelectionAcrossFocus(host);
        SelectsLinks(host);
        ExtendsSelections(host);
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

    // As in a browser, a selection that began with a double-click keeps growing when dragged past its paragraph, and
    // Shift+click extends any selection across paragraphs.
    private static void ExtendsSelections(IProjectServices host)
    {
        using var preview = new MarkdownPreview("First paragraph here.\n\nSecond paragraph here.\n\nThird paragraph here.", "extend.md",
            MarkdownContexts.For(host, new Preferences(), (_, _) => { }));
        var window = new Window { Width = 500, Height = 300, Content = preview };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var blocks = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().ToArray();
        Point At(SelectableTextBlock block, double x) => block.TranslatePoint(new Point(x, block.Bounds.Height / 2), window)!.Value;
        var word = At(blocks[0], 10);
        window.MouseDown(word, MouseButton.Left); window.MouseUp(word, MouseButton.Left);
        window.MouseDown(word, MouseButton.Left);
        window.MouseMove(At(blocks[2], blocks[2].Bounds.Width - 2), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(blocks[2], blocks[2].Bounds.Width - 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        E2E.E2eWorkspace.Require(preview.SelectedText.StartsWith("First", StringComparison.Ordinal) && preview.SelectedText.Contains("Third paragraph", StringComparison.Ordinal),
            $"Dragging after a double-click extends the selection across paragraphs, got '{preview.SelectedText}'.");
        var start = At(blocks[0], 1);
        window.MouseDown(start, MouseButton.Left); window.MouseUp(start, MouseButton.Left);
        var end = At(blocks[1], blocks[1].Bounds.Width - 2);
        window.MouseDown(end, MouseButton.Left, RawInputModifiers.Shift); window.MouseUp(end, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        E2E.E2eWorkspace.Require(preview.SelectedText.StartsWith("First paragraph here.\n\nSecond paragraph", StringComparison.Ordinal),
            $"Shift+click extends the selection across paragraphs, got '{preview.SelectedText}'.");
        window.Close();
        Console.WriteLine("PASS Markdown selections extend across paragraphs after a double-click and with Shift+click");
    }

    // A link is part of the sentence it sits in: selecting across it copies its words and highlights it with the rest.
    private static void SelectsLinks(IProjectServices host)
    {
        using var preview = new MarkdownPreview("See [the guide](https://example.com/guide) before you start.", "links.md",
            MarkdownContexts.For(host, new Preferences(), (_, _) => { }));
        var reported = new List<string>();
        preview.SelectionChanged += reported.Add;
        var window = new Window { Width = 600, Height = 200, Content = preview };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var block = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
        block.Focus();
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.A, command, PhysicalKey.A, null); Dispatcher.UIThread.RunJobs();
        const string sentence = "See the guide before you start.";
        E2E.E2eWorkspace.Require(preview.SelectedText == sentence, $"A selection across a link includes its words, got '{preview.SelectedText}'.");
        window.KeyPress(Key.C, command, PhysicalKey.C, null); Dispatcher.UIThread.RunJobs();
        var copied = window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult();
        E2E.E2eWorkspace.Require(copied == sentence, $"Copying a selection across a link copies its words, got '{copied}'.");
        E2E.E2eWorkspace.Require(reported.LastOrDefault() == sentence, $"The selection reported to plugins includes the link, got '{reported.LastOrDefault()}'.");
        var link = block.GetLogicalDescendants().OfType<Button>().Single();
        E2E.E2eWorkspace.Require(link.Background is ISolidColorBrush fill && block.SelectionBrush is ISolidColorBrush selection && fill.Color == selection.Color,
            "A link inside the selection is highlighted with it.");
        window.Close();
        Console.WriteLine("PASS a Markdown selection across a link includes, copies and highlights the link");
    }

    // Like an editor's, the preview's selection outlives focus moving to a terminal, so an agent's IDE integration keeps
    // reporting the selected lines rather than only the file.
    private static void KeepsSelectionAcrossFocus(IProjectServices host)
    {
        using var preview = new MarkdownPreview("First paragraph here.\n\nSecond paragraph here.", "focus.md",
            MarkdownContexts.For(host, new Preferences(), (_, _) => { }));
        var reported = new List<string>();
        preview.SelectionChanged += reported.Add;
        var elsewhere = new TextBox { Text = "a terminal stand-in" };
        var window = new Window { Width = 500, Height = 300, Content = new StackPanel { Children = { elsewhere, preview } } };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var block = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().First();
        Point At(double x) => block.TranslatePoint(new Point(x, block.Bounds.Height / 2), window)!.Value;
        window.MouseDown(At(1), MouseButton.Left);
        window.MouseMove(At(block.Bounds.Width - 2), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(block.Bounds.Width - 2), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var selected = preview.SelectedText;
        E2E.E2eWorkspace.Require(selected.Length > 0 && block.IsFocused, "A drag selects text and focuses the paragraph.");
        reported.Clear();
        elsewhere.Focus(); Dispatcher.UIThread.RunJobs();
        E2E.E2eWorkspace.Require(preview.SelectedText == selected && reported.All(text => text.Length > 0),
            $"Moving focus elsewhere keeps the preview's selection and never reports it cleared ('{preview.SelectedText}', reported [{string.Join("|", reported)}]).");
        window.Close();
        Console.WriteLine("PASS a Markdown selection survives focus moving to another control, as an editor's does");
    }
}