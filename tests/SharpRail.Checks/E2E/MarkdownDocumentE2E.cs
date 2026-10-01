using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using SharpRail.UI.Rendering;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>
/// The fork's Markdown document cases: frontmatter properties, spec titles and [[links]], the outline, the
/// Split view and find. Properties are read-only here because SharpRail's Markdown tabs have no editable
/// source, so the fork's edit-to-draft assertions have no counterpart.
/// </summary>
internal static class MarkdownDocumentE2E
{
    private static readonly RawInputModifiers Command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static IEnumerable<T> All<T>(Control scope, string name) where T : Control =>
        scope.GetLogicalDescendants().OfType<T>().Where(control => control.Name == name && control.IsEffectivelyVisible);

    private static string TextOf(SelectableTextBlock block) =>
        block.Text ?? string.Concat(block.Inlines?.OfType<Run>().Select(run => run.Text) ?? []);

    private static string Text(Control scope) =>
        string.Join("\n", scope.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(TextOf));

    internal static void Run(string root)
    {
        Properties(root);
        Unreadable(root);
        SpecDocuments(root);
        OrdinaryMarkdown(root);
        Outline(root);
        Split(root);
        Find(root);
    }

    private static E2eWorkspace OpenDocument(string root, string name, string file, string text)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), text);
        var app = new E2eWorkspace(directory);
        app.Open(file, true);
        return app;
    }

    private static void Properties(string root)
    {
        using var app = OpenDocument(root, "frontmatter-properties", "props.md", "---\ntitle: Green talk\ntags:\n  - talk\n  - latex\n---\n\n# Notes\n");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        var block = All<Border>(preview, "FrontmatterProperties").Single();
        Require(Text(All<Control>(block, "FrontmatterValue").First()) == "Green talk", "The title property must show its value.");
        Require(All<Border>(block, "FrontmatterListItem").Count() == 2, "A block list must render one chip per item.");
        Require(preview.GetLogicalDescendants().OfType<Control>().All(control => control.Name != "SpecTitle"),
            "A title without a spec id and type is a property, not a spec title.");
        app.Click(All<Button>(block, "FrontmatterToggle").Single());
        Require(!All<Control>(block, "FrontmatterValue").Any(), "Folding must hide the properties.");
        app.Click(All<Button>(block, "FrontmatterToggle").Single());
        Require(All<Control>(block, "FrontmatterValue").Any(), "Unfolding must show the properties again.");
        Console.WriteLine("PASS fork frontmatter.spec.ts: frontmatter renders as properties, list values as chips, and the block folds away (read-only)");
    }

    private static void Unreadable(string root)
    {
        using var app = OpenDocument(root, "frontmatter-unreadable", "props.md", "---\nnested:\n  child:\n    deep: x\n---\n\n# Notes\n");
        var block = All<Border>(app.Find<MarkdownPreview>("MarkdownPreview"), "FrontmatterProperties").Single();
        Require(Text(block).Contains("deep: x", StringComparison.Ordinal) && !All<Control>(block, "FrontmatterValue").Any(),
            "A block the table cannot speak must show as written instead of guessing.");
        Console.WriteLine("PASS fork frontmatter.spec.ts: a block the editor cannot speak renders read-only instead of guessing");
    }

    private static void SpecDocuments(string root)
    {
        using var app = OpenDocument(root, "spec-documents", "module.md",
            "---\nid: sample-module\ntype: module-design\ntitle: Sample Module\nparent: sample-root\n---\n\n## Goal\n\nPart of [[sample-root]], and [[no-such-node]] is not here.\n");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Require(All<TextBlock>(preview, "SpecTitle").Single().Text == "Sample Module", "A spec must be titled by its frontmatter.");
        app.Click(app.Find<Button>("MarkdownOutlineToggle"));
        Require(All<Button>(app.Find<Border>("MarkdownOutline"), "OutlineEntry").First() is { Content: TextBlock { Text: "Sample Module" } },
            "The outline must open with the spec's title.");
        Button Link(string label) => All<Button>(preview, "SpecLink").Single(link => link.Content is TextBlock text && text.Text == label);
        Until(() => !Link("no-such-node").IsEnabled);
        Require(Link("sample-root").IsEnabled, "A link naming a spec in this workspace must stay enabled.");
        app.Click(Link("sample-root"));
        Until(() => app.Window.Layout.Selected(app.Center)?.Path == "SPEC.md");
        Console.WriteLine("PASS fork spec-documents.spec.ts: a spec is titled by its frontmatter and its [[links]] reach the spec they name");
    }

    private static void OrdinaryMarkdown(string root)
    {
        using var app = OpenDocument(root, "spec-ordinary", "notes.md", "---\ntitle: Not a spec\n---\n\n## Notes\n\nA [[bracketed]] aside.\n");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Require(Text(preview).Contains("[[bracketed]]", StringComparison.Ordinal) && !All<Button>(preview, "SpecLink").Any() &&
            !All<TextBlock>(preview, "SpecTitle").Any(), "Ordinary Markdown must keep [[text]] as written and carry no spec title.");
        Console.WriteLine("PASS fork spec-documents.spec.ts: ordinary markdown keeps its own first heading and leaves [[text]] alone");
    }

    private static void Outline(string root)
    {
        var sections = string.Concat(Enumerable.Range(0, 30).Select(index => $"## Section {index}\n\n" + string.Join("\n\n", Enumerable.Repeat("Filler paragraph.", 6)) + "\n\n"));
        using var app = OpenDocument(root, "markdown-outline", "OUTLINE.md", "# Outline\n\n```md\n# not a heading\n```\n\n" + sections);
        var outline = app.Find<Border>("MarkdownOutline");
        Require(!outline.IsVisible, "The outline must start closed.");
        app.Click(app.Find<Button>("MarkdownOutlineToggle"));
        var entries = All<Button>(outline, "OutlineEntry").ToArray();
        Require(entries.Length == 31 && entries[0].Content is TextBlock { Text: "Outline" } && entries.All(entry => entry.Content is TextBlock { Text: not "not a heading" }),
            "The outline must list every heading outside code fences.");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        app.Click(entries[^1]);
        Until(() => preview.Offset.Y > 0);

        app.Click(app.Find<Button>("MarkdownSourceMode"));
        Require(outline.IsVisible, "The outline must stay in the Source view.");
        var source = app.Find<Control>("MarkdownSource");
        Settle(100);
        app.Click(All<Button>(outline, "OutlineEntry").Last());
        Until(() => source is SharpRail.Scintilla.ScintillaEditor editor ? editor.FirstVisibleLine > 0 : ((ScrollViewer)source).Offset.Y > 0);
        Console.WriteLine("PASS fork outlineTree: the outline sits at the pane's edge in every view and jumps both preview and source");
    }

    private static void Split(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "markdown-split"));
        app.Open("README.md", true);
        app.Click(app.Find<Button>("MarkdownSplitMode"));
        var source = app.Find<Control>("MarkdownSource");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Settle(100);
        Require(source.IsEffectivelyVisible && preview.IsEffectivelyVisible && source.Bounds.Width > 100 && preview.Bounds.Width > 100 &&
            source.TranslatePoint(new Point(), preview)!.Value.X < 0, "Split must show the source beside its preview.");
        app.Click(app.Find<Button>("MarkdownSourceMode"));
        Settle(100);
        Require(source.IsEffectivelyVisible && !preview.IsEffectivelyVisible, "Source must show only the buffer.");
        app.Click(app.Find<Button>("MarkdownPreviewMode"));
        Settle(100);
        Require(!source.IsEffectivelyVisible && preview.IsEffectivelyVisible, "Preview must show only the rendering.");
        Console.WriteLine("PASS fork EmbeddedSplit: Markdown gets a Split view, the buffer and its preview at once");
    }

    private static void Find(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "markdown-find"));
        app.Open("README.md", true);
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        var heading = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().First(block => TextOf(block) == "sample-project");
        app.Click(heading);
        Until(() => heading.IsFocused);
        app.Window.KeyPress(Key.F, Command, PhysicalKey.F, "f");
        app.Window.KeyRelease(Key.F, Command, PhysicalKey.F, "f");
        var input = app.Find<TextBox>("FindInput");
        Until(() => input.IsFocused && app.Find<Border>("FindBar").IsVisible);
        input.Text = "sample-project";
        var count = app.Find<TextBlock>("FindCount");
        Until(() => count.Text == "1/1");
        Require(heading.SelectedText == "sample-project", "The current match must be selected.");
        app.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Until(() => count.Text == "1/1");
        input.Text = "no-such-text-anywhere";
        Until(() => count.Text == "0/0" && input.BorderBrush == Ui.Danger);
        app.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !app.Find<Border>("FindBar").IsVisible);
        Console.WriteLine("PASS fork find.spec.ts: Mod+F over a preview opens the find bar and selects the matches (current match only; no all-match paint)");
    }
}
