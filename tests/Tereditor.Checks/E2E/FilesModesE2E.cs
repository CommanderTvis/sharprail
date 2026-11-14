using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;

using Tereditor.Host.Abstractions;
using Tereditor.UI.Docking;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks.E2E;

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
        GoToFile(root);
    }

    /// <summary>
    /// Go to file: the name match ranks a whole name over a spread one over a folder one, a slash matches path segments,
    /// Changed searches only changes, Enter opens the selected match, Escape brings the tree back as it was, and a
    /// reply to a superseded query never replaces a newer one.
    /// </summary>
    private static void GoToFile(string root)
    {
        Require(FileNameMatch.Score("src/fm.txt", "fm") > FileNameMatch.Score("src/FilesModes.cs", "fm") &&
            FileNameMatch.Score("src/FilesModes.cs", "fm") > FileNameMatch.Score("fm/readme.md", "fm") &&
            FileNameMatch.Score("src/FilesModes.cs", "FM") == FileNameMatch.Score("src/FilesModes.cs", "fm") &&
            FileNameMatch.Score("src/fm.txt", "xyz") is null, "A whole name must rank over a spread one over a folder one, ignoring case.");
        var positions = new List<int>();
        Require(FileNameMatch.Score("scripts/tasks/dmg.cs", "tasks/dmg", positions) is not null && positions.SequenceEqual(Enumerable.Range(8, 9)) &&
            FileNameMatch.Score("scripts/tasks/dmg.cs", "scr/dmg") is not null && FileNameMatch.Score("scripts/tasks/dmg.cs", "dmg/tasks") is null,
            "A slash must match path segments in order.");

        var project = IsolatedGit.Repository(Path.Combine(root, "files-find"), (".gitignore", "build/\n"));
        foreach (var path in new[] { "src/fm.txt", "src/FilesModes.cs", "fm/readme.md", "scripts/tasks/dmg.cs", "docs/guide.md", "build/fm.js" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(project, path))!);
            File.WriteAllText(Path.Combine(project, path), path + "\n");
        }
        IsolatedGit.Run(project, "add", "-A");
        IsolatedGit.Run(project, "commit", "-m", "files");
        File.AppendAllText(Path.Combine(project, "src/FilesModes.cs"), "changed\n");

        using var app = new E2eWorkspace(project, profileRoot: project + "-profile");
        Until(() => app.Window.WorkspaceMounted);
        app.ShowTool("files");
        Expand(app, "src");
        var box = app.Find<TextBox>("FilesFindQuery");
        string[] Matches() => app.Find<ListBox>("FilesFindResults").Items.OfType<Control>().Select(row => (string)row.Tag!).ToArray();

        // Mod+P opens the box with focus; the tree hides only while matches show.
        app.Window.Focus();
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        app.Window.KeyPress(Key.P, command, PhysicalKey.P, "p");
        app.Window.KeyRelease(Key.P, command, PhysicalKey.P, "p");
        Until(() => box.IsEffectivelyVisible && box.IsFocused);
        Require(Tree(app).IsEffectivelyVisible, "An empty query must leave the tree in view.");
        box.Text = "fm";
        Until(() => Matches().Length == 3);
        Require(Matches().SequenceEqual(["src/fm.txt", "src/FilesModes.cs", "fm/readme.md"]) && !Tree(app).IsEffectivelyVisible,
            $"Matches must show best first in place of the tree, Git-ignored files left out: {string.Join(", ", Matches())}.");

        // Changed searches the changes alone; All again searches every file.
        app.Click(app.Find<ToggleButton>("FilesModeChanged"));
        Until(() => Matches().SequenceEqual(["src/FilesModes.cs"]));
        Require(!app.Find<Grid>("ChangesPanel").IsEffectivelyVisible, "Changed's list must hide while matches show.");
        app.Click(app.Find<ToggleButton>("FilesModeAll"));
        Until(() => Matches().Length == 3);

        // A reply to a superseded query arrives after the newer one's and is dropped.
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Host.FindGate = query => query == "guide" ? late.Task : null;
        box.Text = "guide";
        Until(() => { lock (app.Host.FindHeld) return app.Host.FindHeld.Contains("guide"); });
        box.Text = "tasks/dmg";
        Until(() => Matches().SequenceEqual(["scripts/tasks/dmg.cs"]));
        late.SetResult();
        Settle();
        Require(Matches().SequenceEqual(["scripts/tasks/dmg.cs"]), "A late reply to an older query must not replace the newer matches.");
        app.Host.FindGate = null;

        // Enter opens the selected match and closes the box; the tree is back with its folders as they were.
        box.Focus();
        Until(() => box.IsFocused);
        WorkspaceFixture.Press(box, Key.Enter);
        Until(() => app.Tabs.Any(tab => tab.Path == "scripts/tasks/dmg.cs"));
        Require(!box.IsVisible && Tree(app).IsEffectivelyVisible && Expanded(app, "src"), "Enter must close the box and restore the tree.");

        // Escape closes the box and restores the tree too.
        app.Click(app.Find<Button>("FilesFind"));
        Until(() => box.IsEffectivelyVisible && box.IsFocused);
        box.Text = "fm/readme"; // the workspace fixture has a README.md of its own
        Until(() => Matches().SequenceEqual(["fm/readme.md"]));
        WorkspaceFixture.Press(box, Key.Escape);
        Until(() => !box.IsVisible && Tree(app).IsEffectivelyVisible);
        Require(Expanded(app, "src") && box.Text == "" && Matches().Length == 0, "Escape must clear the box and bring the tree back as it was.");

        // Clearing the query closes the box as well.
        app.Click(app.Find<Button>("FilesFind"));
        Until(() => box.IsEffectivelyVisible && box.IsFocused);
        box.Text = "guide";
        Until(() => Matches().SequenceEqual(["docs/guide.md"]));
        box.Text = "";
        Until(() => !box.IsVisible && Tree(app).IsEffectivelyVisible && Expanded(app, "src"));

        // From another side tab, Mod+P brings Files back with the box focused.
        var group = app.Window.Layout.State.Groups.Single(item => item.Tools.Any(tab => tab.Id == "files")).Id;
        app.Window.Layout.Select(group, DockState.SpecsTool); Settle();
        Require(!TreeShown(app), "Another side tab must hide Files.");
        app.Window.Focus();
        app.Window.KeyPress(Key.P, command, PhysicalKey.P, "p");
        app.Window.KeyRelease(Key.P, command, PhysicalKey.P, "p");
        Until(() => box.IsEffectivelyVisible && box.IsFocused && TreeShown(app));
        Console.WriteLine("PASS Go to file: name matches rank best first, a slash matches folders, Changed narrows, Enter opens, Escape restores, late replies drop");
    }

    private static DrawnTree Tree(E2eWorkspace app) => app.Find<DrawnTree>("FilesTree");

    // A side tab that is not selected leaves the logical tree, so a hidden Files has no tree to find.
    private static bool TreeShown(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<DrawnTree>().Any(tree => tree.Name == "FilesTree" && tree.IsEffectivelyVisible);

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