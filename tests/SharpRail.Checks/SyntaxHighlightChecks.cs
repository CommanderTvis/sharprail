using System.Diagnostics;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Editor;
using SharpRail.Scintilla;
using SharpRail.UI.Editor;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

using SkiaSharp;

namespace SharpRail.Checks;

internal static class SyntaxHighlightChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Pump()
    { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { Pump(); if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Syntax highlighting did not finish."); Thread.Sleep(5); }
        Pump();
    }
    private static byte Role(string role) => (byte)Array.IndexOf(TextMateHighlighter.Roles, role);
    private static byte At(byte[] styles, string text, string fragment) => styles[Encoding.UTF8.GetByteCount(text.AsSpan(0, text.IndexOf(fragment, StringComparison.Ordinal)))];

    internal static void Run()
    {
        Task.Run(Grammars).GetAwaiter().GetResult();
        if (OperatingSystem.IsMacOS()) { Editor(); Documents(); Diffs(); Settings(); }
        Console.WriteLine("PASS TextMate language coverage, edits, Unicode, lifecycle, themes and diffs");
    }

    private static void Grammars()
    {
        var highlighter = new TextMateHighlighter();
        (string Path, string Text)[] samples =
        [
            ("a.js", "const x = 42;"), ("a.py", "def f(): return 42"), ("a.java", "public class A {}"),
            ("a.php", "<?php function f() { return 42; }"), ("a.cs", "public class A {}"), ("a.ts", "const x: number = 42;"),
            ("a.css", "a { color: red; }"), ("a.cpp", "int main() { return 42; }"), ("a.rb", "def f; 42; end"),
            ("a.c", "int main() { return 42; }"), ("a.swift", "let x = 42"), ("a.go", "package main\nfunc main() {}"),
            ("a.r", "x <- 42"), ("a.sh", "echo \"hello\""), ("a.kt", "val x = 42"), ("a.scala", "val x = 42"),
            ("a.m", "@interface A\n@end"), ("a.ps1", "$x = 42"), ("a.rs", "fn main() {}"), ("a.dart", "void main() {}"),
            ("Dockerfile", "FROM alpine\nRUN echo hello"), ("Dockerfile.dev", "FROM alpine"),
            ("a.md", "# Heading\n**bold**"), ("a.json", "{\"key\": 42}"), ("a.yaml", "key: 42"),
            (".gitignore", "# comment\n!keep\n*.log"), (".dockerignore", "# comment\nnode_modules"),
            ("Makefile", "all:\n\techo hello"), ("a.xml", "<root name=\"a\"/>"), ("a.html", "<p>Hello</p>"),
            ("a.sql", "SELECT 42;"), ("a.ini", "[section]\nkey=value"),
            ("SharpRail.slnx", "<Solution><Project Path=\"app.csproj\" /></Solution>"),
            ("a.toml", "[package]\nname = \"app\"\nversion = 42"), ("a.properties", "# comment\nkey=value"),
            (".gitattributes", "# comment\n*.png binary"), (".gitconfig", "[user]\nname = Developer"), (".gitmodules", "[submodule \"lib\"]\npath = lib"),
            ("build.gradle", "plugins { id 'java' }"), ("build.gradle.kts", "plugins { kotlin(\"jvm\") }"),
            (".npmignore", "# comment\n*.log"), (".prettierignore", "# comment\nnode_modules"), (".eslintignore", "# comment\ndist"),
            (".containerignore", "# comment\n.git"), ("a.bash", "echo \"hello\""), ("a.zsh", "echo \"hello\""),
            (".bashrc", "export PATH=\"$PATH\""), (".zshrc", "export PATH=\"$PATH\""), ("a.fish", "set name \"hello\""),
            ("a.bat", "@echo off\nREM comment"), ("a.cmd", "@echo off"),
            ("a.patch", "--- a/file\n+++ b/file\n@@ -1 +1 @@\n-old\n+new")
        ];
        foreach (var sample in samples)
        {
            var styles = highlighter.Highlight(sample.Text, sample.Path, null, default);
            Require(TextMateHighlighter.Detect(sample.Path) is not null && styles.Any(style => style != 0), $"No highlighting for {sample.Path}.");
            Require(styles.Length == Encoding.UTF8.GetByteCount(sample.Text), $"Wrong byte coverage for {sample.Path}.");
        }
        var swift = "// comment\nlet message = \"hello\"";
        const string attributes = "*.txt text eol=lf\n[attr]binary -diff -text";
        var attributeStyles = highlighter.Highlight(attributes, ".gitattributes", null, default);
        Require(At(attributeStyles, attributes, "*.txt") == Role("string") && At(attributeStyles, attributes, "text") == Role("keyword") &&
            At(attributeStyles, attributes, "eol") == Role("attributeName"), "Git attributes lost paths or attribute flags/values.");
        var swiftStyles = highlighter.Highlight(swift, "a.swift", null, default);
        Require(At(swiftStyles, swift, "comment") == Role("comment") && At(swiftStyles, swift, "hello") == Role("string"), "Swift repository compatibility lost comments or strings.");
        Require(TextMateHighlighter.Detect("a.tsx") == "source.tsx", "TSX selected the TypeScript grammar.");
        Require(TextMateHighlighter.Detect("a.cpp") == "source.cpp", "C++ selected the C grammar.");
        Require(TextMateHighlighter.Detect("build.kts") == "source.kotlin" && TextMateHighlighter.Detect("build.sbt") == "source.scala", "Script extensions missing.");
        Require(highlighter.Highlight("words", "a.unknown", null, default).All(style => style == 0), "Unknown files should stay plain.");
        const string patch = "--- a/file\n+++ b/file\n@@ -1 +1 @@\n-old\n+new";
        var patchStyles = highlighter.Highlight(patch, "a.patch", null, default);
        Require(At(patchStyles, patch, "old") != At(patchStyles, patch, "new") && At(patchStyles, patch, "new") != 0, "Patch additions and removals need distinct colours.");
        const string custom = """{"name":"Example","scopeName":"source.example","patterns":[{"name":"keyword.control.example","match":"\\bhello\\b"}]}""";
        var definition = CustomHighlighting.Import(custom, "*.example, Examplefile");
        try
        {
            CustomHighlighting.Apply([definition]);
            Require(TextMateHighlighter.Detect("Examplefile") == "source.example" && TextMateHighlighter.Detect("a.EXAMPLE") == "source.example", "Custom filename patterns failed.");
            Require(At(highlighter.Highlight("hello", "a.example", null, default), "hello", "hello") == Role("keyword"), "Custom grammar did not tokenize.");
            var replacement = CustomHighlighting.Import(custom.Replace("keyword.control", "string.quoted", StringComparison.Ordinal), "*.example");
            CustomHighlighting.Apply([replacement]);
            Require(At(highlighter.Highlight("hello", "a.example", null, default), "hello", "hello") == Role("string"), "Replacing the same custom scope reused stale tokens.");
        }
        finally { CustomHighlighting.Apply([]); }
        Require(highlighter.Highlight("hello", "a.example", null, default).All(style => style == 0), "Removed grammar remained active.");
        try { CustomHighlighting.Import(custom, "dir/*.example"); throw new InvalidOperationException("Directory pattern accepted."); }
        catch (ArgumentException) { }
        var text = "/* 😀 comment\r\nstill comment */\r\npublic class Café {}";
        var initial = highlighter.Highlight(text, "a.cs", null, default);
        Require(At(initial, text, "still") == Role("comment") && At(initial, text, "public") == Role("keyword"), "Multiline/CRLF/Unicode styles failed.");
        var edited = text.Replace("/*", "//", StringComparison.Ordinal);
        var updated = highlighter.Highlight(edited, "a.cs", null, default);
        Require(At(updated, edited, "still") != Role("comment"), "Edit did not propagate tokenizer state.");
        Require(updated.SequenceEqual(new TextMateHighlighter().Highlight(edited, "a.cs", null, default)), "Incremental highlighting differs from a fresh parse.");
        Require(highlighter.Highlight("// comment\rpublic class A {}", "a.cs", null, default).Any(style => style == Role("keyword")), "CR line endings lost state.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { highlighter.Highlight(text, "a.cs", null, cancelled.Token); throw new InvalidOperationException("Cancelled highlighting ran."); }
        catch (OperationCanceledException) { }
        Require(highlighter.Highlight(new string('x', TextMateHighlighter.MaximumCharacters + 1), "a.cs", null, default).All(style => style == 0), "Large file highlighting is unbounded.");
    }

    private static int NativeStyle(ScintillaEditor editor, string fragment)
    {
        var position = Encoding.UTF8.GetByteCount(editor.Text.AsSpan(0, editor.Text.IndexOf(fragment, StringComparison.Ordinal)));
        return unchecked((byte)editor.Document.Send(ScintillaMessage.GetStyleAt, position));
    }

    private static void Editor()
    {
        var frame = new EditorFrame("public class Café { string x = \"😀\"; }", "SyntaxEditor");
        frame.Highlight("a.cs");
        var window = new Window { Width = 640, Height = 300, Content = frame };
        Exception? failure = null;
        frame.Editor.OperationFailed += (_, error) => failure = error;
        var synchronization = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        var theme = Ui.Theme;
        try
        {
            Until(() => failure is not null || NativeStyle(frame.Editor, "public") >= 40);
            Require(failure is null, $"Highlighting failed: {failure}");
            Require(!frame.Editor.IsModified, "Highlighting dirtied the buffer.");
            Painted(window, Ui.Theme.Syntax["keyword"], "editor");
            var keywordStyle = NativeStyle(frame.Editor, "public");
            Require(keywordStyle != NativeStyle(frame.Editor, "\"😀\""), "Syntax styles did not reach Scintilla.");
            frame.Editor.SelectRange(7, 5);
            var selection = frame.Editor.Selection;
            var syntax = new Dictionary<string, Color>(theme.Syntax) { ["keyword"] = Color.Parse("#12ab34") };
            Ui.Apply(theme with { Syntax = syntax }); Pump();
            var color = frame.Editor.Document.Send(ScintillaMessage.StyleGetFore, keywordStyle);
            Require(color == 0x34ab12, "Theme did not update native syntax colours.");
            Require(frame.Editor.Selection == selection && !frame.Editor.IsModified, "Theme changed selection or dirty state.");
            frame.Editor.ClearTextStyles();
            Require(NativeStyle(frame.Editor, "public") == 0 && frame.Editor.Selection == selection && !frame.Editor.IsModified, "Restoring plain text changed the document.");
            frame.Editor.Text = "/* open\npublic class A {}";
            Until(() => NativeStyle(frame.Editor, "public") == 40 + Role("comment"));
            frame.Editor.Text = "public class Newest {}";
            frame.Editor.Text = "// 😀 newest\npublic class Latest {}";
            Until(() => NativeStyle(frame.Editor, "public") == 40 + Role("keyword"));
            Require(NativeStyle(frame.Editor, "newest") == 40 + Role("comment"), "Stale token result was applied.");
            window.Content = null; Pump();
            frame.Editor.Text = "val x = 42";
            frame.Highlight("a.kt");
            window.Content = frame;
            Until(() => NativeStyle(frame.Editor, "val") == 40 + Role("keyword"));
            Require(failure is null, $"Reattached highlighting failed: {failure}");
            frame.Editor.Text = "public class Pending {}";
            frame.Editor.Dispose();
            var cancellationWait = Stopwatch.StartNew();
            while (cancellationWait.Elapsed < TimeSpan.FromMilliseconds(150)) { Pump(); Thread.Sleep(5); }
            Require(failure is null, "Pending highlighting survived editor disposal.");
            window.Content = null; Pump();
        }
        finally { Ui.Apply(theme); window.Close(); frame.Editor.Dispose(); SynchronizationContext.SetSynchronizationContext(synchronization); }
    }

    private static void Documents()
    {
        var root = Directory.GetCurrentDirectory();
        var host = new LocalProjectAdapter(new ProjectServices(root));
        Exception? failure = null;
        using var code = new CodeDocumentView(new("a.kt", "val message = \"hello\""), root, host,
            () => { }, _ => { }, () => { }, error => failure = error);
        using var markdown = new MarkdownDocumentView("# Heading\n**bold**", "a.md",
            new((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 80, true));
        var window = new Window { Width = 640, Height = 300, Content = code };
        window.Show();
        try
        {
            Until(() => failure is not null || NativeStyle(code.Editor, "val") == 40 + Role("keyword"));
            Require(failure is null && !code.HasPendingChanges, "File editor syntax changed its save state.");
            window.Content = markdown;
            markdown.Show(MarkdownDocumentView.Mode.Source);
            var editor = ((EditorFrame)markdown.Source!).Editor;
            Until(() => NativeStyle(editor, "Heading") == 40 + Role("keyword"));
            Require(!editor.IsModified, "Markdown source highlighting dirtied the buffer.");
        }
        finally { window.Close(); }
    }

    private static void Settings()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".bench", "highlighting-settings-" + Guid.NewGuid().ToString("N"));
        using var app = new E2E.E2eWorkspace(root, openFiles: false);
        app.Window.ShowSettings("Highlighting");
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        T Find<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        const string json = """{"name":"Example","scopeName":"source.example","patterns":[{"name":"keyword.control.example","match":"hello"}]}""";
        var frame = new EditorFrame("hello", "CustomSyntaxEditor");
        frame.Highlight("a.example");
        var preview = new Window { Width = 400, Height = 200, Content = frame };
        preview.Show();
        try
        {
            Find<TextBox>("HighlightingPatterns").Text = "dir/*.example";
            Find<TextBox>("HighlightingGrammar").Text = json;
            var field = Find<TextBox>("HighlightingGrammar");
            CustomHighlighting.Apply(CustomHighlighting.Current);
            Require(ReferenceEquals(field, Find<TextBox>("HighlightingGrammar")) && field.Text == json,
                "Catalogue updates replaced the custom-highlighting form.");
            Click(Find<Button>("HighlightingImport"));
            Until(() => Find<TextBlock>("HighlightingError").IsVisible);
            Require(app.State!.Current.Settings.CustomHighlighting.Length == 0, "Rejected grammar changed shared settings.");
            Find<TextBox>("HighlightingPatterns").Text = "*.example";
            Click(Find<Button>("HighlightingImport"));
            Until(() => CustomHighlighting.Detect("a.example", CustomHighlighting.Current) == "source.example");
            Until(() => NativeStyle(frame.Editor, "hello") == 40 + Role("keyword"));
            Require(!frame.Editor.IsModified, "Importing custom highlighting dirtied an open editor.");
            var remove = Find<StackPanel>("HighlightingGrammars").GetLogicalDescendants().OfType<Button>().Single();
            Click(remove);
            Until(() => app.State.Current.Settings.CustomHighlighting == "[]" && CustomHighlighting.Current.Length == 0);
            Until(() => NativeStyle(frame.Editor, "hello") == 40);
            // Test the registered launch route without starting an external agent process.
            foreach (var row in app.Workbench.PluginRegistry.Launchers.Where(row => row.Value.Id == "codex").ToArray())
                app.Workbench.PluginRegistry.RemovePlugin(row.PluginId);
            string? prompt = null;
            app.Workbench.PluginRegistry.AddLauncher("highlighting-fixture", new("codex", "Codex", "terminal", options =>
            { prompt = options.InitialPrompt; return "echo highlighting-fixture"; }, () => new(true)));
            Require(Find<Button>("HighlightingAskCodex").IsEnabled, "A registered agent did not enable the highlighting button.");
            Click(Find<Button>("HighlightingAskCodex"));
            Until(() => prompt is not null && !app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
            Require(prompt!.Contains("add-highlighting", StringComparison.Ordinal), "Agent prompt did not reference the builtin skill.");
        }
        finally { settings.Close(); preview.Close(); frame.Editor.Dispose(); CustomHighlighting.Apply([]); }
    }

    private static void Painted(Window window, Color color, string name)
    {
        Pump();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Syntax view did not render.");
        Directory.CreateDirectory(".bench/textmate");
        frame.Save(".bench/textmate/" + name + ".png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        using var stream = new MemoryStream();
        frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        using var pixels = SKBitmap.Decode(stream);
        var count = 0;
        for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var pixel = pixels.GetPixel(x, y);
                if (Math.Abs(pixel.Red - color.R) <= 1 && Math.Abs(pixel.Green - color.G) <= 1 && Math.Abs(pixel.Blue - color.B) <= 1) count++;
            }
        Require(count > 2, "Syntax colours did not reach the rendered view.");
    }

    private static void Diffs()
    {
        const string diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n-/* old\n+public class New {}\n public class Context {}\n@@ -10 +10 @@\n-public class Before {}\n+public class After {}\n";
        using var view = new DiffView(diff, "a.cs", double.PositiveInfinity);
        var window = new Window { Width = 1100, Height = 500, Content = view };
        window.Show(); Pump();
        try
        {
            ScintillaEditor Find(string name) => view.GetVisualDescendants().OfType<ScintillaEditor>().Single(editor => editor.Name == name);
            var old = Find("DiffOldText"); var newer = Find("DiffNewText");
            Until(() => NativeStyle(newer, "public") >= 40);
            Require(NativeStyle(newer, "public") % TextMateHighlighter.Roles.Length == (40 + Role("keyword")) % TextMateHighlighter.Roles.Length, "New-side syntax inherited old-side comment state.");
            var style = NativeStyle(newer, "public");
            var background = newer.Document.Send(ScintillaMessage.StyleGetBack, style);
            Require(background != newer.Document.Send(ScintillaMessage.StyleGetBack, 32), "Added-line wash disappeared.");
            Until(() => NativeStyle(old, "Before") >= 40);
            Require(NativeStyle(old, "Before") != NativeStyle(old, "old"), "Collapsed gap did not reset old-side state.");
            Painted(window, Ui.Theme.Syntax["keyword"], "diff-split");
            window.Width = 600; Pump();
            var inline = Find("DiffInlineText");
            Until(() => NativeStyle(inline, "public") >= 40);
            Painted(window, Ui.Theme.Syntax["keyword"], "diff-inline");
            var text = inline.Text;
            var beforePosition = text.IndexOf("public class Before", StringComparison.Ordinal);
            var beforeStyle = (int)inline.Document.Send(ScintillaMessage.GetStyleAt, Encoding.UTF8.GetByteCount(text.AsSpan(0, beforePosition)));
            Require(beforeStyle == 40 + 3 * TextMateHighlighter.Roles.Length + Role("keyword"), "Inline hunk syntax state did not reset.");
            Require(NativeStyle(inline, "diff --git") == 40 + 5 * TextMateHighlighter.Roles.Length, "Diff metadata was tokenized as code.");
        }
        finally { window.Close(); }
    }
}