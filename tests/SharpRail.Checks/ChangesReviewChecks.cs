using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class ChangesReviewChecks
{
    internal static void Run(string root)
    {
        Presentation();
        ContentIdentity();
        Retention();
        var original = string.Join('\n', Enumerable.Range(1, 35).Select(index => "line " + index)) + "\n";
        var changed = original.Replace("line 2\n", "first change " + new string('x', 200) + "\n", StringComparison.Ordinal)
            .Replace("line 30\n", "last change\n", StringComparison.Ordinal);
        var repository = E2E.IsolatedGit.Repository(Path.Combine(root, "continuous-review"),
            ("a.txt", original), ("b.txt", "before\n"), ("large.lock", "base\n"));
        using var app = new E2E.E2eWorkspace(repository, openFiles: false);
        app.Window.Activate();
        E2E.IsolatedGit.Run(repository, "add", "-A");
        E2E.IsolatedGit.Run(repository, "commit", "-m", "review fixture");
        File.WriteAllText(Path.Combine(repository, "a.txt"), changed);
        File.WriteAllText(Path.Combine(repository, "b.txt"), "after\n");
        File.WriteAllText(Path.Combine(repository, "large.lock"), "generated\n");
        ShowChanges(app);
        ClickRow(app, "a.txt");
        ChangesReviewPane? Review() => app.Window.GetLogicalDescendants().OfType<ChangesReviewPane>().FirstOrDefault(pane => pane.IsEffectivelyVisible);
        T[] Parts<T>(string name) where T : Control => Review()?.GetLogicalDescendants().OfType<T>().Where(control => control.Name == name).ToArray() ?? [];
        Button Action(string name) => Parts<Button>(name).Single();
        string Progress() => Parts<TextBlock>("ReviewProgress").SingleOrDefault()?.Text ?? "";
        Button[] KeepButtons() => Parts<Border>("ReviewSection").SingleOrDefault(section => Equals(section.Tag, "a.txt"))?
            .GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "DiffKeepBlock").ToArray() ?? [];
        Until(() => Review() is not null && KeepButtons().Count(button => button.IsEffectivelyVisible) == 2);
        Require(app.Tabs.Single(tab => tab.Kind == "changes").Preview && !app.Tabs.Any(tab => tab.Kind == "diff"),
            "A single Changes click must browse the shared review tab.");
        Require(Parts<Border>("ReviewSection").Length == 3 && Parts<Border>("ReviewSection").Single(section => Equals(section.Tag, "large.lock"))
            .GetLogicalDescendants().OfType<DiffView>().Count() == 0, "Generated files must start collapsed without reading their diff.");

        app.Click(KeepButtons().Where(button => button.IsEffectivelyVisible).First());
        Until(() => app.Tabs.Single(tab => tab.Kind == "changes").Preview == false &&
            Parts<TextBlock>("ReviewProgress").Any(text => text.Text == "0 of 3 reviewed"));
        Until(() => KeepButtons().Any(button => Equals(button.Content, "Keep") && button.IsEffectivelyVisible));
        app.Click(KeepButtons().First(button => Equals(button.Content, "Keep") && button.IsEffectivelyVisible));
        Until(() => Progress() == "1 of 3 reviewed");
        Require(Frames(app).Single(frame => RowPath(frame) == "a.txt").GetLogicalDescendants().OfType<TextBlock>()
            .Any(text => text.Name == "ChangeViewed" && text.IsVisible), "Viewed progress must appear in the Changes navigator.");
        var viewed = Parts<CheckBox>("ReviewViewed").Single(box => Equals(box.Tag, "a.txt"));
        app.Click(viewed);
        Until(() => Progress() == "0 of 3 reviewed");
        app.Click(Action("ReviewSingle"));
        Until(() => Parts<Border>("ReviewSection").Length == 1 && KeepButtons().Count(button => Equals(button.Content, "Kept")) == 2);
        Require(Progress() == "0 of 3 reviewed", "Switching presentation must preserve an explicit unviewed choice.");
        app.Click(Action("ReviewNext"));
        Until(() => Parts<Border>("ReviewSection").SingleOrDefault()?.Tag is "b.txt");
        ClickRow(app, "a.txt");
        Until(() => Parts<Border>("ReviewSection").SingleOrDefault()?.Tag is "a.txt");
        app.Click(Action("ReviewStacked"));
        Until(() => Parts<Border>("ReviewSection").Length == 3);
        app.Click(Action("ReviewNextUnviewed"));
        Until(() => Frames(app).Single(frame => RowPath(frame) == "b.txt").Classes.Contains("active"));
        app.Click(Action("ReviewMarkAll"));
        Until(() => Progress() == "3 of 3 reviewed");
        ClickRow(app, "a.txt");
        Until(() => KeepButtons().Length == 2 && Parts<Button>("ReviewFile").Any(button => Equals(button.Tag, "a.txt") && button.IsFocused));
        app.Window.KeyPress(Key.V, RawInputModifiers.None, PhysicalKey.V, null);
        app.Window.KeyRelease(Key.V, RawInputModifiers.None, PhysicalKey.V, null);
        Until(() => Progress() == "2 of 3 reviewed");
        File.WriteAllText(Path.Combine(repository, "a.txt"), changed.Replace("first change", "changed again", StringComparison.Ordinal));
        Until(() => Review()!.GetLogicalDescendants().OfType<TextBlock>().Any(text => Equals(text.Tag, "a.txt") && text.Text == "1/2 kept"));
        var revert = Parts<Border>("ReviewSection").Single(section => Equals(section.Tag, "a.txt"))
            .GetLogicalDescendants().OfType<Button>().First(button => button.Name == "DiffRevertBlock" && button.IsEffectivelyVisible);
        app.Click(revert);
        Until(() => !File.ReadAllText(Path.Combine(repository, "a.txt")).Contains("changed again", StringComparison.Ordinal) &&
            app.Window.Toasts.Items.Any(toast => toast.Action?.Label == "Undo"));
        app.Window.Toasts.Items.Last(toast => toast.Action?.Label == "Undo").Action!.Invoke();
        Until(() => File.ReadAllText(Path.Combine(repository, "a.txt")).Contains("changed again", StringComparison.Ordinal));
        Until(() => KeepButtons().Length == 2);
        app.Window.Height = 400;
        Settle();
        var scroller = Parts<ScrollViewer>("ReviewScroll").Single();
        scroller.Offset = new Vector(0, 50);
        Until(() => Parts<Grid>("ReviewStickyHeader").Single().GetLogicalDescendants().OfType<Button>()
            .Any(button => Equals(button.Tag, "a.txt")));
        scroller.Offset = default;
        Until(() => Parts<Grid>("ReviewStickyHeader").Single().Children.Count == 0);
        var editor = Review()!.DiffFor("a.txt")!.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Last();
        Until(() => editor.VerticalScroll.Maximum == 0);
        Require(double.IsPositiveInfinity(editor.WrapWidth), "Stacked code must stay unwrapped.");
        var wheelPoint = editor.TranslatePoint(new Point(120, 30), app.Window)!.Value;
        app.Window.MouseWheel(wheelPoint, new Vector(-2, 0));
        Until(() => editor.HorizontalScroll.Value > 0);
        Require(scroller.Offset.Y == 0, "Horizontal code scrolling must leave the changeset position alone.");
        app.Window.MouseWheel(wheelPoint, new Vector(0, -1));
        Until(() => scroller.Offset.Y > 0);
        Require(editor.VerticalPixelOffset == 0, "Wheeling over stacked code must move only the changeset.");
        scroller.Offset = default;

        using (var capture = app.Window.CaptureRenderedFrame()) capture?.Save(Path.Combine(root, "changes-review.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        ClickRow(app, "b.txt", twice: true);
        Until(() => app.Window.Layout.Selected(app.Center) is { Kind: "diff", Path: "b.txt", Preview: false });
        Require(app.Tabs.Any(tab => tab.Kind == "changes"), "An explicitly kept review must survive opening an individual diff.");

        var commit = E2E.IsolatedGit.Run(repository, "rev-parse", "HEAD").Trim();
        app.Window.Layout.Open(new DockTab("changes:commit:" + commit, "Changes · seed", "changes", Scope: "commit", Comparison: commit), true);
        Until(() => Review() is not null && Parts<Border>("ReviewSection").Length > 0 && Parts<Button>("DiffCopy").Length > 0);
        Require(Parts<Button>("DiffKeepBlock").Length == 0 && Parts<Button>("DiffRevertFile").Length == 0,
            "A historical review must offer neither Keep nor Revert.");
        File.WriteAllText(Path.Combine(repository, "untracked.txt"), "new untracked content\n");
        app.Window.Layout.Open(new DockTab("changes:all:", "Changes · All changes", "changes", Scope: "all"), true);
        Until(() => Review() is not null && Parts<Border>("ReviewSection").Any(section => Equals(section.Tag, "untracked.txt")));
        Review()!.Reveal("untracked.txt");
        Until(() => Review()!.DiffFor("untracked.txt")?.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>()
            .Any(editor => editor.Text.Contains("new untracked content", StringComparison.Ordinal)) == true);
        Console.WriteLine("PASS continuous Changes review: click grammar, lazy generated sections, Keep/viewed progress, presentation switches, navigator and historical scope");
    }

    internal static void Presentation()
    {
        const string diff = "@@ -1,2 +1,2 @@\n context\n-before source\n+after source\n";
        var context = new SharpRail.Plugins.UI.Kit.Markdown.MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 100, true);
        var files = new[] { "README.md", "notes.md", "code.cs" }.Select(path => new GitChange(path, "M", " ", null, 1, 1)).ToArray();
        var choices = new Dictionary<string, string>();
        var state = new ChangesReviewState { ActivePath = "README.md" };
        var single = true;
        using var pane = new ChangesReviewPane("Changes", state, false,
            _ => Task.FromResult(new GitSnapshot(true, "main", files, [], [])),
            (file, natural, _, _) => Task.FromResult(new DiffView(diff, file.Path, 800,
                file.Path.EndsWith(".md", StringComparison.Ordinal) ? [new DiffChoice("markdown", "Preview", _ =>
                    Task.FromResult<Control?>(new SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview("Changed **paragraph**", file.Path, context))), DiffChoice.Source] : [DiffChoice.Source],
                selected: choices.GetValueOrDefault(file.Path), selectedChanged: choice => choices[file.Path] = choice,
                naturalHeight: natural, embedded: true)),
            _ => { }, () => { }, () => { }, () => true, () => single, value => single = value);
        var window = new Window { Width = 1000, Height = 700, Content = pane };
        window.Show();
        try
        {
            Control Scope() => pane;
            Button Button(string name, Control? scope = null) => (scope ?? Scope()).GetLogicalDescendants().OfType<Button>().Single(button => button.Name == name);
            void Click(string name, Control? scope = null)
            {
                var button = Button(name, scope);
                Until(() => button.Bounds.Width > 0 && button.IsEffectivelyVisible);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Settle();
            }
            bool Controls() => Button("ReviewSplit").IsVisible && Button("ReviewInline").IsVisible && Button("ReviewWhitespace").IsVisible;
            void Split(DiffView view)
            {
                var editors = view.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().ToArray();
                Require(editors.Length == 2, "Split Source must create two editors instead of a unified patch.");
                var old = editors.Single(editor => editor.Name == "DiffOldText");
                var next = editors.Single(editor => editor.Name == "DiffNewText");
                Until(() => old.Bounds.Width > 0 && next.Bounds.Width > 0);
                Require(old.Text.Contains("before source", StringComparison.Ordinal) && !old.Text.Contains("after source", StringComparison.Ordinal) &&
                    next.Text.Contains("after source", StringComparison.Ordinal) && !next.Text.Contains("before source", StringComparison.Ordinal) &&
                    old.TranslatePoint(new Point(old.Bounds.Width, 0), window)!.Value.X <= next.TranslatePoint(default, window)!.Value.X,
                    "Split places only the old source on the left and only the new source on the right.");
            }
            void Inline(string path)
            {
                Until(() => pane.DiffFor(path)?.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().SingleOrDefault() is { Name: "DiffInlineText" });
                var editor = pane.DiffFor(path)!.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Single();
                Require(editor.Text.Contains("before source", StringComparison.Ordinal) && editor.Text.Contains("after source", StringComparison.Ordinal),
                    "Inline Source contains both changes in one source editor.");
            }
            Until(() => pane.DiffFor("README.md")?.IsRendered == true);
            Require(!Controls(), "Preview-only review must not offer source layout or whitespace controls.");
            Click("DiffView_code", pane.DiffFor("README.md"));
            Until(Controls); Split(pane.DiffFor("README.md")!);
            window.Width = 560; Settle(); Split(pane.DiffFor("README.md")!);
            Require(((Avalonia.Controls.Primitives.ToggleButton)Button("ReviewSplit")).IsChecked == true,
                "A review's explicit Split choice must describe the actual source layout after resizing.");
            Click("ReviewInline"); Inline("README.md");
            Click("DiffView_markdown", pane.DiffFor("README.md")); Require(!Controls(), "Returning to Preview hides irrelevant source controls.");
            Click("DiffView_code", pane.DiffFor("README.md")); Inline("README.md");
            Click("ReviewNext"); Until(() => pane.DiffFor("notes.md")?.IsRendered == true); Require(!Controls(), "One-file Preview is independent of a previous Source file.");
            Click("ReviewNext"); Inline("code.cs"); Require(Controls(), "One-file Source retains the review's Inline preference.");
            Click("ReviewPrevious"); Until(() => pane.DiffFor("notes.md")?.IsRendered == true);
            Click("ReviewPrevious"); Inline("README.md");
            Require(choices["README.md"] == DiffChoice.Source.Id, "File navigation retains the selected Markdown Source view.");
            window.Width = 1000;
            Click("DiffView_markdown", pane.DiffFor("README.md"));
            Click("ReviewStacked"); Until(() => pane.DiffFor("code.cs") is not null && Controls());
            Require(pane.DiffFor("README.md")!.IsRendered && !pane.DiffFor("code.cs")!.IsRendered,
                "Mixed stacked review offers source controls without changing Markdown Preview.");
            var preview = pane.DiffFor("README.md")!.GetLogicalDescendants().OfType<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>().Single();
            Click("ReviewSplit"); Split(pane.DiffFor("code.cs")!);
            Require(ReferenceEquals(preview, pane.DiffFor("README.md")!.GetLogicalDescendants().OfType<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>().Single()),
                "Source layout changes leave the rendered Markdown control and its state intact.");
            window.Height = 180;
            var scroll = pane.GetLogicalDescendants().OfType<ScrollViewer>().Single(view => view.Name == "ReviewScroll");
            scroll.Offset = default;
            Until(() => pane.DiffFor("code.cs") is null);
            Require(Controls(), "Scrolling a known Source section offscreen must not change the review toolbar.");
            window.Height = 700;
            using var standalone = new DiffView(diff, "README.md", 800,
                [new DiffChoice("markdown", "Preview", _ => Task.FromResult<Control?>(new TextBlock { Text = "Rendered Markdown" })), DiffChoice.Source]);
            window.Content = standalone;
            Until(() => standalone.Bounds.Width > 0);
            Require(!Button("DiffSplit", standalone).IsVisible, "Standalone Preview has no source-layout toggle.");
            Click("DiffView_code", standalone);
            Require(Button("DiffSplit", standalone).IsVisible, "Standalone Markdown Source offers its source-layout toggle.");
            Split(standalone);
            Click("DiffSplit", standalone);
            Require(standalone.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Single().Name == "DiffInlineText",
                "Standalone Markdown Source switches to a real inline editor.");
            Console.WriteLine("PASS review controls: Preview-only, real Source inline/split geometry, resize, file-mode retention and mixed stacked review");
        }
        finally { window.Close(); }
    }

    internal static void Retention()
    {
        MarkdownScrolling();
        const string original = "@@ -1,2 +1,2 @@\n before\n-old\n+new\n";
        var state = new ChangesReviewState();
        TaskCompletionSource<DiffView>? pending = null;
        var loads = 0;
        using var pane = new ChangesReviewPane("Changes", state, false,
            _ => Task.FromResult(new GitSnapshot(true, "main", [new GitChange("a.txt", "M", " ", null, 1, 1)], [], [])),
            (_, natural, _, token) =>
            {
                loads++;
                return pending is null ? Task.FromResult(new DiffView(original, "a.txt", 800, naturalHeight: natural, embedded: true))
                    : pending.Task.WaitAsync(token);
            }, _ => { }, () => { }, () => { }, () => true, () => false, _ => { });
        var window = new Window { Width = 800, Height = 200, Content = pane };
        window.Show();
        try
        {
            Until(() => pane.DiffFor("a.txt")?.Bounds.Height > 0);
            var retained = pane.DiffFor("a.txt")!;
            var editor = retained.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Last();
            editor.SelectRange(editor.Text.IndexOf("new", StringComparison.Ordinal), 3);
            var scroll = pane.GetLogicalDescendants().OfType<ScrollViewer>().Single(view => view.Name == "ReviewScroll");
            scroll.Offset = new Vector(0, 40);
            Settle();
            var offset = scroll.Offset.Y;
            var previousLoads = loads;
            window.Content = new TextBlock { Text = "Another tab" };
            Settle();
            _ = pane.RefreshAsync();
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Content = pane;
            Until(() => loads > previousLoads);
            Require(ReferenceEquals(retained, pane.DiffFor("a.txt")) && retained.IsEffectivelyVisible &&
                !pane.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Loading diff…"),
                "Returning to a stale review must show its retained content while rereading the diff.");
            Require(Math.Abs(scroll.Offset.Y - offset) < 1, "A pending review refresh must preserve the scrolled position.");
            pending.SetResult(retained);
            Settle();
            Require(ReferenceEquals(editor, retained.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Last()) && editor.Selection.Text == "new",
                "An unchanged review must retain its editor and selection state.");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previousLoads = loads;
            _ = pane.RefreshAsync();
            Until(() => loads > previousLoads);
            Require(ReferenceEquals(retained, pane.DiffFor("a.txt")), "A visible review refresh must retain its old body until replacement is ready.");
            var replacement = new DiffView(original.Replace("+new", "+changed", StringComparison.Ordinal), "a.txt", 800, naturalHeight: true, embedded: true);
            pending.SetResult(replacement);
            Until(() => ReferenceEquals(replacement, pane.DiffFor("a.txt")));
            Require(replacement.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Any(view => view.Text.Contains("changed", StringComparison.Ordinal)),
                "Fresh changed content must still replace the retained review.");
            Console.WriteLine("PASS review tab retention: hidden/visible asynchronous refreshes, pending content, editor identity and scroll offset");
        }
        finally { window.Close(); }
    }

    private static void MarkdownScrolling()
    {
        var context = new SharpRail.Plugins.UI.Kit.Markdown.MarkdownContext((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 14, 100, true);
        var markdown = string.Join("\n\n", Enumerable.Range(1, 90).Select(index => "Paragraph " + index + " with enough content to review."));
        var state = new ChangesReviewState();
        using var pane = new ChangesReviewPane("Changes", state, false,
            _ => Task.FromResult(new GitSnapshot(true, "main", [new GitChange("README.md", "M", " ", null, 90, 0)], [], [])),
            (_, natural, triage, _) => Task.FromResult(new DiffView("", "README.md", 800,
                [new DiffChoice("markdown", "Preview", _ => Task.FromResult<Control?>(new SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview(markdown, "README.md", context)))],
                naturalHeight: natural, embedded: true)),
            _ => { }, () => { }, () => { }, () => true, () => false, _ => { });
        var window = new Window { Width = 800, Height = 500, Content = pane };
        window.Show();
        try
        {
            SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview? Preview() => pane.GetLogicalDescendants().OfType<SharpRail.Plugins.UI.Kit.Markdown.MarkdownPreview>().FirstOrDefault();
            Until(() => Preview()?.Bounds.Height > 1000);
            var preview = Preview()!;
            var scroll = pane.GetLogicalDescendants().OfType<ScrollViewer>().Single(view => view.Name == "ReviewScroll");
            Require(preview.Extent.Height <= preview.Viewport.Height + 1, "Stacked Markdown must show its full content without an inner vertical viewport.");
            window.MouseWheel(preview.TranslatePoint(new Point(100, 60), window)!.Value, new Vector(0, -2));
            Until(() => scroll.Offset.Y > 0);
            Require(preview.Offset.Y == 0, "Markdown wheel input must move only the changeset.");
            var header = pane.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ReviewFile");
            Require(header.Bounds.Height == 32 && !pane.GetLogicalDescendants().OfType<Border>().Any(control => control.Name == "DiffPath" && control.IsVisible),
                "File headers must stay compact without repeating the path.");
            var end = pane.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "ReviewEnd");
            Require(end.Bounds.Height < 60, "End of changes must remain a compact row.");
            var frame = pane.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "ReviewSection");
            Require(frame.BorderThickness == new Thickness(1) && frame.Parent is StackPanel { Spacing: 16 },
                "Each changed file must have a border and a clear gap from its neighbors.");
            var retained = pane.DiffFor("README.md");
            window.Content = new TextBlock { Text = "Another tab" };
            Settle();
            window.Content = pane;
            Settle();
            Require(ReferenceEquals(retained, pane.DiffFor("README.md")) && ReferenceEquals(preview, Preview()),
                "Returning to a review must retain its loaded diff and rendered Markdown.");
            window.Width = 600;
            Until(() => preview.Bounds.Width < 700);
            Require(preview.Extent.Height <= preview.Viewport.Height + 1, "Markdown must retain natural height after resizing.");
            window.Content = new TextBlock { Text = "Another tab" };
            _ = pane.RefreshAsync();
            window.Content = pane;
            Until(() => pane.DiffFor("README.md") is { } current && !ReferenceEquals(current, retained));
            Require(!ReferenceEquals(preview, Preview()), "Changes while hidden must refresh the review on return.");
        }
        finally { window.Close(); }
    }

    private static void ContentIdentity()
    {
        const string first = "@@ -1,3 +1,3 @@\n same\n-before\n+after\n same\n";
        const string shifted = "@@ -9,3 +12,3 @@\n same\n-before\n+after\n same\n";
        var firstKey = DiffView.ContentKeys(first).Values.Single();
        Require(firstKey == DiffView.ContentKeys(shifted).Values.Single(), "Unique Keep keys must survive neighbouring line shifts.");
        Require(firstKey != DiffView.ContentKeys(first.Replace("+after", "+different", StringComparison.Ordinal)).Values.Single(), "Changed content must invalidate Keep.");
        var twins = DiffView.ContentKeys(first + shifted).Values.ToArray();
        Require(twins.Distinct().Count() == 2 && twins.All(key => key != firstKey), "Removing a duplicate must not transfer its Keep decision to the remaining twin.");
        var model = new ChangesReviewState();
        model.Keep("file", "first", true, ["first", "last"]);
        Require(!model.Viewed.Contains("file"), "Keeping one of two hunks must leave the file unviewed.");
        model.Keep("file", "last", true, ["first", "last"]);
        Require(model.Viewed.Contains("file"), "Keeping the final hunk must mark its file viewed.");
        model.Viewed.Remove("file");
        model.Keep("file", "last", true, ["first", "last"]);
        Require(!model.Viewed.Contains("file"), "A repeated Keep must respect an explicit unviewed choice.");
    }
}