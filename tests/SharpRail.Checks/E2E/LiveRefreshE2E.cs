using System.Diagnostics;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Scintilla;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Upstream's live-refresh cases. WebSocket fsChanged frames become the window's coalesced watcher
/// refreshes, and the host health probe becomes a direct host call timed during the storm.
/// </summary>
internal static class LiveRefreshE2E
{
    internal static void Run(string root)
    {
        if (Source is not { } source)
        {
            Console.WriteLine("SKIP upstream live refresh: set SHARPRAIL_TEST_GIT_SOURCE.");
            return;
        }
        Panels(root, source);
        Storm(root, source);
        if (OperatingSystem.IsMacOS()) Editors(root, source);
    }

    private static bool HasFile(E2eWorkspace app, string path) =>
        app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>().Any(item => item.Tag is ProjectFile file && file.Path == path);

    private static bool HasSpec(E2eWorkspace app, string title) =>
        app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "SpecsTree" &&
            tree.GetLogicalDescendants().OfType<TreeViewItem>().Any(item => AutomationProperties.GetName(item) == title));

    private static string PreviewText(E2eWorkspace app) => string.Concat(app.Window.GetLogicalDescendants().OfType<MarkdownPreview>()
        .Where(preview => preview.IsEffectivelyVisible && preview.Name == "MarkdownPreview")
        .SelectMany(preview => preview.GetLogicalDescendants().OfType<SelectableTextBlock>())
        .SelectMany(block => block.Inlines?.OfType<Run>() ?? []).Select(run => run.Text));

    private static void Panels(string root, string source)
    {
        var (app, worktree) = OpenSample(root, "live-panels", source);
        using var _ = app;
        app.Click(app.Find<Button>("Tab_specs"));
        Until(() => HasSpec(app, "Sample Project"));
        Directory.CreateDirectory(Path.Combine(worktree, "module-live"));
        File.WriteAllText(Path.Combine(worktree, "module-live", "SPEC.md"),
            "---\nid: sample-live\ntype: module-design\ntitle: Live Module\nparent: sample-root\n---\n\n## Responsibility\n\nWritten on disk mid-session by the e2e suite.\n");
        Until(() => HasSpec(app, "Live Module"));

        app.Click(app.Find<Button>("Tab_files"));
        Until(() => HasFile(app, "README.md"));
        File.WriteAllText(Path.Combine(worktree, "fresh-file.txt"), "hello\n");
        Until(() => HasFile(app, "fresh-file.txt"));
        File.Delete(Path.Combine(worktree, "fresh-file.txt"));
        Until(() => !HasFile(app, "fresh-file.txt"));

        ShowChanges(app);
        Until(() => Paths(app).Contains("module-live/SPEC.md"));
        Require(!Paths(app).Contains("README.md"), "A clean README must not be listed.");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\nedited live by e2e\n");
        Until(() => RenderedDiffE2E.Modified(app, "README.md"));
        ClickRow(app, "README.md");
        UntilDiff(app, text => text.Contains("edited live by e2e", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\nedited twice by e2e\n");
        UntilDiff(app, text => text.Contains("edited twice by e2e", StringComparison.Ordinal));

        app.Click(app.Find<Button>("Tab_files"));
        app.Click(app.FileRow("README.md"), twice: true);
        Until(() => PreviewText(app).Contains("edited twice by e2e", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\nlive tab reload\n");
        Until(() => PreviewText(app).Contains("live tab reload", StringComparison.Ordinal) && !PreviewText(app).Contains("edited twice by e2e", StringComparison.Ordinal));
        Console.WriteLine("PASS upstream live-refresh.spec.ts: worktree changes on disk appear live in Specs, Files, Changes, and an open file tab");
    }

    private static void Storm(string root, string source)
    {
        var (app, worktree) = OpenSample(root, "live-storm", source);
        using var _ = app;
        app.Click(app.Find<Button>("Tab_files"));
        Until(() => HasFile(app, "README.md"));
        Settle(1200);
        var before = app.Window.WatchRefreshes;
        Directory.CreateDirectory(Path.Combine(worktree, "storm"));
        var probe = TimeSpan.MinValue;
        for (var burst = 0; burst < 20; burst++)
        {
            for (var index = 0; index < 10; index++)
                File.WriteAllText(Path.Combine(worktree, "storm", $"f-{burst}-{index}.txt"), $"{burst}:{index}\n");
            if (burst == 10)
            {
                var timer = Stopwatch.StartNew();
                var listing = app.Host.ListFilesAsync("storm").AsTask();
                Until(() => listing.IsCompleted);
                Require(listing.Result.Count > 0, "The host must answer during the storm.");
                probe = timer.Elapsed;
            }
            Settle(150);
        }
        File.WriteAllText(Path.Combine(worktree, "storm-done.txt"), "done\n");
        Until(() => HasFile(app, "storm-done.txt"));
        Settle(1500);
        app.ExpandFolder("storm");
        Until(() => HasFile(app, "storm/f-19-9.txt"));
        var refreshes = app.Window.WatchRefreshes - before;
        Require(refreshes is >= 2 and <= 8, $"A write storm must coalesce into 2–8 refreshes, not {refreshes}.");
        Require(probe >= TimeSpan.Zero && probe < TimeSpan.FromSeconds(1), $"The host took {probe.TotalMilliseconds:F0} ms to answer during the storm.");
        Console.WriteLine($"PASS upstream live-refresh.spec.ts: churn canary: a write storm coalesces to a few frames and the host stays responsive ({refreshes} refreshes, host {probe.TotalMilliseconds:F0} ms)");
    }

    private static void Editors(string root, string source)
    {
        var (app, worktree) = OpenSample(root, "live-editor", source);
        using var _ = app;
        var path = Path.Combine(worktree, "live.txt");
        File.WriteAllText(path, "first version\n");
        app.Click(app.Find<Button>("Tab_files"));
        Until(() => HasFile(app, "live.txt"));
        app.Click(app.FileRow("live.txt"), twice: true);
        Until(() => app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().Any(editor => editor.IsEffectivelyVisible && editor.Text.Contains("first version", StringComparison.Ordinal)));
        var editor = app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().Single(item => item.IsEffectivelyVisible);
        File.WriteAllText(path, "second version\n");
        Until(() => editor.Text == "second version\n");
        Require(!editor.IsModified && app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().Contains(editor),
            "A clean editor must reload in place without becoming modified.");

        editor.Focus();
        app.Window.KeyTextInput("typed ");
        Dispatcher.UIThread.RunJobs();
        Require(editor.IsModified && editor.Text.Contains("typed", StringComparison.Ordinal), "Typing must modify the editor.");
        var edited = editor.Text;
        var reloads = app.Window.WatchRefreshes;
        File.WriteAllText(path, "external version\n");
        Until(() => app.Window.WatchRefreshes > reloads);
        Settle(500);
        Require(editor.Text == edited && editor.IsModified, "An editor with unsaved edits must keep them when the file changes on disk.");
        editor.Focus();
        app.Window.KeyPress(Key.S, RawInputModifiers.Meta, PhysicalKey.S, "s");
        app.Window.KeyRelease(Key.S, RawInputModifiers.Meta, PhysicalKey.S, "s");
        Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "WorkspaceError" && text.IsVisible &&
            (text.Text?.Contains("changed on disk", StringComparison.Ordinal) ?? false)));
        Require(editor.IsModified && File.ReadAllText(path) == "external version\n", "Saving over an external change must report the conflict and keep both versions.");
        Console.WriteLine("PASS live refresh reloads a clean Scintilla tab and keeps unsaved edits for the save conflict");
        Discard(app, "file:live.txt");
    }

    private static void Discard(E2eWorkspace app, string tab)
    {
        app.Window.Layout.Close(app.Center, tab);
        Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any());
        var dialog = app.Window.OwnedWindows.OfType<DialogWindow>().Single();
        dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Don't Save" })
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Until(() => app.Tabs.All(item => item.Id != tab));
    }
}