using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Upstream #779: Files is one tool with an All | Changed switch. All marks changed files with a status letter and
/// every ancestor folder with an amber dot, dims ignored entries, never marks deletions, and remembers expanded
/// folders per workspace across mode, side-tab and workspace switches and a reload; Changed is the Changes view.
/// </summary>
internal static class FilesModesE2E
{
    internal static void Run(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "files-modes"), (".gitignore", "build/\ndebug.log\n"), ("old.txt", "old\n"));
        foreach (var (path, text) in new[] { ("src/auth/login.ts", "one\n"), ("src/ui/view.ts", "view\n"), ("src/gone.ts", "gone\n"), ("legacy/only.ts", "only\n") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(project, path))!);
            File.WriteAllText(Path.Combine(project, path), text);
        }
        IsolatedGit.Run(project, "add", "-A");
        IsolatedGit.Run(project, "commit", "-m", "files");
        File.AppendAllText(Path.Combine(project, "src/auth/login.ts"), "two\n");
        File.WriteAllText(Path.Combine(project, "src/ui/new.ts"), "new\n");
        File.Delete(Path.Combine(project, "src/gone.ts"));
        File.Delete(Path.Combine(project, "legacy/only.ts"));
        IsolatedGit.Run(project, "mv", "old.txt", "renamed.txt");
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        File.WriteAllText(Path.Combine(project, "docs/added.md"), "added\n");
        IsolatedGit.Run(project, "add", "docs/added.md");
        Directory.CreateDirectory(Path.Combine(project, "build"));
        File.WriteAllText(Path.Combine(project, "build/out.js"), "out\n");
        File.WriteAllText(Path.Combine(project, "debug.log"), "log\n");
        var profile = project + "-profile";

        using (var app = new E2eWorkspace(project, profileRoot: profile))
        {
            Until(() => app.Window.WorkspaceMounted);
            app.ShowTool("files");
            Until(() => Row(app, "src").Dot);
            // Marks: every ancestor, collapsed or expanded, carries the amber name and dot; files carry their letter.
            RequireFolder(app, "src");
            RequireFolder(app, "docs");
            Require(!Row(app, "legacy").Dot && Row(app, "legacy").Tint is null, "A folder holding only a deletion must not be marked.");
            Expand(app, "src"); Expand(app, "src/auth"); Expand(app, "src/ui"); Expand(app, "docs");
            RequireFolder(app, "src");
            RequireFolder(app, "src/auth");
            RequireFile(app, "src/auth/login.ts", "M", Ui.Warning);
            RequireFile(app, "src/ui/new.ts", "U", Ui.Success);
            RequireFile(app, "docs/added.md", "A", Ui.Success);
            RequireFile(app, "renamed.txt", "R", Ui.Info);
            Require(Row(app, "src/ui/view.ts") is { Badge: null, Tint: null }, "An unchanged file must carry no mark.");
            Require(Tree(app).Rows.All(row => row.Id != "src/gone.ts"), "A deleted file must not be listed.");
            // Ignored entries and everything inside them are dimmed.
            Require(Row(app, "debug.log").Muted && Row(app, "build").Muted, "Ignored entries must be dimmed.");
            Expand(app, "build");
            Require(Row(app, "build/out.js").Muted, "Entries inside an ignored folder must be dimmed.");
            Console.WriteLine("PASS upstream #779: All marks changed files with M/A/U/R in status colours and every ancestor folder; deletions mark nothing; ignored entries dim");

            // Changed is the Changes view: count, list and marks come from one read.
            app.ShowTool("changes");
            var changes = app.Find<Grid>("ChangesPanel");
            Until(() => changes.IsEffectivelyVisible && changes.GetLogicalDescendants().OfType<Border>().Any(frame => frame.Classes.Contains("change-row")));
            Require(!Tree(app).IsEffectivelyVisible && app.Find<ToggleButton>("ChangesList").IsChecked == true, "Changed must show the Changes view in place of the tree.");
            // The list realizes only rows near its viewport; its items are every listed change.
            var count = changes.GetLogicalDescendants().OfType<ItemsControl>().Single(list => list.Name == "ChangesRows").Items.Count(item => item is not string);
            Require(Label(app, "FilesModeChanged") == $"Changed {count}", $"The Changed switch must count the listed changes: {Label(app, "FilesModeChanged")} vs {count}.");
            app.Click(app.Find<ToggleButton>("ChangesTree"));
            Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree"));
            ChangedFolder(app, "src").IsExpanded = false;
            app.ShowTool("files");
            Require(Expanded(app, "src", "src/auth", "src/ui", "docs"), "Expanded folders must survive the All → Changed → All switch.");
            app.ShowTool("changes");
            Require(!ChangedFolder(app, "src").IsExpanded, "A collapsed Changed-tree folder must stay collapsed across mode switches.");
            app.ShowTool("files");

            // Another side tab in the same pane and back.
            var group = app.Window.Layout.State.Groups.Single(item => item.Tools.Any(tab => tab.Id == "files")).Id;
            app.Window.Layout.Select(group, DockState.SpecsTool); Settle();
            app.ShowTool("files");
            Require(Expanded(app, "src", "src/auth", "docs"), "Expanded folders must survive a side-tab switch.");

            // Another workspace keeps its own tree: it starts from the retained one, and its changes stay its own.
            var other = WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
            app.ShowTool("files");
            Until(() => Expanded(app, "src", "src/auth"));
            Tree(app).Expand(Row(app, "src/auth"), false);
            WorkspaceTabsE2E.Switch(app, project);
            app.ShowTool("files");
            Until(() => Expanded(app, "src", "src/auth", "docs"));
            WorkspaceTabsE2E.Switch(app, other);
            app.ShowTool("files");
            Until(() => Expanded(app, "src") && !Row(app, "src/auth").Expanded);
            WorkspaceTabsE2E.Switch(app, project);
            app.ShowTool("files");
            Until(() => Expanded(app, "src", "src/auth", "docs"));
            Console.WriteLine("PASS upstream #779: expanded folders are remembered across All ↔ Changed, side-tab and workspace switches");
        }

        using (var app = new E2eWorkspace(project, profileRoot: profile))
        {
            Until(() => app.Window.WorkspaceMounted);
            app.ShowTool("files");
            Until(() => Expanded(app, "src", "src/auth", "docs"));
            app.ShowTool("changes");
            Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree") ||
                app.Window.GetLogicalDescendants().OfType<ToggleButton>().Any(toggle => toggle.Name == "ChangesTree"));
            if (!app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree")) app.Click(app.Find<ToggleButton>("ChangesTree"));
            Until(() => app.Window.GetLogicalDescendants().OfType<TreeView>().Any(tree => tree.Name == "ChangesTree"));
            Require(!ChangedFolder(app, "src").IsExpanded, "A collapsed Changed-tree folder must be remembered across a reload.");
            app.ShowTool("files");
            app.Click(app.Find<Button>("FilesCollapseAll"));
            Until(() => Tree(app).Rows.All(row => row.Depth == 0));
            Require(Row(app, "src").Dot, "A collapsed folder keeps its mark.");
            Console.WriteLine("PASS upstream #779: the tree's expanded and collapsed folders survive a reload; Collapse all folders collapses All");
        }
    }

    private static DrawnTree Tree(E2eWorkspace app) => app.Find<DrawnTree>("FilesTree");

    private static DrawnTreeEntry Row(E2eWorkspace app, string path)
    {
        var id = path;
        Until(() => Tree(app).Rows.Any(row => row.Id == id), "row " + path);
        return Tree(app).Rows.Single(row => row.Id == id);
    }

    private static bool Expanded(E2eWorkspace app, params string[] paths) =>
        paths.All(path => Tree(app).Rows.FirstOrDefault(row => row.Id == path) is { Expanded: true } row &&
            row.Children.Any(child => child.Tag is ProjectFile));

    private static void Expand(E2eWorkspace app, string path)
    {
        Tree(app).Expand(Row(app, path), true);
        Until(() => Row(app, path).Children.Any(child => child.Tag is ProjectFile), "listing " + path);
    }

    private static Color ColorOf(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : default;

    private static void RequireFolder(E2eWorkspace app, string path)
    {
        var row = Row(app, path);
        Require(row.Dot && row.Badge is null && ColorOf(row.Tint) == ColorOf(Ui.Warning), $"{path} must show an amber name and dot.");
    }

    private static void RequireFile(E2eWorkspace app, string path, string letter, IBrush colour)
    {
        Until(() => Row(app, path).Badge == letter, path + " " + letter);
        Require(ColorOf(Row(app, path).Tint) == ColorOf(colour) && !Row(app, path).Dot, $"{path} must show {letter} in its status colour.");
    }

    private static string? Label(E2eWorkspace app, string name) => ((TextBlock)app.Find<ToggleButton>(name).Content!).Text;

    private static TreeViewItem ChangedFolder(E2eWorkspace app, string path) =>
        app.Window.GetLogicalDescendants().OfType<TreeView>().Single(tree => tree.Name == "ChangesTree")
            .GetLogicalDescendants().OfType<TreeViewItem>().Single(node => Equals(node.Tag, path));
}