using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.Scintilla;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class MarkdownFindE2E
{
    internal static void Run(string root)
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("SKIP Markdown source editor find: macOS only"); return; }
        foreach (var mode in new[] { MarkdownDocumentView.Mode.Source, MarkdownDocumentView.Mode.Split })
            foreach (var modifier in new[] { RawInputModifiers.Control, RawInputModifiers.Meta })
                Source(root, mode, modifier);
    }

    private static void Press(E2eWorkspace app, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        app.Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        app.Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Settle(50);
    }

    private static void Source(string root, MarkdownDocumentView.Mode mode, RawInputModifiers modifier)
    {
        var directory = Path.Combine(root, $"markdown-find-{mode}-{modifier}");
        Directory.CreateDirectory(directory);
        var text = "# Finder\n\n🚆 é **Needle** first.\n\n" +
            string.Concat(Enumerable.Repeat("Filler paragraph.\n\n", 80)) + "needle second.\n\n[Link](source-only.md)\n";
        File.WriteAllText(Path.Combine(directory, "notes.md"), text);
        using var app = new E2eWorkspace(directory);
        app.Open("notes.md", true);
        var view = app.Window.GetLogicalDescendants().OfType<MarkdownDocumentView>().Single();
        app.Click(app.Find<Button>(mode == MarkdownDocumentView.Mode.Source ? "MarkdownSourceMode" : "MarkdownSplitMode"));
        var editor = app.Find<ScintillaEditor>("MarkdownSource");
        Require(editor.Focus(), "The source editor must accept focus before Find.");
        Press(app, Key.F, modifier);
        var find = app.Find<Border>("FindBar");
        var input = app.Find<TextBox>("FindInput");
        var count = app.Find<TextBlock>("FindCount");
        Require(find.IsVisible && input.IsFocused, "Find from the source must focus one visible search input.");
        input.Text = "needle";
        Settle(100);
        Require(count.Text == "1/2" && editor.Selection.Text == "Needle",
            $"{mode} must search the source buffer, select Unicode-positioned text and count it once; got {count.Text}.");
        Require(!editor.IsModified && editor.Text == text, "Searching must not edit or dirty the source buffer.");
        var point = input.TranslatePoint(new Point(input.Bounds.Width / 2, input.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseDown(point, MouseButton.Left);
        app.Window.MouseUp(point, MouseButton.Left);
        Require(input.IsFocused, "The find input must remain clickable above the source editor.");
        Press(app, Key.Enter);
        Require(count.Text == "2/2" && editor.Selection.Text == "needle" && editor.FirstVisibleLine > 0,
            "Next must select and scroll to the second source match.");
        Press(app, Key.Enter);
        Require(count.Text == "1/2" && editor.Selection.Text == "Needle", "Next must wrap to the first match.");
        Press(app, Key.Enter, RawInputModifiers.Shift);
        Require(count.Text == "2/2" && editor.Selection.Text == "needle", "Previous must wrap to the last match.");
        input.Text = "source-only.md";
        Settle(50);
        Require(count.Text == "1/1" && editor.Selection.Text == "source-only.md", "Find must include Markdown syntax hidden in the preview.");
        input.Text = "missing text";
        Settle(50);
        Require(count.Text == "0/0" && input.BorderBrush == Ui.Danger, "A missing source match must show the no-match state.");
        input.Text = "needle";
        Settle(50);
        Press(app, Key.Escape);
        Require(!find.IsVisible && editor.IsFocused && editor.Selection.Text == "Needle", "Escape must return to the selected source match.");
        Press(app, Key.F, modifier);
        app.Click(app.Find<Button>("MarkdownPreviewMode"));
        Require(!find.IsVisible, "Changing the view must dismiss find from the now-hidden source pane.");

        if (mode == MarkdownDocumentView.Mode.Split)
        {
            app.Click(app.Find<Button>("MarkdownSplitMode"));
            var heading = view.Preview.GetLogicalDescendants().OfType<SelectableTextBlock>().First();
            app.Click(heading);
            Press(app, Key.F, modifier);
            input.Text = "source-only.md";
            Settle(50);
            Require(count.Text == "0/0", "Find from the preview half must exclude the source buffer.");
            input.Text = "needle";
            Settle(50);
            Require(count.Text == "1/2", $"Split preview find must count rendered matches once; got {count.Text}.");
            Press(app, Key.Escape);
            Require(heading.IsFocused, "Escape must restore the preview control that opened Find.");
        }
        app.Click(app.Find<Button>(mode == MarkdownDocumentView.Mode.Source ? "MarkdownSourceMode" : "MarkdownSplitMode"));
        Require(editor.Focus(), "The source must regain focus for editing after Find.");
        app.Window.KeyTextInput("Replacement");
        Require(editor.IsModified && editor.Text.Contains("**Replacement**", StringComparison.Ordinal), "Typing after Escape must edit the source at the found match.");
        Press(app, Key.F, modifier);
        input.Text = "Replacement";
        Settle(50);
        Require(count.Text == "1/1" && editor.Selection.Text == "Replacement", "Find must search unsaved source edits.");
        Press(app, Key.Escape);
        Console.WriteLine($"PASS Markdown {mode} find with {modifier}: source matches, Unicode, scroll, wrap, focus and pane isolation");
    }
}