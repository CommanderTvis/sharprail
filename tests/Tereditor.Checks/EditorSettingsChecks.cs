using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Host.Core;
using Tereditor.Plugins.UI.Kit.Editor;
using Tereditor.Scintilla;
using Tereditor.UI.Editor;
using Tereditor.UI.Rendering;

namespace Tereditor.Checks;

/// <summary>Settings › Editor's indentation, font family and line height reaching file editors and diffs.</summary>
internal static class EditorSettingsChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void Pump(Func<bool>? done = null)
    {
        var deadline = Awake.Now.AddSeconds(15);
        do { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
        while (done is not null && !done() && Awake.Now < deadline && Sleep());
        Require(done?.Invoke() != false, "An editor settings operation timed out.");
        static bool Sleep() { Thread.Sleep(1); return true; }
    }

    internal static void Run(string root)
    {
        Indentation();
        EditorConfig();
        Fonts();
        Live(Path.Combine(root, "editor-settings"));
        Console.WriteLine("PASS editor settings: indentation precedence (.editorconfig > file > setting), detection, font fallback, live font and line height in editors and diffs");
    }

    private static void Indentation()
    {
        var spaces4 = new IndentSettings(false, 4, true);
        Require(EditorIndentation.Detect("class A\n{\n  int a;\n  void B()\n  {\n    c();\n  }\n}\n") == new DetectedIndent(false, 2),
            "Two-space steps are detected.");
        Require(EditorIndentation.Detect("a:\n\tb\n\tc\n\t\td\n") == new DetectedIndent(true, null), "Tab-led lines are detected as tabs.");
        Require(EditorIndentation.Detect("one\ntwo\n    three\n") is null, "A single indented line is not clear.");
        Require(EditorIndentation.Detect("/**\n * doc\n * more\n */\nx\n") is null, "A block comment's leading stars are not indentation.");
        Require(EditorIndentation.Resolve(spaces4, null, null) == new Indentation(false, 4, 4), "Without a file signal the settings apply.");
        Require(EditorIndentation.Resolve(spaces4, new DetectedIndent(false, 2), null) == new Indentation(false, 2, 2), "A clear file overrides the settings.");
        Require(EditorIndentation.Resolve(spaces4 with { Detect = false }, new DetectedIndent(true, null), null) == new Indentation(false, 4, 4),
            "Detection off ignores the file.");
        Require(EditorIndentation.Resolve(spaces4, new DetectedIndent(true, null), null) == new Indentation(true, 4, 4), "Detected tabs keep the set size.");
        Require(EditorIndentation.Resolve(spaces4, new DetectedIndent(false, 2), new EditorConfigIndent(null, 8, false, null)) == new Indentation(false, 8, 8),
            ".editorconfig indent_size beats the detected size, and tab_width follows it.");
        Require(EditorIndentation.Resolve(spaces4, new DetectedIndent(false, 2), new EditorConfigIndent(true, null, false, null)) == new Indentation(true, 2, 2),
            "Precedence is per key: .editorconfig sets the style, the file still sets the size.");
        Require(EditorIndentation.Resolve(spaces4, null, new EditorConfigIndent(true, null, true, 3)) == new Indentation(true, 3, 3),
            "indent_size = tab takes tab_width.");
        Require(EditorIndentation.Resolve(spaces4, null, new EditorConfigIndent(false, 2, false, 8)) == new Indentation(false, 2, 8),
            "tab_width sets the display width apart from the indent size.");
    }

    private static void EditorConfig()
    {
        Require(EditorIndentation.ConfigPaths("src/app/main.cs").SequenceEqual(["src/app/.editorconfig", "src/.editorconfig", ".editorconfig"]),
            ".editorconfig is looked up from the file's directory to the workspace root.");
        const string top = "root = true\n[*]\nindent_style = space\nindent_size = 4\n[*.{yml,yaml}]\nindent_size = 2\n[Makefile]\nindent_style = tab\n[docs/**.md]\nindent_size = 3\n";
        Require(EditorIndentation.Read([(".editorconfig", top)], "src/a.yml") == new EditorConfigIndent(false, 2, false, null), "Later sections and brace globs win.");
        Require(EditorIndentation.Read([(".editorconfig", top)], "tools/Makefile") == new EditorConfigIndent(true, 4, false, null), "A slash-free glob matches in any directory.");
        Require(EditorIndentation.Read([(".editorconfig", top)], "docs/guide/a.md")?.Size == 3, "A rooted ** glob matches below its directory.");
        Require(EditorIndentation.Read([(".editorconfig", top)], "src/docs/a.md")?.Size == 4, "A glob with a slash is anchored to its .editorconfig.");
        var nested = "[*.cs]\nindent_style = tab\ntab_width = 8\n";
        Require(EditorIndentation.Read([("src/.editorconfig", nested), (".editorconfig", top)], "src/a.cs") == new EditorConfigIndent(true, 4, false, 8),
            "A closer .editorconfig overrides the root's keys and keeps the rest.");
        Require(EditorIndentation.Read([("src/.editorconfig", "root = true\n[*.cs]\nindent_size = 6\n"), (".editorconfig", top)], "src/a.cs") == new EditorConfigIndent(null, 6, false, null),
            "root = true stops the search.");
        Require(EditorIndentation.Read([(".editorconfig", "[*]\ncharset = utf-8\n")], "a.txt") is null, "Files without indentation keys say nothing.");
    }

    private static void Fonts()
    {
        Require(EditorFonts.Resolve("No Such Font Zq7") is null && EditorFonts.Resolve("") is null && EditorFonts.Resolve(EditorFonts.Bundled) is null,
            "An unknown family, empty and the bundled name all resolve to the bundled font.");
        var bundled = EditorFonts.Typeface;
        Require(bundled.FamilyName == EditorFonts.Bundled, "The default editor font is the bundled JetBrains Mono.");
        try
        {
            EditorFonts.Family = "No Such Font Zq7";
            Require(EditorFonts.Typeface == bundled && EditorFonts.FellBack && Math.Abs(EditorFonts.AdvanceRatio - 1) < 1e-9,
                "A missing family falls back to the bundled font with its column width.");
        }
        finally { EditorFonts.Family = ""; }
        Require(!EditorFonts.FellBack && EditorFonts.Monospace()[0] == EditorFonts.Bundled, "The font list leads with the bundled font.");
    }

    private static void Live(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, ".editorconfig"), "root = true\n[*.py]\nindent_size = 3\n");
        File.WriteAllText(Path.Combine(root, "src", "two.cs"), "class A\n{\n  int a;\n  void B()\n  {\n    c();\n  }\n}\n");
        File.WriteAllText(Path.Combine(root, "src", "three.py"), "def a():\n  b()\n  if c:\n    d()\n");
        var host = new LocalProjectAdapter(new ProjectServices(root));
        CodeDocumentView Open(string path)
        {
            var read = Task.Run(() => host.ReadFileAsync(path).AsTask());
            Pump(() => read.IsCompleted);
            return new CodeDocumentView(read.Result, root, host, () => { }, _ => { }, () => { }, error => throw error, find: false);
        }
        var installed = EditorFonts.Monospace().Skip(1).FirstOrDefault(family => EditorFonts.Resolve(family) is not null);
        var diff = new DiffView("@@ -1,2 +1,2 @@\n before\n-old\n+new\n", "a.txt", 800);
        var cs = Open("src/two.cs");
        var py = Open("src/three.py");
        var panel = new StackPanel();
        panel.Children.Add(new Border { Height = 200, Child = cs });
        panel.Children.Add(new Border { Height = 200, Child = py });
        panel.Children.Add(new Border { Height = 200, Child = diff });
        var window = new Window { Width = 900, Height = 700, Content = panel };
        window.Show();
        try
        {
            Pump(() => diff.GetLogicalDescendants().OfType<ScintillaEditor>().Any());
            var diffEditor = diff.GetLogicalDescendants().OfType<ScintillaEditor>().First();
            double Row(ScintillaEditor editor) { Pump(); return editor.LineTop(1) - editor.LineTop(0); }

            // Indentation: the file's two spaces beat the setting; the .editorconfig size beats the file's two.
            cs.IndentSettings = new(false, 4, true);
            Require(cs.Indentation == new Indentation(false, 2, 2) && cs.Editor.IndentSize == 2 && cs.Editor.TabWidth == 2 && !cs.Editor.UseTabs,
                $"A clear two-space file indents by two: {cs.Indentation}.");
            Pump(() => py.Indentation.Size == 3);
            Require(py.Indentation == new Indentation(false, 3, 3), $".editorconfig beats the file's own indentation: {py.Indentation}.");
            cs.IndentSettings = new(true, 8, false);
            Require(cs.Indentation == new Indentation(true, 8, 8) && cs.Editor.UseTabs && cs.Editor.TabWidth == 8, "With detection off a settings change applies live.");
            cs.Editor.Focus(); cs.Editor.SelectRange(0, 0);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t"); window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t"); Pump();
            Require(cs.Editor.Text.StartsWith("\tclass A", StringComparison.Ordinal), "Tab inserts a tab character when indenting with tabs.");
            cs.Editor.Undo();
            cs.IndentSettings = new(false, 4, false);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t"); window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t"); Pump();
            Require(cs.Editor.Text.StartsWith("    class A", StringComparison.Ordinal), "Tab inserts the indent size in spaces.");

            // Line height: every editor and the diff scale their rows together, live.
            var natural = Row(cs.Editor);
            Require(natural > 0 && Math.Abs(Row(diffEditor) - natural) < 0.5, $"The diff draws rows as tall as the file editor ({Row(diffEditor)} vs {natural}).");
            ScintillaEditor.LineSpacing = 1.5;
            Require(Math.Abs(Row(cs.Editor) - natural * 1.5) <= 1 && Math.Abs(Row(diffEditor) - Row(cs.Editor)) < 0.5,
                $"Line height 1.5 reaches the editor and the diff live ({Row(cs.Editor)}, {Row(diffEditor)} from {natural}).");
            ScintillaEditor.LineSpacing = 1;
            Require(Math.Abs(Row(cs.Editor) - natural) < 0.5, "Line height 1.0 restores the natural row.");

            // Font family: a live switch for editors and diffs; an unknown family falls back to the bundled font.
            if (installed is not null)
            {
                EditorFonts.Family = installed; Pump();
                Require(cs.Editor.Typeface.FamilyName.Equals(installed, StringComparison.OrdinalIgnoreCase) && diffEditor.Typeface == cs.Editor.Typeface,
                    $"The chosen family {installed} reaches the editor and the diff live ({cs.Editor.Typeface.FamilyName}, {diffEditor.Typeface.FamilyName}).");
                Require(cs.Editor.Text.StartsWith("    class A", StringComparison.Ordinal), "A font switch keeps the text.");
            }
            else Console.WriteLine("SKIP live font switch: no installed monospace family besides the bundled one");
            EditorFonts.Family = "No Such Font Zq7"; Pump();
            Require(cs.Editor.Typeface.FamilyName == EditorFonts.Bundled && diffEditor.Typeface.FamilyName == EditorFonts.Bundled && Row(cs.Editor) > 0,
                "An unknown family falls back to the bundled font and still lays out.");
        }
        finally
        {
            EditorFonts.Family = ""; ScintillaEditor.LineSpacing = 1;
            window.Close(); cs.Dispose(); py.Dispose(); diff.Dispose(); Dispatcher.UIThread.RunJobs();
        }
    }
}