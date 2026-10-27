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
        Task.Run(IncrementalTokenizing).GetAwaiter().GetResult();
        Task.Run(TomlIncremental).GetAwaiter().GetResult();
        if (OperatingSystem.IsMacOS()) { Editor(); Typing(); Documents(); Diffs(); SingleSidedDiffs(); Settings(); }
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
        Toml(highlighter);
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

    private static void Toml(TextMateHighlighter highlighter)
    {
        const string text = "# comment\n[tasks.\"restore:update\"]\ndescription = \"Tab\\there\"\ndepends = [\"bootstrap\", 'literal']\n" +
            "env = { RestoreLockedMode = \"false\", Other = 'xyz' }\nrun = 'C:\\path'\ncount = 42\nratio = 1.5\nenabled = true\n" +
            "when = 1979-05-27T07:32:00Z\nmulti = \"\"\"\nfirst line\nsecond\"\"\"\nraw = '''\nraw line\n'''\n" +
            "list = [\n  \"one\",\n  \"two\"\n]\n[[bin.items]]\nname = \"last\"\n";
        var styles = highlighter.Highlight(text, "mise.toml", null, default);
        void Is(string role, params string[] fragments)
        {
            foreach (var fragment in fragments)
                Require(At(styles, text, fragment) == Role(role), $"TOML '{fragment}' should be {role}, was {TextMateHighlighter.Roles[At(styles, text, fragment)]}.");
        }
        Is("comment", "comment");
        Is("property", "tasks", "\"restore:update\"", "description", "depends", "env", "RestoreLockedMode", "Other", "run =", "bin", "items", "name");
        Is("string", "Tab", "\"bootstrap\"", "literal", "false", "xyz", "C:", "first line", "second", "raw line", "one", "two", "last");
        Is("number", "42", "1.5");
        Is("constant", "true", "1979-05-27", "\\t");
        foreach (var id in new[] { "dark", "light" })
        {
            var syntax = Themes.Resolve(id).Syntax;
            Require(syntax["property"] != syntax["string"] && syntax["property"] != syntax["type"] && syntax["string"] != syntax["comment"],
                $"TOML keys, strings and comments share a colour in {id}.");
        }
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
            foreach (var text in new[] { "// 😀 detached\npublic class ReattachedLonger {}", "public class A {}", "//😀abcdefghijk" })
            {
                window.Content = null; Pump();
                frame.Editor.Text = text;
                window.Content = frame;
                var fragment = text.StartsWith("//", StringComparison.Ordinal) ? (text.Contains("detached", StringComparison.Ordinal) ? "detached" : "abcdefghijk") : "public";
                var role = text.StartsWith("//", StringComparison.Ordinal) ? "comment" : "keyword";
                Until(() => NativeStyle(frame.Editor, fragment) == 40 + Role(role));
                Require(failure is null, $"Detached buffer edit failed: {failure}");
            }
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

    private static byte[] Styles(ScintillaEditor editor)
    {
        var styles = new byte[Encoding.UTF8.GetByteCount(editor.Text)];
        for (var i = 0; i < styles.Length; i++) styles[i] = unchecked((byte)editor.Document.Send(ScintillaMessage.GetStyleAt, i));
        return styles;
    }

    // Typing faster than the highlight debounce, with a frame between keystrokes, must never drop a colour.
    private static void Typing()
    {
        const string source = "// header comment\nnamespace N;\npublic class A\n{\n    string s = \"hello world\";\n    // trailing note\n    int n = 42;\n}\n";
        var frame = new EditorFrame(source, "TypingSyntaxEditor");
        frame.Highlight("a.cs");
        var window = new Window { Width = 640, Height = 300, Content = frame };
        var synchronization = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        try
        {
            Until(() => NativeStyle(frame.Editor, "public") >= 40);
            foreach (var (after, role) in new[] { ("\"hello", "string"), ("// trailing", "comment") })
            {
                var text = frame.Editor.Text;
                var start = text.IndexOf(after, StringComparison.Ordinal) + after.Length;
                var position = Encoding.UTF8.GetByteCount(text.AsSpan(0, start));
                var baseline = Styles(frame.Editor);
                frame.Editor.Focus(); frame.Editor.SelectRange(start, 0); Pump();
                var typed = 0;
                foreach (var ch in "xyzw")
                {
                    window.KeyTextInput(ch.ToString()); typed++; Pump();
                    var styles = Styles(frame.Editor);
                    Require(styles.Length == baseline.Length + typed, "Typing did not reach the editor.");
                    for (var i = 0; i < styles.Length; i++)
                    {
                        var expected = i < position ? baseline[i] : i >= position + typed ? baseline[i - typed] : (byte)(40 + Role(role));
                        Require(styles[i] == expected, $"Typing into a {role} changed byte {i} from style {expected} to {styles[i]} after {typed} keystrokes.");
                    }
                }
                var final = frame.Editor.Text;
                var fresh = new TextMateHighlighter().Highlight(final, "a.cs", null, CancellationToken.None).Select(style => (byte)(40 + style)).ToArray();
                Until(() => Styles(frame.Editor).SequenceEqual(fresh));
            }
        }
        finally { window.Close(); frame.Editor.Dispose(); SynchronizationContext.SetSynchronizationContext(synchronization); }
    }

    // An edit retokenizes from the edited line until the grammar state converges, also when lines shift.
    private static void IncrementalTokenizing()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 5000; i++) builder.Append("    public int Field").Append(i).Append(" = ").Append(i).Append("; // note\n");
        var text = builder.ToString();
        var highlighter = new TextMateHighlighter();
        highlighter.Highlight(text, "a.cs", null, CancellationToken.None);
        var middle = text.IndexOf("Field2500", StringComparison.Ordinal);
        foreach (var edit in new[] { "x", "\n", "/*" })
        {
            var edited = text.Insert(middle, edit);
            var timer = Stopwatch.StartNew();
            var styles = highlighter.Highlight(edited, "a.cs", null, CancellationToken.None);
            var elapsed = timer.Elapsed;
            var tokenized = highlighter.TokenizedLines;
            Require(styles.SequenceEqual(new TextMateHighlighter().Highlight(edited, "a.cs", null, CancellationToken.None)), $"Incremental tokenizing of {edit.Trim()} differs from a fresh pass.");
            Console.WriteLine($"Keystroke {(edit == "\n" ? "newline" : edit)} on 5000 lines: {tokenized} lines tokenized, {elapsed.TotalMilliseconds:F1} ms");
            if (edit != "/*") Require(tokenized <= 3, $"Inserting {edit.Trim()} retokenized {tokenized} lines instead of the edited ones.");
            highlighter.Highlight(text, "a.cs", null, CancellationToken.None);
        }
    }

    // The repository's own mise.toml tokenizes identically fresh, while opened line by line and after edits; digits inside strings stay strings.
    private static void TomlIncremental()
    {
        var text = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "mise.toml")).ReplaceLineEndings("\n") +
            "\nrun = \"echo 'Windows packages are built on Windows.' >&2; exit 1\"\npath = \".tools\\\\dotnet\\\\dotnet.exe\"\n" +
            "env = { RestoreLockedMode = \"false\" }\nlist = [\"<x64|arm64>\", \"a \u2013 b \u2014 c\"]\nafter = 7\n";
        var fresh = new TextMateHighlighter().Highlight(text, "mise.toml", null, default);
        var exit = text.IndexOf(">&2; exit 1", StringComparison.Ordinal);
        foreach (var digit in new[] { exit + 3, exit + 10 })
            Require(fresh[Encoding.UTF8.GetByteCount(text.AsSpan(0, digit))] == Role("string"), "A digit inside a TOML basic string left the string.");
        var after = Encoding.UTF8.GetByteCount(text.AsSpan(0, text.LastIndexOf("after = 7", StringComparison.Ordinal)));
        Require(fresh[after] == Role("property") && fresh[after + 8] == Role("number"), "TOML state after strings with quotes and escapes drifted.");
        var highlighter = new TextMateHighlighter();
        var lines = text.Split('\n');
        for (var count = 1; count <= lines.Length; count++)
        {
            var prefix = string.Join('\n', lines.Take(count));
            Require(highlighter.Highlight(prefix, "mise.toml", null, default).SequenceEqual(new TextMateHighlighter().Highlight(prefix, "mise.toml", null, default)),
                $"Incremental TOML tokenizing diverges from a fresh pass at line {count}.");
        }
        var middle = text.IndexOf(">&2", StringComparison.Ordinal);
        foreach (var edit in new[] { "x", "\n", "\"", "'" })
        {
            var edited = text.Insert(middle, edit);
            Require(highlighter.Highlight(edited, "mise.toml", null, default).SequenceEqual(new TextMateHighlighter().Highlight(edited, "mise.toml", null, default)),
                $"Incremental TOML tokenizing after inserting {edit.Trim()} differs from a fresh pass.");
            Require(highlighter.Highlight(text, "mise.toml", null, default).SequenceEqual(fresh), "Undoing a TOML edit did not restore the fresh styles.");
        }
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
        var root = CheckRoot.Create("highlighting-settings");
        using var app = new E2E.E2eWorkspace(root, openFiles: false);
        app.Window.ShowSettings("Highlighting");
        Until(() => app.Window.OpenSettingsViews.Any());
        var settings = app.Window.OpenSettingsViews.Single();
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
            Until(() => prompt is not null && !app.Window.OpenSettingsViews.Any());
            Require(prompt!.Contains("add-highlighting", StringComparison.Ordinal), "Agent prompt did not reference the builtin skill.");
        }
        finally { settings.Close(); preview.Close(); frame.Editor.Dispose(); CustomHighlighting.Apply([]); }
    }

    private static void Painted(Window window, Color color, string name)
    {
        Pump();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Syntax view did not render.");
        frame.Save(CheckRoot.Artifact("textmate", name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
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

    private static void SingleSidedDiffs()
    {
        const string modified = "--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-before\n+after\n";
        foreach (var diff in new[]
        {
            "--- /dev/null\n+++ b/a.cs\n@@ -0,0 +1 @@\n+added\n",
            "--- a/a.cs\r\n+++ /dev/null\r\n@@ -1 +0,0 @@\r\n-deleted\r\n"
        })
        {
            using var view = new DiffView(diff, "a.cs", double.PositiveInfinity);
            var window = new Window { Width = 1100, Height = 500, Content = view };
            window.Show(); Pump();
            try
            {
                var toggle = view.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(button => button.Name == "DiffSplit");
                void Inline() => Require(toggle.IsChecked == false && view.GetVisualDescendants().OfType<ScintillaEditor>().Single().Name == "DiffInlineText",
                    "Added and deleted files must default to one inline editor.");
                Inline();
                window.Width = 600; Pump(); Inline();
                window.Width = 1200; Pump(); Inline();
                view.Update(modified); Pump();
                Require(toggle.IsChecked == true, "A modified file must still default to split when wide enough.");
                view.Update(diff); Pump(); Inline();
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Require(view.GetVisualDescendants().OfType<ScintillaEditor>().Count() == 2, "An explicit split must remain available for added and deleted files.");
                view.Update(diff + "\\ No newline at end of file\n");
                window.Width = 1300; Pump();
                Require(toggle.IsChecked == true, "Refreshing and resizing must preserve a manually selected split.");
            }
            finally { window.Close(); }
        }
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