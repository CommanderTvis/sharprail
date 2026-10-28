using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>
/// The fork's Markdown document cases: frontmatter properties, spec titles and [[links]], the outline, the
/// Split view and find. Preview properties and source share the normal file editor draft.
/// </summary>
internal static class MarkdownDocumentE2E
{
    private static readonly RawInputModifiers Command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static IEnumerable<T> All<T>(Control scope, string name) where T : Control =>
        scope.GetLogicalDescendants().OfType<T>().Where(control => control.Name == name && control.IsEffectivelyVisible);

    private static string TextOf(SelectableTextBlock block) =>
        block.Text ?? string.Concat(block.Inlines?.OfType<Run>().Select(run => run.Text) ?? []);

    private static string Text(Control scope) =>
        string.Join("\n", (scope is MarkdownPreview preview ? preview.TextBlocks() : scope.GetLogicalDescendants().OfType<SelectableTextBlock>()).Select(TextOf));

    internal static void Run(string root)
    {
        FrontmatterEditing(root);
        SpecDocuments(root);
        OrdinaryMarkdown(root);
        DiffLinks(root);
        Outline(root);
        Split(root);
        CursorFollow(root);
        Find(root);
        MarkdownFindE2E.Run(root);
        LiveEdits(root);
        Images(root);
    }

    internal static void FrontmatterEditing(string root)
    {
        FrontmatterRoundTrips();
        Properties(root);
        PropertyEdits(root);
        Unreadable(root);
    }

    // An agent editing the open file reloads its view; the reader keeps their place and mode, as in the reference (#608).
    private static void LiveEdits(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "markdown-live-edits"));
        app.Open("LARGE.md", true);
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Until(() => preview.Extent.Height > preview.Viewport.Height + 600);
        preview.Offset = new Vector(0, 500);
        Settle(100);
        File.AppendAllText(Path.Combine(app.Root, "LARGE.md"), "\n\nAn appended paragraph.\n");
        Until(() => app.Find<MarkdownPreview>("MarkdownPreview") is var reloaded && !ReferenceEquals(reloaded, preview) &&
            Text(reloaded).Contains("An appended paragraph.", StringComparison.Ordinal));
        Settle(300);
        var after = app.Find<MarkdownPreview>("MarkdownPreview");
        Require(Math.Abs(after.Offset.Y - 500) < 1, $"A live edit keeps the reader's scroll position, got {after.Offset.Y}.");
        Console.WriteLine("PASS a live edit of an open Markdown file keeps its scroll position");
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

    private static void DiffLinks(string root)
    {
        using var app = OpenDocument(root, "markdown-diff-links", "diff.md",
            "---\nid: diff-links\ntype: module-design\n---\n\n<del>Deleted [contribution](target.md) and [[missing-spec]]</del>\n\n<ins>Added [license](target.md)</ins>\n\n~~[Struck link](target.md)~~\n\n[Plain link](target.md)\n");
        var labels = app.Find<MarkdownPreview>("MarkdownPreview").GetLogicalDescendants().OfType<MarkdownLink>()
            .Select(link => (TextBlock)link.Content!).ToArray();
        foreach (var label in labels.Where(label => label.Text is "contribution" or "missing-spec"))
            Require(label.TextDecorations == Avalonia.Media.TextDecorations.Strikethrough && label.Foreground == Ui.Danger && label.Background is not null,
                "Deleted ordinary and spec links must retain the surrounding diff's strike, color and background.");
        Require(labels.Length == 5 && labels.Single(label => label.Text == "license").Foreground == Ui.Success &&
            labels.Single(label => label.Text == "license").TextDecorations is null &&
            labels.Single(label => label.Text == "Struck link").TextDecorations == Avalonia.Media.TextDecorations.Strikethrough &&
            labels.Single(label => label.Text == "Plain link").TextDecorations is null,
            "Inserted, struck and ordinary links must retain their respective decorations.");
        Console.WriteLine("PASS Markdown diff links inherit deletion and insertion styling, including spec links");
    }

    private static void Properties(string root)
    {
        using var app = OpenDocument(root, "frontmatter-properties", "props.md", "---\ntitle: Green talk\ntags:\n  - talk\n  - latex\n---\n\n# Notes\n");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        var block = All<Border>(preview, "FrontmatterProperties").Single();
        if (SharpRail.Scintilla.ScintillaEditor.IsSupported)
            Require(All<FrontmatterField>(block, "FrontmatterValue").First().GetLogicalDescendants().OfType<AutoCompleteBox>().Single().Text == "Green talk", "The title property must show its value.");
        else
            Require(Text(block).Contains("Green talk", StringComparison.Ordinal) && !All<FrontmatterField>(block, "FrontmatterValue").Any(),
                "Without a source editor, properties must show their values read-only.");
        Require(All<Control>(block, "FrontmatterListItem").Count() == 2, "A block list must render one chip per item.");
        Require(preview.GetLogicalDescendants().OfType<Control>().All(control => control.Name != "SpecTitle"),
            "A title without a spec id and type is a property, not a spec title.");
        app.Click(All<Button>(block, "FrontmatterToggle").Single());
        Require(!All<Control>(block, "FrontmatterValue").Any(), "Folding must hide the properties.");
        app.Click(All<Button>(block, "FrontmatterToggle").Single());
        Require(All<Control>(block, "FrontmatterValue").Any(), "Unfolding must show the properties again.");
        Console.WriteLine("PASS fork frontmatter.spec.ts: frontmatter renders as properties, list values as chips, and the block folds away");
    }

    private static void FrontmatterRoundTrips()
    {
        const string body = "\r\n# Body\n\n---\nLeave this exactly.\r\n";
        var properties = new FrontmatterProperty[]
        {
            new("title", Text: "quotes \" and slash \\ and newline\n東京"),
            new("tags", Items: ["one, two", "[brackets]", "a: b", "true"]),
            new("empty-list", Items: []), new("empty-map", Entries: []),
            new("map", Entries: [new("key", ""), new("other", "[not a list]")])
        };
        var edited = Frontmatter.WithProperties("---\r\ntitle: Before\r\n...\r\n" + body, properties);
        var parsed = Frontmatter.Parse(edited)!;
        Require(parsed.Readable && parsed.Properties.Count == properties.Length && edited.EndsWith(body, StringComparison.Ordinal),
            "A properties edit preserves the body verbatim, including mixed line endings.");
        for (var index = 0; index < properties.Length; index++)
        {
            var before = properties[index]; var after = parsed.Properties[index];
            Require(before.Text == after.Text && (before.Items ?? []).SequenceEqual(after.Items ?? []) &&
                (before.Entries ?? []).SequenceEqual(after.Entries ?? []) && Frontmatter.Shape(before) == Frontmatter.Shape(after),
                "Values and empty shapes must round-trip without changing their meaning.");
        }
        Require(!Frontmatter.ValidKey("bad:key") && !Frontmatter.ValidKey("") && !Frontmatter.ValidKey(" bad") && Frontmatter.ValidKey("valid key-1"),
            "Property names must use the key grammar the preview can read back.");
        var list = new FrontmatterProperty("pairs", Items: ["a: one", "b: two"]);
        Require(Frontmatter.Convert(Frontmatter.Convert(list, "Mapping"), "Sequence").Items!.SequenceEqual(list.Items!), "Mapping/sequence conversion must preserve unique pairs.");
        Require(Frontmatter.Convert(new("duplicate", Items: ["a: one", "a: two"]), "Mapping").Entries!.Select(pair => pair.Key).SequenceEqual(["1", "2"]), "Duplicate pair keys use ordinal keys.");
        foreach (var raw in new[] { "nested:\n  child:\n    deep: x", "title: |\n  words", "title: &anchor words", "title: *anchor", "tags: [*anchor]", "tags:\n  - [nested]", "map:\n  list: [nested]", "title: one\ntitle: two" })
        {
            var content = "---\n" + raw + "\n---\n# Body";
            Require(Frontmatter.Parse(content) is { Readable: false } && Frontmatter.WithProperties(content, properties) == content,
                "Unsupported YAML must never be rewritten by the properties editor.");
        }
        Console.WriteLine("PASS frontmatter serialization: exact body, quoting, empty shapes, conversions and unsupported YAML");
    }

    private static void PropertyEdits(string root)
    {
        const string original = "---\ntitle: Green talk\ntags: [talk, latex]\ntype: module-design\n---\n\n# Notes\n";
        using var app = OpenDocument(root, "frontmatter-editing", "props.md", original);
        if (!SharpRail.Scintilla.ScintillaEditor.IsSupported)
        {
            var preview = app.Find<MarkdownPreview>("MarkdownPreview");
            Require(!preview.GetLogicalDescendants().OfType<FrontmatterEditor>().Any() &&
                Text(preview).Contains("Green talk", StringComparison.Ordinal) &&
                File.ReadAllText(Path.Combine(app.Root, "props.md")) == original,
                "Platforms without a source editor keep frontmatter read-only and leave the original file unchanged.");
            Console.WriteLine("PASS frontmatter read-only fallback without a native source editor; native draft/undo/save checks require a supported editor platform");
            return;
        }
        FrontmatterEditor Editor() => app.Find<MarkdownPreview>("MarkdownPreview").GetLogicalDescendants().OfType<FrontmatterEditor>().Single();
        void Edit(string name, string value, int index = 0, Key key = Key.Enter)
        {
            var field = All<FrontmatterField>(Editor(), name).ElementAt(index);
            var input = field.GetVisualDescendants().OfType<TextBox>().First();
            input.Focus(); input.SelectAll();
            app.Window.KeyTextInput(value);
            app.Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            app.Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            Settle(250);
            if (key == Key.Enter && name == "FrontmatterValue")
                Require(!input.IsFocused, "Enter commits and blurs a scalar field, as in the fork.");
        }
        string Source() => app.Window.GetLogicalDescendants().OfType<MarkdownDocumentView>().Single().Source!
            .GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Single().Text;
        Edit("FrontmatterValue", "Amber talk");
        Require(Source().Contains("title: Amber talk", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(app.Root, "props.md")) == original,
            "A preview edit changes the unsaved source draft without writing disk.");
        var source = app.Window.GetLogicalDescendants().OfType<MarkdownDocumentView>().Single().Source!
            .GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Single();
        Require(source.IsModified, "Preview edits make the tab dirty.");
        source.Undo(); Settle(250);
        Require(source.Text == original && !source.IsModified, "Undo restores the original save point.");
        source.Redo(); Settle(250);
        Edit("FrontmatterValue", "Cancelled", key: Key.Escape);
        Require(Source().Contains("title: Amber talk", StringComparison.Ordinal), "Escape cancels a field draft.");
        var activeField = All<FrontmatterField>(Editor(), "FrontmatterValue").First().GetVisualDescendants().OfType<TextBox>().First();
        activeField.Focus(); activeField.SelectAll(); app.Window.KeyTextInput("Pending blur");
        var nextField = All<FrontmatterField>(Editor(), "FrontmatterValue").Last().GetVisualDescendants().OfType<TextBox>().First();
        nextField.Focus(); nextField.SelectAll(); app.Window.KeyTextInput("Pending type draft");
        Settle(250);
        Require(nextField.IsFocused && Source().Contains("title: Pending blur", StringComparison.Ordinal),
            $"Committing on blur must preserve focus on the next field through preview refresh: focus={nextField.IsFocused}, source={Source()}.");
        Require(nextField.Text == "Pending type draft" && Frontmatter.Parse(Source())!.Properties.Single(property => property.Key == "type").Text == "module-design",
            "A pending field draft must survive preview reattachment without being committed by it.");
        app.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        app.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Edit("FrontmatterValue", "Amber talk");
        var blurField = All<FrontmatterField>(Editor(), "FrontmatterValue").First().GetVisualDescendants().OfType<TextBox>().First();
        blurField.Focus(); blurField.SelectAll(); app.Window.KeyTextInput("Blur committed");
        app.Click(All<Button>(app.Find<MarkdownPreview>("MarkdownPreview"), "FrontmatterToggle").Single());
        Settle(250);
        Require(Source().Contains("title: Blur committed", StringComparison.Ordinal) && !All<FrontmatterField>(Editor(), "FrontmatterValue").Any(),
            "Blur commits and preview refresh retains folded properties.");
        app.Click(All<Button>(app.Find<MarkdownPreview>("MarkdownPreview"), "FrontmatterToggle").Single());
        Edit("FrontmatterValue", "Amber talk");
        Edit("FrontmatterValue", "😀");
        Edit("FrontmatterValue", "😁");
        Require(Frontmatter.Parse(Source())!.Properties[0].Text == "😁", "Replacing emoji must keep UTF-16/UTF-8 boundaries intact.");
        Edit("FrontmatterValue", "Amber talk");
        Edit("FrontmatterListAdd", "beamer");
        Require(All<FrontmatterChip>(Editor(), "FrontmatterListItem").Count() == 3 && All<FrontmatterField>(Editor(), "FrontmatterListAdd").Single().IsKeyboardFocusWithin,
            "A list chip can be added while retaining focus to add the next chip.");
        Edit("FrontmatterListAdd", "beamer");
        Require(All<FrontmatterChip>(Editor(), "FrontmatterListItem").Count() == 4, "Duplicate chips retain separate occurrences.");
        app.Click(All<FrontmatterChip>(Editor(), "FrontmatterListItem").Last().GetLogicalDescendants().OfType<Button>().Single()); Settle(250);
        app.Click(All<FrontmatterChip>(Editor(), "FrontmatterListItem").First().GetLogicalDescendants().OfType<Button>().Single()); Settle(250);
        Require(All<FrontmatterChip>(Editor(), "FrontmatterListItem").Count() == 2, "A list chip can be removed.");
        Edit("FrontmatterKey", "type");
        Require(Source().Contains("title: Amber talk", StringComparison.Ordinal), "Duplicate property keys are refused.");
        Edit("FrontmatterKey", "bad:key");
        Require(Source().Contains("title: Amber talk", StringComparison.Ordinal), "An invalid key rename must keep the original property.");
        Edit("FrontmatterKey", "heading");
        Require(Source().Contains("heading: Amber talk", StringComparison.Ordinal), "Properties can be renamed.");
        var type = All<FrontmatterField>(Editor(), "FrontmatterValue").Last().GetLogicalDescendants().OfType<AutoCompleteBox>().Single();
        Require(type.ItemsSource!.Cast<string>().Contains("task-spec"), "Type values offer the spec vocabulary without constraining text.");
        void ConvertTo(string shape)
        {
            app.Click(All<Button>(Editor(), "FrontmatterType").First());
            Settle();
            var menu = All<Button>(Editor(), "FrontmatterType").First().ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "FrontmatterType" + shape);
            menu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            All<Button>(Editor(), "FrontmatterType").FirstOrDefault()?.ContextMenu?.Close();
            Settle(250);
        }
        ConvertTo("Sequence");
        Require(Frontmatter.Parse(Source())!.Properties[0].Items!.Single() == "Amber talk", "Text converts to one sequence item.");
        ConvertTo("Mapping");
        Require(Frontmatter.Parse(Source())!.Properties[0].Entries!.Single().Key == "1", "A sequence converts to ordinal mapping keys.");
        Edit("FrontmatterMapValue", "Changed mapping");
        app.Click(All<Button>(Editor(), "FrontmatterMapAdd").Single()); Settle(250);
        Require(All<FrontmatterField>(Editor(), "FrontmatterMapKey").Count() == 2, "Mapping entries can be added.");
        Edit("FrontmatterMapKey", "renamed");
        Require(Source().Contains("renamed: Changed mapping", StringComparison.Ordinal), "Mapping keys and values can be edited.");
        ConvertTo("Text");
        Require(Frontmatter.Parse(Source())!.Properties[0].Text!.Contains("renamed: Changed mapping", StringComparison.Ordinal), "Mapping-to-text conversion exposes the structure.");
        app.Click(All<Button>(Editor(), "FrontmatterAdd").Single()); Settle(250);
        Require(Frontmatter.Parse(Source())!.Properties.Last().Key == "property", "Add property creates a unique empty field.");
        app.Click(All<Button>(Editor(), "Remove").Last()); Settle(250);
        app.Window.KeyPress(Key.S, Command, PhysicalKey.S, "s");
        app.Window.KeyRelease(Key.S, Command, PhysicalKey.S, "s");
        Until(() => File.ReadAllText(Path.Combine(app.Root, "props.md")) == Source());
        Require(!source.IsModified && Source().EndsWith("\n\n# Notes\n", StringComparison.Ordinal), "Save writes the preview draft and preserves the prose.");
        Edit("FrontmatterValue", "Unsaved conflict");
        File.WriteAllText(Path.Combine(app.Root, "props.md"), "# External replacement\n");
        Settle(250);
        var save = ((SharpRail.UI.Editor.CodeDocumentView)app.Window.GetLogicalDescendants().OfType<MarkdownDocumentView>().Single().Source!).SaveAsync();
        Until(() => save.IsCompleted);
        Require(source.IsModified && Source().Contains("Unsaved conflict", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(app.Root, "props.md")) == "# External replacement\n",
            "A preview draft keeps the normal save conflict check and never overwrites external edits.");
        ((SharpRail.UI.Editor.CodeDocumentView)app.Window.GetLogicalDescendants().OfType<MarkdownDocumentView>().Single().Source!).Discard();
        Require(!source.IsModified, "The conflict fixture must close without leaving an unsaved-buffer dialog alive.");
        Console.WriteLine("PASS fork frontmatter.spec.ts: editable drafts, undo, cancel, list chips, rename, type suggestions, shape conversion, mapping entries, add/remove and save");
    }

    private static void Unreadable(string root)
    {
        using var app = OpenDocument(root, "frontmatter-unreadable", "props.md", "---\nnested:\n  child:\n    deep: x\n---\n\n# Notes\n");
        var block = All<Border>(app.Find<MarkdownPreview>("MarkdownPreview"), "FrontmatterProperties").Single();
        Require(Text(block).Contains("deep: x", StringComparison.Ordinal) && !All<Control>(block, "FrontmatterValue").Any(),
            "A block the table cannot speak must show as written instead of guessing.");
        Console.WriteLine("PASS fork frontmatter.spec.ts: a block the editor cannot speak renders read-only instead of guessing");
    }

    internal static void SpecDocuments(string root)
    {
        using var app = OpenDocument(root, "spec-documents", "module.md",
            "---\nid: sample-module\ntype: module-design\ntitle: Sample Module\nparent: sample-root\n---\n\n## Goal\n\nPart of [[sample-root]], and [[no-such-node]] is not here.\n");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Require(All<TextBlock>(preview, "SpecTitle").Single().Text == "Sample Module", "A spec must be titled by its frontmatter.");
        app.Click(app.Find<Button>("MarkdownOutlineToggle"));
        var outlineEntries = All<Button>(app.Find<Border>("MarkdownOutline"), "OutlineEntry").ToArray();
        Require(outlineEntries[0] is { Content: TextBlock { Text: "Sample Module", FontWeight: Avalonia.Media.FontWeight.SemiBold } title } && title.Foreground == Ui.TextBrush,
            "The outline must open with the spec's title, semibold and not muted.");
        Require(outlineEntries[1] is { Content: TextBlock { Text: "Goal" }, Padding.Left: var goalIndent } && goalIndent == outlineEntries[0].Padding.Left + 12 * 2,
            "Headings must nest one level under the spec title.");
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
        if (source is SharpRail.Scintilla.ScintillaEditor editor)
        {
            editor.Focus();
            editor.SelectAll();
            app.Window.KeyTextInput("# Edited in split view\n\nAn unsaved paragraph.\n");
            Until(() => Text(app.Find<MarkdownPreview>("MarkdownPreview")).Contains("An unsaved paragraph.", StringComparison.Ordinal));
            Require(!File.ReadAllText(Path.Combine(app.Root, "README.md")).Contains("An unsaved paragraph.", StringComparison.Ordinal),
                "Editing must keep the draft in memory until Save.");
            var view = app.Window.GetLogicalDescendants().OfType<MarkdownDocumentView>().Single();
            var refresh = view.RefreshPreviewAsync(editor.Text, new MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null),
                _ => null, (_, _) => { }, 20, 60, true));
            Until(() => refresh.IsCompleted);
            Require(refresh.IsCompletedSuccessfully && editor.IsModified && ReferenceEquals(source, app.Find<Control>("MarkdownSource")) &&
                view.Preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(block => block.FontSize == 20),
                "Refreshing appearance must update the preview and retain the dirty source buffer.");
            app.Window.KeyPress(Key.S, Command, PhysicalKey.S, "s");
            app.Window.KeyRelease(Key.S, Command, PhysicalKey.S, "s");
            Until(() => File.ReadAllText(Path.Combine(app.Root, "README.md")).Contains("An unsaved paragraph.", StringComparison.Ordinal));
            preview = app.Find<MarkdownPreview>("MarkdownPreview");
        }
        app.Click(app.Find<Button>("MarkdownSourceMode"));
        Settle(100);
        Require(source.IsEffectivelyVisible && !preview.IsEffectivelyVisible, "Source must show only the buffer.");
        app.Click(app.Find<Button>("MarkdownPreviewMode"));
        Settle(100);
        Require(!source.IsEffectivelyVisible && preview.IsEffectivelyVisible, "Preview must show only the rendering.");
        if (source is SharpRail.Scintilla.ScintillaEditor editable)
        {
            app.Click(app.Find<Button>("MarkdownSplitMode"));
            editable.Focus();
            app.Window.KeyTextInput("Unsaved before closing.\n");
            var group = app.Window.Layout.View.FocusedCenter;
            app.Window.Layout.Close(group, "markdown:README.md");
            Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any());
            var prompt = app.Window.OwnedWindows.OfType<DialogWindow>().Single();
            var cancel = prompt.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" });
            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => !prompt.IsVisible);
            Require(app.Tabs.Any(tab => tab.Path == "README.md") && editable.Text.Contains("Unsaved before closing.", StringComparison.Ordinal),
                "Cancelling a Markdown close must keep its unsaved buffer.");
            app.Window.Layout.Close(group, "markdown:README.md");
            Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any());
            prompt = app.Window.OwnedWindows.OfType<DialogWindow>().Single();
            prompt.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Don't Save" })
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Until(() => !app.Tabs.Any(tab => tab.Path == "README.md"));
            Require(!File.ReadAllText(Path.Combine(app.Root, "README.md")).Contains("Unsaved before closing.", StringComparison.Ordinal),
                "Discarding Markdown edits must leave the saved file intact.");
        }
        Console.WriteLine("PASS fork EmbeddedSplit: Markdown gets a Split view, the buffer and its preview at once");
    }

    private static void CursorFollow(string root)
    {
        if (!SharpRail.Scintilla.ScintillaEditor.IsSupported) return;
        var text = "---\ntitle: Cursor following\n---\n\n" + string.Concat(Enumerable.Range(0, 45)
            .Select(index => $"## Section {index}\n\nParagraph {index} with Unicode café 👋.\n\n- Item {index}\n\n")) +
            "~~~cs\nvar final = 42;\n~~~\n";
        using var app = OpenDocument(root, "markdown-cursor-follow", "follow.md", text);
        app.Click(app.Find<Button>("MarkdownSplitMode"));
        var view = app.Window.GetLogicalDescendants().OfType<MarkdownDocumentView>().Single();
        var editor = app.Find<SharpRail.Scintilla.ScintillaEditor>("MarkdownSource");
        var toggle = app.Find<Avalonia.Controls.Primitives.ToggleButton>("MarkdownFollowCursor");
        Require(toggle.IsChecked == true && toggle.IsEffectivelyVisible, "Split must offer cursor following enabled by default.");
        void Move(string source)
        {
            editor.Focus();
            editor.SelectRange(editor.Text.IndexOf(source, StringComparison.Ordinal), 0);
            Settle(100);
        }
        bool Visible(string value)
        {
            var block = view.Preview.GetLogicalDescendants().OfType<SelectableTextBlock>()
                .First(block => TextOf(block).Contains(value, StringComparison.Ordinal));
            return block.TranslatePoint(default, view.Preview) is { } point && point.Y >= -1 &&
                point.Y + block.Bounds.Height <= view.Preview.Viewport.Height + 1;
        }
        Move("Paragraph 40");
        Require(Visible("Paragraph 40") && view.Preview.Offset.Y > 0, "The preview must reveal the paragraph at the source cursor.");
        var offset = view.Preview.Offset;
        editor.SelectRange(editor.Text.IndexOf("Paragraph 40", StringComparison.Ordinal) + 5, 0);
        Settle(100);
        Require(view.Preview.Offset == offset, "Moving within a visible paragraph must not scroll the preview.");
        Move("## Section 3");
        Require(Visible("Section 3"), "Moving backwards must reveal the matching heading.");
        Move("Item 35");
        Require(Visible("Item 35"), "Following must use nested list-item source locations.");
        Move("var final");
        Require(Visible("var final"), "Following must reveal a fenced code block.");
        editor.Focus();
        SelectToDocumentStart(app.Window);
        Settle(100);
        Require(editor.CaretLine == 1 && editor.Selection.EndLine > 100 && view.Preview.Offset.Y < 100,
            "A backwards keyboard selection must follow its active caret, not its lower selection endpoint.");
        Move("title:");
        Require(view.Preview.Offset.Y < 100, "Frontmatter must follow to the properties near the top.");
        Move("Paragraph 40");
        editor.Focus();
        app.Window.KeyTextInput("Edited ");
        Until(() => Text(view.Preview).Contains("Edited Paragraph 40", StringComparison.Ordinal));
        Settle(100);
        Require(Visible("Edited Paragraph 40"), "Replacing the preview after typing must keep the edited block visible.");
        app.Click(toggle);
        Require(toggle.IsChecked == false, "The toolbar button must disable following.");
        view.Preview.Offset = new Vector(0, 250);
        Settle(100);
        offset = view.Preview.Offset;
        Move("Paragraph 1 ");
        Require(view.Preview.Offset == offset, "Disabled following must leave the preview at its independent scroll position.");
        editor.Focus();
        app.Window.KeyTextInput("Independent ");
        Until(() => Text(view.Preview).Contains("Independent Paragraph 1 ", StringComparison.Ordinal));
        Settle(100);
        Require(view.Preview.Offset == offset && !view.State.FollowCursor, "Disabled following and scroll position must survive preview replacement.");
        app.Click(app.Find<Button>("MarkdownPreviewMode"));
        Require(!toggle.IsVisible, "Following is only a Split control.");
        app.Click(app.Find<Button>("MarkdownSplitMode"));
        Require(toggle.IsChecked == false, "Mode changes must retain disabled following.");
        var state = view.State;
        app.Click(toggle);
        Settle(100);
        Require(Visible("Independent Paragraph 1 "), "Re-enabling following must immediately reveal the current cursor block.");
        var selection = editor.Selection;
        view.Preview.Offset = new Vector(0, 900);
        Settle(100);
        Require(editor.Selection == selection && view.Preview.Offset.Y > 800, "Manual preview scrolling must neither move the source cursor nor snap back.");
        view.Restore(state);
        Settle(100);
        Require(toggle.IsChecked == false && view.Preview.Offset == state.Preview, "Restoring view state must retain the toggle and independent position.");
        Console.WriteLine("PASS Markdown split follows the source cursor, preserves visible blocks, and retains its independent-scroll toggle");
    }

    private static void Images(string root)
    {
        var directory = Path.Combine(root, "markdown-images");
        Directory.CreateDirectory(directory);
        using var bitmap = new SkiaSharp.SKBitmap(1600, 900);
        bitmap.Erase(SkiaSharp.SKColors.Lime);
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(directory, "wide.png"), encoded.ToArray());
        using var app = OpenDocument(root, "markdown-images", "images.md", "# Image\n\n![Wide chart](wide.png)\n\nAfter the image.\n\n| Screenshot |\n| --- |\n| <img src=\"wide.png\" width=\"240\" /> |\n");
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        Image? image = null;
        Until(() => (image = preview.GetLogicalDescendants().OfType<Image>().FirstOrDefault()) is { Bounds.Height: > 100 });
        var loaded = image!;
        var paragraph = loaded.GetLogicalAncestors().OfType<SelectableTextBlock>().First();
        Require(loaded.Bounds.Width <= paragraph.Bounds.Width + 1, "A wide image must fit the Markdown column.");
        Require(paragraph.Bounds.Height >= loaded.Bounds.Height, "The image paragraph must reserve the full image height.");
        Require(Math.Abs(loaded.Bounds.Width / loaded.Bounds.Height - 1600d / 900) < .02, "The whole image must keep its aspect ratio.");
        Image? htmlImage = null;
        Until(() => (htmlImage = preview.GetLogicalDescendants().OfType<Image>().Skip(1).FirstOrDefault()) is { Bounds.Height: > 30 });
        var loadedHtml = htmlImage!;
        var htmlParagraph = loadedHtml.GetLogicalAncestors().OfType<SelectableTextBlock>().First();
        Require(htmlParagraph.Bounds.Height >= loadedHtml.Bounds.Height, "A table paragraph must reserve the full HTML image height.");
        Require(Math.Abs(loadedHtml.Bounds.Width / loadedHtml.Bounds.Height - 1600d / 900) < .02, "An HTML table image must keep its aspect ratio.");
        Console.WriteLine("PASS Markdown images fit the column and reserve their full height");
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