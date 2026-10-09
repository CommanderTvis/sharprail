using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ChangesDiffE2E
{
    private const string LongPath = "packages/server/src/git/diffScopeResolverImplementationForTheChangesPanel.ts";

    internal static void Run(string root)
    {
        if (Source is not { } source)
        {
            Console.WriteLine("SKIP upstream Changes diff viewer: set SHARPRAIL_TEST_GIT_SOURCE.");
            return;
        }
        Viewer(root, source);
        Rows(root, source);
        NarrowHeader(root, source);
        DelayedWatch(root, source);
    }

    private static void DelayedWatch(string root, string source)
    {
        var repository = WorkspaceTabsE2E.Repository(root, "changes-delayed-watch", source);
        TaskCompletionSource ready = null!;
        using var app = new E2eWorkspace(repository, prepare: host => ready = host.HoldWatch());
        ShowChanges(app);
        Until(() => Paths(app).Contains("README.md"));
        File.WriteAllText(Path.Combine(repository, "before-watch.txt"), "Written before subscribing\n");
        Require(!Paths(app).Contains("before-watch.txt"), "The initial snapshot already contains the later edit.");
        ready.SetResult();
        Until(() => Paths(app).Contains("before-watch.txt"));
        Console.WriteLine("PASS Changes catches an edit between the initial Git snapshot and file-watch readiness");
    }

    private static T Named<T>(Control pane, string name) where T : Control =>
        pane.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Viewer(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-viewer", source);
        using var _ = app;
        var lines = Enumerable.Range(0, 120).Select(index => $"export const v{index} = {index};").ToArray();
        File.WriteAllText(Path.Combine(worktree, "long.ts"), string.Join("\n", lines) + "\n");
        ChangesFixture.Git(worktree, "add", "long.ts");
        Commit(worktree, "long file");
        lines[60] = "export const v60 = 6000;";
        File.WriteAllText(Path.Combine(worktree, "long.ts"), string.Join("\n", lines) + "\n");

        ShowChanges(app);
        PickScope(app, "Uncommitted");
        Until(() => Paths(app).Contains("long.ts"));
        ClickRow(app, "long.ts");
        Until(() => Pane(app) is not null);
        var pane = Pane(app)!;
        Require(Text(Named<Border>(pane, "DiffPath")) == "long.ts", "The diff header must name the file.");
        Until(() => System.Text.RegularExpressions.Regex.IsMatch(DiffText(app), @"\d+ hidden lines"));
        Require(DiffText(app).Contains("6000", StringComparison.Ordinal), "The changed line must stay visible between collapsed context.");

        var whitespace = Named<ToggleButton>(pane, "DiffWhitespace");
        Require(whitespace.IsChecked == false, "Hide whitespace must start off.");
        app.Click(whitespace);
        Require(whitespace.IsChecked == true, "Hide whitespace must toggle on for this tab.");

        app.Click(Named<Button>(pane, "DiffCopy"));
        var copied = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => copied.IsCompleted);
        Require(copied.GetAwaiter().GetResult()?.Contains("export const v60 = 6000;", StringComparison.Ordinal) == true, "Copy must write the diff to the clipboard.");
        Console.WriteLine("PASS upstream changes.spec.ts: The diff viewer collapses unchanged context and has a per-tab hide-whitespace + copy header");
    }

    private static void Rows(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-rows", source);
        using var _ = app;
        Directory.CreateDirectory(Path.Combine(worktree, "packages/server/src/git/deeply/nested/for/the/changes/panel"));
        File.WriteAllText(Path.Combine(worktree, LongPath), "export const range = 1;\n");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\nedited by e2e\n");
        const string rootLevel = "diffScopeResolverImplementationForTheChangesPanelAtRootLevel.ts";
        File.WriteAllText(Path.Combine(worktree, rootLevel), "export const root = 1;\n");
        const string shortName = "packages/server/src/git/deeply/nested/for/the/changes/panel/shortName.ts";
        File.WriteAllText(Path.Combine(worktree, shortName), "export const short = 1;\n");

        ShowChanges(app);
        UntilRows(app, LongPath, "README.md", rootLevel, shortName);
        var panel = app.Find<Control>("ChangesPanel");
        var viewport = panel.GetVisualDescendants().OfType<ScrollViewer>().First();
        Border Frame(string path) => Frames(app).Single(frame => RowPath(frame) == path);
        TextBlock Part(string path, string name) => Frame(path).GetLogicalDescendants().OfType<TextBlock>().First(text => text.Classes.Contains(name));
        Control Counts(string path) => ((Grid)RowButton(Frame(path)).Content!).Children.Last();

        foreach (var path in new[] { LongPath, rootLevel })
        {
            Require(Right(Counts(path), app.Window) <= Right(Frame(path), app.Window) + 1, "Line counts must stay inside their row.");
            Require(Right(Frame(path), app.Window) <= Right(viewport, app.Window) + 1, "A long name must not widen the row past the panel.");
        }
        Require(Clipped(Part(shortName, "change-path-dir")), "A deep directory must truncate before the file name does.");
        Require(!Clipped(Part(shortName, "change-path-base")), "A short file name must not be truncated.");
        Require(Clipped(Part(LongPath, "change-path-base")) && Clipped(Part(rootLevel, "change-path-base")), "A long file name must be truncated.");

        var longRow = Frame(LongPath);
        ClickRow(app, LongPath);
        Until(() => Frames(app).Count(frame => frame.Classes.Contains("active")) == 1);
        var active = Frames(app).Single(frame => frame.Classes.Contains("active"));
        Require(RowPath(active) == LongPath, "The active diff's row must be the highlighted one.");
        Require(active.Bounds.Width > RowButton(active).Bounds.Width, "The highlight must span the menu slot.");
        Require(!Transparent(active.Background), "The row wrapper must paint the highlight.");
        var presenter = RowButton(active).GetVisualDescendants().OfType<ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");
        Require(Transparent(presenter.Background), "The inner row button must not paint over the wrapper highlight.");

        app.Click(app.Find<ToggleButton>("ChangesTree"));
        Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree"));
        var folder = app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .First(button => AutomationProperties.GetName(button)!.StartsWith("packages", StringComparison.Ordinal) && button.Content is Grid);
        var file = RowButton(Frame(LongPath));
        Require(Math.Abs(Right(((Grid)folder.Content!).Children.Last(), app.Window) - Right(((Grid)file.Content!).Children.Last(), app.Window)) <= 1,
            "Folder and file counts must right-align in the tree.");
        Console.WriteLine("PASS upstream changes.spec.ts: Change rows stay one aligned, fully-highlighted row — menu slot included, long names truncated");
    }

    private static void NarrowHeader(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-narrow", source);
        using var _ = app;
        Directory.CreateDirectory(Path.Combine(worktree, "packages/server/src/git"));
        File.WriteAllText(Path.Combine(worktree, LongPath), "export const range = 1;\n");
        ShowChanges(app);
        Until(() => Paths(app).Contains(LongPath));
        ClickRow(app, LongPath);
        Until(() => Pane(app) is not null);

        app.Window.Width = 620; app.Window.Height = 800; Settle();
        var pane = Pane(app)!;
        foreach (var name in new[] { "DiffWhitespace", "DiffCopy", "DiffSplit" })
        {
            var control = Named<Control>(pane, name);
            Require(control.IsEffectivelyVisible && control.Bounds.Width > 0 && Right(control, pane) <= pane.Bounds.Width + 1,
                $"{name} must stay inside the narrow diff header.");
        }
        var chip = Named<Border>(pane, "DiffPath");
        Require(Right(chip, pane) <= Named<Control>(pane, "DiffSplit").TranslatePoint(new Point(0, 0), pane)!.Value.X + 1,
            "The path chip must give way to the controls, however long the path.");
        Console.WriteLine("PASS upstream changes.spec.ts: The diff header keeps its controls on a narrow pane, however long the file's path");

        // Until the user picks a layout, it follows the pane width; a click pins it.
        if (OperatingSystem.IsMacOS())
        {
            Until(() => Named<ToggleButton>(Pane(app)!, "DiffInline").IsChecked == true && Named<ToggleButton>(Pane(app)!, "DiffSplit").IsChecked == false);
            app.Window.Width = 1800; Settle();
            Until(() => Named<ToggleButton>(Pane(app)!, "DiffSplit").IsChecked == true);
            app.Window.Width = 620; Settle();
            Until(() => Named<ToggleButton>(Pane(app)!, "DiffInline").IsChecked == true);
            app.Click(Named<ToggleButton>(Pane(app)!, "DiffSplit"));
            app.Window.Width = 600; Settle();
            app.Window.Width = 640; Settle();
            Require(Named<ToggleButton>(Pane(app)!, "DiffSplit").IsChecked == true, "A clicked Split must stay split on a narrow pane.");
            Console.WriteLine("PASS fork diffLayout: A diff too narrow for two columns opens inline until you say otherwise");
        }
    }
}