using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class GitUiChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Dispatcher.UIThread.RunJobs(); Require(done(), "Git UI operation timed out.");
    }

    private static IEnumerable<Button> Buttons(Window window) => window.GetLogicalDescendants().OfType<Button>();
    private static MenuItem Action(Button button, string name) => button.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, name));
    private static void Invoke(MenuItem item)
    {
        Require(item.IsEnabled, "Git menu action is disabled.");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, Button button)
    {
        button.BringIntoView();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Run(string root)
    {
        if (!Directory.Exists(root + "/.git")) return;
        var window = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(root + "-ui-profile"));
        window.Show(); Pump(() => window.WorkspaceMounted && Buttons(window).Any(button => button.Name == "ChangesBranch"));
        Button Change() => Buttons(window).Single(button => ToolTip.GetTip(button) is string tip &&
            tip.StartsWith("space ü\tfile.txt", StringComparison.Ordinal) &&
            button.ContextMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Stage file")) == true);
        Button Named(string name) => Buttons(window).Single(button => button.Name == name);
        Click(window, Named("ChangesTree"));
        Require(window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.GetLogicalAncestors().OfType<Grid>().Any(grid => grid.Name == "ChangesPanel")),
            "Changes Tree control did not switch the view.");
        Click(window, Named("ChangesList"));
        Require(!window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.GetLogicalAncestors().OfType<Grid>().Any(grid => grid.Name == "ChangesPanel")),
            "Changes List control did not restore the flat view.");
        Require(Named("ChangesScope").Bounds.Height == 24 && Named("ChangesList").Bounds.Height == 20,
            "Changes toolbar does not match the compact reference geometry.");
        Invoke(Action(Named("ChangesScope"), "Staged"));
        Pump(() => Action(Named("ChangesScope"), "Staged").IsChecked);
        Require(!Buttons(window).Any(button => ToolTip.GetTip(button) is string tip && tip.StartsWith("space ü\tfile.txt", StringComparison.Ordinal)),
            "Staged scope displayed an untracked file.");
        Invoke(Action(Named("ChangesScope"), "All changes"));
        Pump(() => Action(Named("ChangesScope"), "All changes").IsChecked &&
            Buttons(window).Any(button => ToolTip.GetTip(button) is string tip && tip.StartsWith("space ü\tfile.txt", StringComparison.Ordinal)));
        Invoke(Action(Change(), "Stage file"));
        Pump(() => Action(Change(), "Unstage file").IsEnabled);
        File.WriteAllText(Path.Combine(root, "space ü\tfile.txt"), "Untracked content\nPending second line\n");
        Invoke(Action(Named("ChangesScope"), "Staged"));
        Pump(() => Action(Named("ChangesScope"), "Staged").IsChecked &&
            Change().GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "+1"));
        Invoke(Action(Named("ChangesScope"), "Uncommitted"));
        Pump(() => Action(Named("ChangesScope"), "Uncommitted").IsChecked &&
            Change().GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "+2"));
        Click(window, Change());
        Pump(() => window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs).Any(tab => tab.Kind == "diff" && tab.Scope == "uncommitted"));
        Require(window.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(text =>
            text.Inlines?.OfType<Run>().Any(run => run.Text?.Contains("Untracked content", StringComparison.Ordinal) == true) == true),
            "Uncommitted scope did not include the staged-only file and its diff.");
        Invoke(Action(Named("ChangesScope"), "All changes"));
        Pump(() => Action(Named("ChangesScope"), "All changes").IsChecked);
        Click(window, Change());
        Pump(() => window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs).Any(tab => tab.Kind == "diff" && tab.Scope == "all"));
        Require(window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs).Count(tab => tab.Kind == "diff") == 2,
            "All changes and Uncommitted reused the same diff tab.");
        File.WriteAllText(Path.Combine(root, "space ü\tfile.txt"), "Untracked content\n");
        Invoke(Action(Named("ChangesScope"), "Staged"));
        Pump(() => Action(Named("ChangesScope"), "Staged").IsChecked);
        Invoke(Action(Change(), "Unstage file"));
        Pump(() => !Buttons(window).Any(button => ToolTip.GetTip(button) is string tip && tip.StartsWith("space ü\tfile.txt", StringComparison.Ordinal)));
        Require(Action(Named("ChangesScope"), "Staged").IsChecked, "Unstaging reset the selected scope.");
        Invoke(Action(Named("ChangesScope"), "All changes"));
        Pump(() => Buttons(window).Any(button => ToolTip.GetTip(button) is string tip && tip.StartsWith("space ü\tfile.txt", StringComparison.Ordinal)));
        Pump(() => !Action(Change(), "Unstage file").IsEnabled);

        Invoke(Action(Named("ChangesBranch"), "sharprail-fork"));
        Pump(() => Named("ChangesScope").ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Name?.StartsWith("ChangesCommit_", StringComparison.Ordinal) == true));
        MenuItem Commit() => Named("ChangesScope").ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name?.StartsWith("ChangesCommit_", StringComparison.Ordinal) == true);
        var commitId = Commit().Name!["ChangesCommit_".Length..];
        var subject = ToolTip.GetTip(Commit());
        Invoke(Commit());
        Pump(() => Commit().IsChecked && Equals(ToolTip.GetTip(Named("ChangesScope")), subject));
        Require(Named("ChangesScope").GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text is { } label && commitId.StartsWith(label, StringComparison.Ordinal) && label.Length >= 7),
            "Commit scope header must show a short SHA and retain its subject tooltip.");
        Require(!Buttons(window).Any(button => ToolTip.GetTip(button) is string tip && tip.StartsWith("space ü\tfile.txt", StringComparison.Ordinal)),
            "Commit selection displayed an untracked working file.");
        var committedRow = Buttons(window).First(button => button.ContextMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Stage file")) == true);
        Require(!Action(committedRow, "Stage file").IsEnabled && !Action(committedRow, "Unstage file").IsEnabled,
            "A committed diff row must not mutate working files.");
        Click(window, committedRow);
        Pump(() => window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs).Any(tab => tab.Scope == "commit" && tab.Comparison == commitId));
        Invoke(Action(Named("ChangesScope"), "Uncommitted"));
        Pump(() => Action(Named("ChangesScope"), "Uncommitted").IsChecked && Buttons(window).Any(button => ToolTip.GetTip(button) is string tip && tip.StartsWith("space ü\tfile.txt", StringComparison.Ordinal)));
        Require(Action(Named("ChangesBranch"), "sharprail-fork").IsChecked, "Changing scope discarded the independent comparison target.");
        Require(Action(Change(), "Stage file").IsEnabled, "A retained target disabled staging in Uncommitted scope.");
        Invoke(Commit());
        Pump(() => Commit().IsChecked);

        Click(window, Named("AddWorkspace"));
        Pump(() => window.OwnedWindows.Any(item => Equals(item.Tag, "NewWorkspaceDialog")));
        var prompt = window.OwnedWindows.Single(item => Equals(item.Tag, "NewWorkspaceDialog"));
        Click(prompt, Buttons(prompt).Single(button => button.IsDefault));
        Pump(() => window.WorkspaceRoot != root && window.WorkspaceMounted);
        var worktree = window.WorkspaceRoot;
        Require(Path.GetDirectoryName(worktree) == root + "-worktrees", "A new workspace must be created beside the project.");
        Pump(() => window.WorkspaceRoot == worktree && window.WorkspaceMounted &&
            Buttons(window).Any(button => Equals(ToolTip.GetTip(button), root) && button.ContextMenu is not null));
        var main = Buttons(window).Single(button => Equals(ToolTip.GetTip(button), root) && button.ContextMenu is not null);
        Require(Action(Named("ChangesScope"), "All changes").IsChecked &&
            !Named("ChangesScope").ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Name?.StartsWith("ChangesCommit_", StringComparison.Ordinal) == true),
            "A new workspace inherited the previous workspace's commit selection or catalog.");
        Require(!main.ContextMenu!.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Remove worktree…")), "Main worktree removal was offered.");
        Click(window, main); Pump(() => window.WorkspaceRoot == root && window.WorkspaceMounted &&
            Buttons(window).Any(button => Equals(ToolTip.GetTip(button), worktree)));
        Button Linked() => Buttons(window).Single(button => Equals(ToolTip.GetTip(button), worktree));
        Pump(() => Commit().IsChecked);
        Require(Action(Named("ChangesBranch"), "sharprail-fork").IsChecked, "Returning to a workspace lost its target and commit selection.");
        Invoke(Action(Linked(), "Remove worktree…"));
        var confirmation = window.OwnedWindows.Single(item => item.Title == "Remove worktree?");
        confirmation.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs(); Require(Directory.Exists(worktree), "Cancelling removal deleted the worktree.");
        Invoke(Action(Linked(), "Remove worktree…"));
        confirmation = window.OwnedWindows.Single(item => item.Title == "Remove worktree?");
        Click(confirmation, Buttons(confirmation).Single(button => button.Content is TextBlock { Text: "Remove worktree" }));
        Pump(() => !Directory.Exists(worktree) && !Buttons(window).Any(button => Equals(ToolTip.GetTip(button), worktree)));
        window.Close();
        var saved = new ProfileStore(root + "-ui-profile");
        Require(saved.Data.GitSelections[root].Target == "sharprail-fork" && saved.Data.GitSelections[root].Commit?.Sha == commitId,
            "The profile did not persist the independent target and commit.");
        window = new WorkbenchWindow(new ProjectServices(root), root, saved);
        window.Show();
        Pump(() => window.WorkspaceMounted && Buttons(window).Any(button => button.Name == "ChangesBranch") &&
            Named("ChangesScope").ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Name == "ChangesCommit_" + commitId));
        Require(Commit().IsChecked && Equals(ToolTip.GetTip(Named("ChangesScope")), subject) &&
            Action(Named("ChangesBranch"), "sharprail-fork").IsChecked,
            "A reopened window lost the persisted target, commit catalog or selected commit.");
        Invoke(Action(Named("ChangesScope"), "Uncommitted"));
        Pump(() => Action(Named("ChangesScope"), "Uncommitted").IsChecked);
        window.Close();
        saved = new ProfileStore(root + "-ui-profile");
        Require(saved.Data.GitSelections[root].Scope == "Uncommitted" && saved.Data.GitSelections[root].Commit is null &&
            saved.Data.GitSelections[root].Target == "sharprail-fork",
            "A changed pending scope did not persist independently of its target.");
        Console.WriteLine("PASS Git query restoration across fresh windows with commit catalogs and independent pending scope");
        Console.WriteLine("PASS Git UI stage/unstage, diff, create/switch worktree, cancel/confirm removal and main-worktree protection");
    }
}
