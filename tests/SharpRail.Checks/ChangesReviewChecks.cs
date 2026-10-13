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
        ContentIdentity();
        MarkdownScrolling();
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