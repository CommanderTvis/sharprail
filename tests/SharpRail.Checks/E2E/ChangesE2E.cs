using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
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
        var file = tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(node => node.Tag is GitChange);
        var row = (Button)file.Header!;
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
    }

    private static string Text(Control control) => string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
}
