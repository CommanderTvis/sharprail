using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ChangesScopeE2E
{
    internal static void Run(string root)
    {
        if (Source is not { } source)
        {
            Console.WriteLine("SKIP upstream Changes scopes: set SHARPRAIL_TEST_GIT_SOURCE.");
            return;
        }
        ScopeSelector(root, source);
        HeadMovesOutOfBand(root, source);
        TargetPicker(root, source);
        MergeBase(root, source);
        CommitPill(root, source);
        PerWorkspaceMenu(root, source);
        RetargetOpenTabs(root, source);
        RewrittenCommit(root, source);
        FailedRead(root, source);
    }

    private static bool Sha(string label) => Regex.IsMatch(label, "^[0-9a-f]{7,}$");

    private static void ScopeSelector(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-scope", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        ShowChanges(app);
        UntilRows(app, "README.md", "committed.txt");
        Require(ScopeLabel(app) == "All changes", "The scope selector must start on All changes.");

        Pick(app, "ChangesScope", CommitItem("e2e scope commit"));
        Until(() => Sha(ScopeLabel(app)));
        UntilRows(app, "committed.txt");

        PickScope(app, "Uncommitted");
        Until(() => ScopeLabel(app) == "Uncommitted");
        UntilRows(app, "README.md");
        ClickRow(app, "README.md", twice: true);
        Until(() => DiffTabs(app).Count() == 1);

        PickScope(app, "All changes");
        Until(() => ScopeLabel(app) == "All changes");
        Until(() => Paths(app).Contains("README.md"));
        ClickRow(app, "README.md", twice: true);
        Until(() => DiffTabs(app).Count() == 2);
        Console.WriteLine("PASS upstream changes.spec.ts: Changes scope selector filters by commit / uncommitted; each scope is its own diff tab");
    }

    private static void HeadMovesOutOfBand(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-head", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        File.WriteAllText(Path.Combine(worktree, "committed.txt"), "committed by e2e\ndirty line by e2e\n");
        ShowChanges(app);
        PickScope(app, "Uncommitted");
        Until(() => Paths(app).Contains("committed.txt"));
        ClickRow(app, "committed.txt", twice: true);
        Until(() => Count(DiffText(app), "dirty line by e2e") == 1);

        Settle(1500);
        ChangesFixture.Git(worktree, "add", "-A");
        Commit(worktree, "e2e commits the dirty edits");

        Until(() => Paths(app).Length == 0 && Clean(app));
        Until(() => Count(DiffText(app), "dirty line by e2e") == 0 && Pane(app)!.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "DiffEmpty"));
        Require(DiffTabs(app).Count() == 1, "The open diff tab must survive HEAD moving out-of-band.");
        Console.WriteLine("PASS upstream changes.spec.ts: Uncommitted scope converges when HEAD moves out-of-band (a commit in a terminal)");
    }

    private static void TargetPicker(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-target", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        ShowChanges(app);
        UntilRows(app, "README.md", "committed.txt");

        PickTarget(app, "workspace-1");
        UntilRows(app, "README.md");
        Require(Text(app.Find<Button>("ChangesBranch")).Contains("workspace-1", StringComparison.Ordinal), "The target picker must show the chosen branch.");
        var picker = app.Find<Button>("ChangesBranch");
        app.Click(picker); Until(() => picker.ContextMenu!.IsOpen);
        Require(MenuItems(picker.ContextMenu!.Items).Single(item => Equals(item.Tag, "workspace-1")).IsChecked, "The chosen target must be marked active in the picker.");
        CloseMenu(picker);
        Console.WriteLine("PASS upstream changes.spec.ts: The scope menu's target-branch picker re-points what the changes are measured against");
    }

    private static void MergeBase(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-merge-base", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        var main = ChangesFixture.Git(app.Root, "rev-parse", "--abbrev-ref", "HEAD");
        var upstream = app.Root + "-upstream";
        ChangesFixture.Git(app.Root, "worktree", "add", upstream, "-b", "future-main", main);
        File.WriteAllText(Path.Combine(upstream, "upstream.txt"), "landed on the base after the fork\n");
        ChangesFixture.Git(upstream, "add", "upstream.txt");
        Commit(upstream, "upstream work");

        ShowChanges(app);
        UntilRows(app, "README.md", "committed.txt");
        PickTarget(app, "future-main");
        Until(() => Text(app.Find<Button>("ChangesBranch")).Contains("future-main", StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(worktree, "own-file.txt"), "still just my work\n");
        UntilRows(app, "README.md", "committed.txt", "own-file.txt");
        Console.WriteLine("PASS upstream changes.spec.ts: A target that advanced past the fork point adds no phantom changes (merge-base semantics)");
    }

    private static void CommitPill(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-pill", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        var main = ChangesFixture.Git(app.Root, "rev-parse", "--abbrev-ref", "HEAD");
        ShowChanges(app);
        Pick(app, "ChangesScope", CommitItem("e2e scope commit"));
        Until(() => Sha(ScopeLabel(app)));
        Until(() => Equals(Avalonia.Controls.ToolTip.GetTip(app.Find<Button>("ChangesScope")), "e2e scope commit"));
        Require(Text(app.Find<Button>("ChangesBranch")).Contains(main, StringComparison.Ordinal), "The target picker must keep naming the base branch beside a commit pill.");
        Console.WriteLine("PASS upstream changes.spec.ts: A commit scope keeps the header readable: short sha on the pill, subject in its tooltip");
    }

    private static void PerWorkspaceMenu(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-menu", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        ShowChanges(app);
        Until(() => HasMenuItem(app, "ChangesScope", CommitItem("e2e scope commit")));
        Require(app.Find<Button>("ChangesScope").ContextMenu!.Items.OfType<MenuItem>().Count(item => item.Name?.StartsWith("ChangesCommit_", StringComparison.Ordinal) == true) == 1,
            "The first worktree must list its own commit exactly once.");

        WorkspaceTabsE2E.Switch(app, app.Root);
        WorkspaceTabsE2E.CreateWorkspace(app, "workspace-2");
        ShowChanges(app);
        Until(() => HasMenuItem(app, "ChangesScope", item => item.Name == "ChangesNoCommits" && Equals(item.Header, "No commits on this branch")));
        Require(!HasMenuItem(app, "ChangesScope", item => item.Name?.StartsWith("ChangesCommit_", StringComparison.Ordinal) == true),
            "A second worktree must not inherit the first worktree's commit rows.");
        Console.WriteLine("PASS upstream changes.spec.ts: The scope menu is per workspace: its commit rows never carry over to another worktree");
    }

    private static void RetargetOpenTabs(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-retarget", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        File.WriteAllText(Path.Combine(worktree, "committed.txt"), "revised by the workspace\n");
        ChangesFixture.Git(worktree, "add", "committed.txt");
        Commit(worktree, "e2e revise commit");
        ChangesFixture.Git(worktree, "branch", "e2e-target", "HEAD~1");
        var main = ChangesFixture.Git(app.Root, "rev-parse", "--abbrev-ref", "HEAD");

        ShowChanges(app);
        UntilRows(app, "README.md", "committed.txt");
        ClickRow(app, "committed.txt", twice: true);
        UntilDiff(app, text => text.Contains("revised by the workspace", StringComparison.Ordinal));
        Require(!DiffText(app).Contains("committed by e2e", StringComparison.Ordinal), "Against the base the diff must not show the intermediate content.");

        PickTarget(app, "e2e-target");
        UntilDiff(app, text => text.Contains("committed by e2e", StringComparison.Ordinal));

        ClickRow(app, "README.md");
        UntilDiff(app, text => text.Contains("dirty edit by e2e", StringComparison.Ordinal));
        PickTarget(app, main);
        Until(() => DiffTabs(app).Where(tab => tab.Scope == "branch").All(tab => tab.Comparison == main));

        ClickRow(app, "committed.txt");
        UntilDiff(app, text => text.Contains("revised by the workspace", StringComparison.Ordinal));
        Require(!DiffText(app).Contains("committed by e2e", StringComparison.Ordinal), "The backgrounded diff tab must be re-read against the restored target.");
        Console.WriteLine("PASS upstream changes.spec.ts: Re-pointing the target branch re-reads an open branch-scope diff tab — active or backgrounded");
    }

    private static void RewrittenCommit(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-rewrite", source);
        using var _ = app;
        SeedCommitAndDirtyEdit(worktree);
        ShowChanges(app);
        Pick(app, "ChangesScope", CommitItem("e2e scope commit"));
        Until(() => Sha(ScopeLabel(app)));

        ChangesFixture.Git(worktree, "reset", "--hard", "HEAD~1");
        ChangesFixture.Git(worktree, "reflog", "expire", "--expire=now", "--all");
        ChangesFixture.Git(worktree, "gc", "--prune=now");
        File.WriteAllText(Path.Combine(worktree, "nudge.txt"), "nudge the watcher\n");

        Until(() => ScopeLabel(app) == "All changes");
        Until(() => app.Find<Border>("GestureToast").IsVisible &&
            app.Find<TextBlock>("GestureToastMessage").Text!.Contains("no longer in this branch", StringComparison.Ordinal));
        Console.WriteLine("PASS upstream changes.spec.ts: A commit scope whose commit is rewritten away falls back to All changes with a toast");
    }

    private static void FailedRead(string root, string source)
    {
        var (app, worktree) = Open(root, "changes-failed-read", source);
        using var _ = app;
        var main = ChangesFixture.Git(app.Root, "rev-parse", "--abbrev-ref", "HEAD");
        File.WriteAllText(Path.Combine(worktree, "README.md"), "# sample-project\n\nedited by e2e\n");
        ChangesFixture.Git(worktree, "branch", "doomed");

        ShowChanges(app);
        PickTarget(app, "doomed");
        UntilRows(app, "README.md");
        ChangesFixture.Git(worktree, "branch", "-D", "doomed");

        PickScope(app, "Uncommitted");
        UntilRows(app, "README.md");
        PickScope(app, "All changes");
        Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "ChangesError"));
        Require(!Clean(app), "A failed read must never render as a clean working tree.");
        Require(app.Find<Button>("ChangesRetry").IsEffectivelyVisible, "A failed read must offer Retry.");

        PickTarget(app, main);
        UntilRows(app, "README.md");
        Require(!app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "ChangesError"), "Re-pointing to a valid target must clear the error.");
        Console.WriteLine("PASS upstream changes.spec.ts: A failed read says so — it never renders as an empty (clean) change set");
    }
}