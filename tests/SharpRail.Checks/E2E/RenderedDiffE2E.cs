using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Upstream's rendered Markdown diff cases. The reference merges in a web worker and watches browser
/// long tasks; here the merge seam records the thread it runs on and the test times every dispatcher turn.
/// </summary>
internal static class RenderedDiffE2E
{
    internal static void Run(string root)
    {
        if (Source is not { } source)
        {
            Console.WriteLine("SKIP upstream rendered Markdown diff: set SHARPRAIL_TEST_GIT_SOURCE.");
            return;
        }
        Toggle(root, source);
        Large(root, source);
        Failure(root, source);
        LiveEdits(root, source);
    }

    private static void OpenDiff(E2eWorkspace app, string path)
    {
        ShowChanges(app);
        ClickRow(app, path);
        Until(() => Pane(app) is { } pane && DiffTabs(app).Any(tab => tab.Path == path) && Named<ToggleButton>(pane, "DiffView_code") is not null);
    }

    private static T? Find<T>(Control scope, string name) where T : Control =>
        scope.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible);

    private static T Named<T>(Control pane, string name) where T : Control =>
        Find<T>(pane, name) ?? throw new InvalidOperationException(name + " is not shown.");

    private static MarkdownPreview? Rendered(E2eWorkspace app) => Pane(app) is { } pane ? Find<MarkdownPreview>(pane, "RenderedDiff") : null;

    private static IEnumerable<Run> Runs(Control scope) =>
        scope.GetLogicalDescendants().OfType<SelectableTextBlock>().SelectMany(block => block.Inlines?.OfType<Run>() ?? []);

    private static string Marked(E2eWorkspace app, string mark) =>
        Rendered(app) is { } rendered ? string.Concat(Runs(rendered).Where(run => run.Classes.Contains(mark)).Select(run => run.Text)) : "";

    private static string RenderedText(E2eWorkspace app) =>
        Rendered(app) is { } rendered ? string.Concat(Runs(rendered).Select(run => run.Text)) : "";

    private static void Toggle(string root, string source)
    {
        var (app, worktree) = OpenSample(root, "rendered-toggle", source);
        using var _ = app;
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\nedited by e2e\n");
        ShowChanges(app);
        Until(() => Modified(app, "README.md"));
        OpenDiff(app, "README.md");
        Require(DiffTabs(app).Count() == 1 && app.Window.Layout.Selected(app.Center)?.Kind == "diff", "The diff must open as the active tab.");
        var pane = Pane(app)!;
        Require(Named<ToggleButton>(pane, "DiffView_markdown").IsChecked == true, "A Markdown diff must start on Rendered.");
        Require(Find<ToggleButton>(pane, "DiffSplit") is null && Find<ToggleButton>(pane, "DiffInline") is null,
            "A Markdown diff must offer Source|Rendered instead of Split|Inline.");
        Until(() => Marked(app, "ins").Contains("edited by e2e", StringComparison.Ordinal));
        var heading = Rendered(app)!.GetLogicalDescendants().OfType<SelectableTextBlock>().First(block => block.FontSize == 24);
        Require(string.Concat(heading.Inlines!.OfType<Run>().Select(run => run.Text)) == "sample-project", "The unchanged heading must render as an h1.");

        app.Click(Named<ToggleButton>(pane, "DiffView_code"));
        Require(Named<ToggleButton>(pane, "DiffView_code").IsChecked == true && Rendered(app) is null, "Source must replace the rendered diff.");
        UntilDiff(app, text => text.Contains("edited by e2e", StringComparison.Ordinal));
        ClickRow(app, "README.md");
        Require(DiffTabs(app).Count() == 1, "Reopening the row must reuse its diff tab.");

        File.WriteAllText(Path.Combine(worktree, "script.ts"), "export const edited = true;\n");
        OpenDiffNamed(app, "script.ts");
        UntilDiff(app, text => text.Contains("edited = true", StringComparison.Ordinal));
        pane = Pane(app)!;
        Require(Named<ToggleButton>(pane, "DiffSplit").IsChecked == true && Find<ToggleButton>(pane, "DiffView_markdown") is null,
            "A non-Markdown diff must start split with no Rendered toggle.");
        app.Click(Named<ToggleButton>(pane, "DiffInline"));
        Require(Named<ToggleButton>(pane, "DiffInline").IsChecked == true, "Inline must become active.");
        UntilDiff(app, text => text.Contains("edited = true", StringComparison.Ordinal));

        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-2");
        ShowChanges(app);
        Until(() => Clean(app));
        Console.WriteLine("PASS upstream changes.spec.ts: Changes tab shows the active worktree's diff and swaps per workspace");
    }

    internal static bool Modified(E2eWorkspace app, string path) =>
        Frames(app).Any(frame => RowPath(frame) == path && ToolTip.GetTip(RowButton(frame)) is string tip && tip.EndsWith("[ M]", StringComparison.Ordinal));

    private static void OpenDiffNamed(E2eWorkspace app, string path)
    {
        ClickRow(app, path);
        Until(() => app.Window.Layout.Selected(app.Center) is { Kind: "diff" } tab && tab.Path == path && Pane(app) is not null);
    }

    private static string LargeMarkdown() =>
        "# Large repetitive doc\n\n" + string.Join("\n", Enumerable.Repeat("- alpha beta gamma delta epsilon", 800)) + "\n";

    private static string LargeMarkdownEdited()
    {
        var lines = LargeMarkdown().Split('\n');
        lines[400] = "- EDITED replacement row";
        return string.Join("\n", lines) + "- appended row by e2e\n";
    }

    private static void Large(string root, string source)
    {
        var (app, worktree) = OpenSample(root, "rendered-large", source, ("LARGE.md", LargeMarkdown()));
        using var _ = app;
        File.WriteAllText(Path.Combine(worktree, "LARGE.md"), LargeMarkdownEdited());
        var uiThread = Environment.CurrentManagedThreadId;
        var mergeThreads = new List<(int Thread, bool UiAccess)>();
        using var release = new ManualResetEventSlim();
        app.Window.RenderedDiffMerge = (before, after, token) =>
        {
            lock (mergeThreads) mergeThreads.Add((Environment.CurrentManagedThreadId, Dispatcher.UIThread.CheckAccess()));
            release.Wait(TimeSpan.FromSeconds(30), token);
            return MarkdownDiff.Merge(before, after, token);
        };
        OpenDiff(app, "LARGE.md");
        Until(() => Find<TextBlock>(Pane(app)!, "RenderedDiffLoading") is not null);
        // The merge is held; the dispatcher keeps running, so the workbench stays interactive meanwhile.
        Until(() => { lock (mergeThreads) return mergeThreads.Count == 1; });
        release.Set();

        var longest = TimeSpan.Zero;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!Marked(app, "ins").Contains("EDITED", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            var turn = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (turn.Elapsed > longest) longest = turn.Elapsed;
            Thread.Sleep(2);
        }
        Require(Marked(app, "ins").Contains("EDITED", StringComparison.Ordinal), "The large rendered diff must mark the edited row.");
        Require(Marked(app, "del").Contains("alpha", StringComparison.Ordinal), "The large rendered diff must mark the replaced row.");
        Require(Marked(app, "ins").Contains("appended row by e2e", StringComparison.Ordinal), "The large rendered diff must mark the appended row.");
        lock (mergeThreads)
            Require(mergeThreads.Count == 1 && mergeThreads[0].Thread != uiThread && !mergeThreads[0].UiAccess,
                "The merge must run once, off the dispatcher thread.");
        Require(longest < TimeSpan.FromMilliseconds(250), $"A dispatcher turn blocked for {longest.TotalMilliseconds:F0} ms (limit 250 ms).");
        Console.WriteLine($"PASS upstream changes.spec.ts: Rendered markdown diff of a large repetitive file never blocks the main thread (longest dispatcher turn {longest.TotalMilliseconds:F0} ms)");
    }

    private static void Failure(string root, string source)
    {
        var (app, worktree) = OpenSample(root, "rendered-failure", source);
        using var _ = app;
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\nedited by e2e\n");
        app.Window.RenderedDiffMerge = (_, _, _) => throw new InvalidOperationException("merge failed");
        OpenDiff(app, "README.md");
        Until(() => Find<TextBlock>(Pane(app)!, "RenderedDiffError") is not null);
        Require(Find<TextBlock>(Pane(app)!, "RenderedDiffError")!.Text!.Contains("Source", StringComparison.Ordinal),
            "The error placeholder must point to the Source view.");
        app.Click(Named<ToggleButton>(Pane(app)!, "DiffView_code"));
        UntilDiff(app, text => text.Contains("edited by e2e", StringComparison.Ordinal));
        Console.WriteLine("PASS upstream changes.spec.ts: Rendered markdown diff shows an error placeholder when the merge worker fails");
    }

    private static void LiveEdits(string root, string source)
    {
        var (app, worktree) = OpenSample(root, "rendered-live", source);
        using var _ = app;
        var readme = Path.Combine(worktree, "README.md");
        File.WriteAllText(readme, "# sample-project\n\nfirst edit by e2e\n");
        OpenDiff(app, "README.md");
        Until(() => Marked(app, "ins").Contains("first edit by e2e", StringComparison.Ordinal));

        // Hold a merge for an intermediate edit so the next edit must cancel it rather than race it.
        using var held = new ManualResetEventSlim();
        var heldCancelled = false;
        app.Window.RenderedDiffMerge = (before, after, token) =>
        {
            if (after.Contains("held edit", StringComparison.Ordinal))
            {
                held.Set();
                heldCancelled = token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                token.ThrowIfCancellationRequested();
            }
            return MarkdownDiff.Merge(before, after, token);
        };
        File.WriteAllText(readme, "# sample-project\n\nheld edit by e2e\n");
        Until(() => held.IsSet);
        File.WriteAllText(readme, "# sample-project\n\nsecond edit by e2e\n");
        Until(() => Marked(app, "ins").Contains("second edit by e2e", StringComparison.Ordinal));
        Until(() => heldCancelled);
        Settle(500);
        Require(!RenderedText(app).Contains("first edit by e2e", StringComparison.Ordinal) && !RenderedText(app).Contains("held edit", StringComparison.Ordinal),
            "The rendered diff must drop stale merges once the fresh one lands.");
        Console.WriteLine("PASS upstream changes.spec.ts: Rendered markdown diff follows live edits on disk (stale merge cancelled, fresh one lands)");
    }
}