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
        Click(window, Change());
        Pump(() => window.Layout.State.Workspaces[root].Documents.Values.SelectMany(tabs => tabs).Any(tab => tab.Kind == "diff"));
        Require(window.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(text =>
            text.Inlines?.OfType<Run>().Any(run => run.Text?.Contains("Untracked content", StringComparison.Ordinal) == true) == true),
            "Git UI did not display the real staged diff.");
        Invoke(Action(Change(), "Unstage file"));
        Pump(() => !Action(Change(), "Unstage file").IsEnabled);

        Click(window, Buttons(window).Single(button => Equals(ToolTip.GetTip(button), "Create worktree")));
        var prompt = window.OwnedWindows.Single(item => item.Title == "Create worktree");
        var fields = prompt.GetLogicalDescendants().OfType<TextBox>().ToArray();
        var worktree = root + "-ui-worktree";
        fields[0].Text = worktree; fields[1].Text = "sharprail-ui-" + Guid.NewGuid().ToString("N")[..8];
        Click(prompt, Buttons(prompt).Single(button => button.IsDefault));
        Pump(() => window.WorkspaceRoot == worktree && window.WorkspaceMounted &&
            Buttons(window).Any(button => Equals(ToolTip.GetTip(button), root) && button.ContextMenu is not null));
        var main = Buttons(window).Single(button => Equals(ToolTip.GetTip(button), root) && button.ContextMenu is not null);
        Require(!Action(main, "Remove worktree…").IsEnabled, "Main worktree removal was enabled.");
        Click(window, main); Pump(() => window.WorkspaceRoot == root && window.WorkspaceMounted &&
            Buttons(window).Any(button => Equals(ToolTip.GetTip(button), worktree)));
        Button Linked() => Buttons(window).Single(button => Equals(ToolTip.GetTip(button), worktree));
        Invoke(Action(Linked(), "Remove worktree…"));
        var confirmation = window.OwnedWindows.Single(item => item.Title == "Remove worktree?");
        confirmation.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs(); Require(Directory.Exists(worktree), "Cancelling removal deleted the worktree.");
        Invoke(Action(Linked(), "Remove worktree…"));
        confirmation = window.OwnedWindows.Single(item => item.Title == "Remove worktree?");
        Click(confirmation, Buttons(confirmation).Single(button => button.Content is TextBlock { Text: "Remove worktree" }));
        Pump(() => !Directory.Exists(worktree) && !Buttons(window).Any(button => Equals(ToolTip.GetTip(button), worktree)));
        window.Close();
        Console.WriteLine("PASS Git UI stage/unstage, diff, create/switch worktree, cancel/confirm removal and main-worktree protection");
    }
}
