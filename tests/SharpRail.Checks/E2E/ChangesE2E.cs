using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using SharpRail.Host.Abstractions;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class ChangesE2E
{
    internal static void Run(string root)
    {
        var source = Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE");
        if (string.IsNullOrEmpty(source))
        {
            Console.WriteLine("SKIP upstream Changes: set SHARPRAIL_TEST_GIT_SOURCE.");
            return;
        }
        var directory = WorkspaceTabsE2E.Repository(root, "changes-tree", source);
        using var app = new E2eWorkspace(directory);
        var workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        File.AppendAllText(Path.Combine(workspace, ".gitignore"), "\n!docs/guides/\n!docs/guides/notes.md\n");
        Directory.CreateDirectory(Path.Combine(workspace, "docs", "guides"));
        File.WriteAllText(Path.Combine(workspace, "docs", "guides", "notes.md"), "one\ntwo\nthree\n");
        var refresh = app.Window.RefreshAsync();
        Until(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
        app.Click(app.Find<Button>("Tab_changes"));
        Until(() => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "docs/guides/notes.md"));
        Require(app.Find<ToggleButton>("ChangesList").IsChecked == true, "Changes must start in List view.");
        app.Click(app.Find<ToggleButton>("ChangesTree"));
        Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree"));
        var tree = app.Window.GetLogicalDescendants().OfType<TreeView>().Single(control => control.Name == "ChangesTree");
        var folders = tree.GetLogicalDescendants().OfType<TreeViewItem>().Where(node => node.Tag is string).ToArray();
        Require(folders.Length == 1 && Equals(folders[0].Tag, "docs/guides"), "Single-child folder chains must compact into one row.");
        var folder = (Button)folders[0].Header!;
        Require(AutomationProperties.GetName(folder) == "docs/guides" && Text(folder).Contains("+3"), "Compact folders must show their label and aggregate counts.");
        var file = tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(node => node.Tag is GitChange { Path: "docs/guides/notes.md" });
        var row = ((Control)file.Header!).GetLogicalDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "docs/guides/notes.md");
        app.Click(folder); Require(!folders[0].IsExpanded, "Clicking the folder row must collapse its files.");
        app.Click(folder); Require(folders[0].IsExpanded && row.IsEffectivelyVisible, "Clicking again must reveal its files.");
        Require(file.Tag is GitChange { IndexStatus: "?", Added: 3 } && Text(row).Contains("notes.md") && Text(row).Contains("+3"),
            "The file row must retain its untracked status and line count.");
        app.Click(row);
        Until(() => app.Tabs.Count(tab => tab.Kind == "diff") == 1);
        Until(() => app.Window.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(text =>
            text.Inlines?.OfType<Run>().Any(run => run.Text?.Contains("three", StringComparison.Ordinal) == true) == true));
        app.Click(app.Find<Button>("Tab_files")); app.Click(app.Find<Button>("Tab_changes"));
        Require(app.Find<ToggleButton>("ChangesTree").IsChecked == true, "The selected tree view must survive tool navigation.");
        Console.WriteLine("PASS upstream changes.spec.ts: Changes has a List|Tree toggle; Tree groups files into folders with +/- counts");
        ActionMenus(root, source);
        ProbeFailure(root, source);
    }

    private static void ActionMenus(string root, string source)
    {
        using var app = new E2eWorkspace(WorkspaceTabsE2E.Repository(root, "changes-actions", source));
        var workspace = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
        File.AppendAllText(Path.Combine(workspace, ".gitignore"), "\n!docs/notes.md\n");
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        File.WriteAllText(Path.Combine(workspace, "docs", "notes.md"), "one\ntwo\n");
        var refresh = app.Window.RefreshAsync();
        Until(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
        app.Click(app.Find<Button>("Tab_changes"));
        Button Row() => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "docs/notes.md");
        Until(() => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "docs/notes.md"));
        var row = Row();
        var actions = app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Actions for docs/notes.md");
        app.Click(actions); Until(() => row.ContextMenu!.IsOpen);
        Require(app.Tabs.All(tab => tab.Kind != "diff") && actions.Opacity == 1,
            "The action trigger must reveal its menu without opening a diff and stay visible while open.");
        app.Click(row.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy path")), freshGesture: false);
        var copied = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => copied.IsCompleted);
        Require(copied.GetAwaiter().GetResult() == "docs/notes.md", "Copy path must write the repository-relative path to the clipboard.");
        app.Click(row, mouseButton: MouseButton.Right); Until(() => row.ContextMenu.IsOpen);
        app.Click(row.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "View")), freshGesture: false);
        Until(() => app.Tabs.Count(tab => tab.Kind == "diff") == 1);
        Until(() => app.Window.GetLogicalDescendants().OfType<SelectableTextBlock>().Any(text =>
            text.Inlines?.OfType<Run>().Any(run => run.Text?.Contains("two", StringComparison.Ordinal) == true) == true));
        app.Click(app.Find<ToggleButton>("ChangesTree"));
        var file = Row();
        app.Click(file, mouseButton: MouseButton.Right); Until(() => file.ContextMenu!.IsOpen);
        Avalonia.Controls.TopLevel.GetTopLevel(file.ContextMenu!.Items.OfType<MenuItem>().First())!
            .KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Until(() => !file.ContextMenu.IsOpen);
        var folder = app.Window.GetLogicalDescendants().OfType<TreeViewItem>().Single(node => Equals(node.Tag, "docs"));
        app.Click((Control)folder.Header!, mouseButton: MouseButton.Right);
        Require(!file.ContextMenu.IsOpen, "Folder context input must not open a file action menu.");
        Console.WriteLine("PASS upstream changes.spec.ts: A change row's action menu opens from the ⌄ button and from right-click; Copy path writes the relative path");
    }

    private static void ProbeFailure(string root, string source)
    {
        var directory = WorkspaceTabsE2E.Repository(root, "changes-probe-failure", source);
        var marker = Path.Combine(directory, ".git");
        var saved = directory + "-git-admin";
        Directory.Move(marker, saved);
        File.WriteAllText(marker, "gitdir: " + directory + "-missing-admin");
        using var app = new E2eWorkspace(directory);
        app.Click(app.Find<Button>("Tab_changes"));
        Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "ChangesError"));
        Require(Text(app.Find<Control>("ChangesPanel")).Contains("not a git repository:", StringComparison.Ordinal) &&
            !Text(app.Find<Control>("ChangesPanel")).Contains("Working tree clean", StringComparison.Ordinal),
            "Corrupt Git metadata must show its read error rather than an empty change set.");
        app.Click(app.Find<Button>("Tab_files")); app.Open("README.md", true);
        Require(app.Tabs.Any(tab => tab.Path == "README.md"), "Git probe failure must not prevent opening workspace files.");
        File.Delete(marker); Directory.Move(saved, marker);
        app.Click(app.Find<Button>("Tab_changes"));
        app.Click(app.Find<Button>("ChangesRetry"));
        Until(() => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "README.md"));
        Require(!app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ChangesError"),
            "A successful retry must replace the error with actual changes.");
        Console.WriteLine("PASS Git probe failure remains visible, permits document navigation and recovers through Retry");
    }

    private static string Text(Control control) => string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
}
