using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class FilesE2E
{
    internal static void Transfers(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "file-actions"));
        var source = Path.Combine(app.Root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        File.WriteAllText(Path.Combine(source, "top.txt"), "top");
        byte[] bytes = [0, 255, 1, 42];
        File.WriteAllBytes(Path.Combine(source, "nested", "data.bin"), bytes);
        Node(app, "source");
        foreach (var path in new[] { "notes.txt", "source" })
        {
            var menu = OpenMenu(app, path);
            Require(Equals(Entry(menu, "FileNodeMove").Header, "Move…") && Equals(Entry(menu, "FileNodeCopy").Header, "Copy…"),
                "Both files and directories must offer explicit Move and Copy actions.");
            menu.Close();
        }
        Choose(app, "notes.txt", "FileNodeCopy");
        var dialog = Dialog(app);
        var input = Named<TextBox>(dialog, "PathNameInput");
        Until(() => input.IsFocused);
        Require(input.SelectionStart == 0 && input.SelectionEnd == input.Text!.Length &&
            Named<TextBlock>(dialog, "DialogExplanation").Text!.Contains("relative to this workspace", StringComparison.Ordinal),
            "Transfers select the whole destination path and explain that it is workspace-relative.");
        input.Text = "cancelled.txt";
        Cancel(dialog);
        Require(!File.Exists(Path.Combine(app.Root, "cancelled.txt")) && File.Exists(Path.Combine(app.Root, "notes.txt")), "Canceling the destination dialog must not copy or move anything.");

        Transfer("notes.txt", "copied.txt", copy: true);
        Require(File.ReadAllText(Path.Combine(app.Root, "copied.txt")) == File.ReadAllText(Path.Combine(app.Root, "notes.txt")), "Copy must preserve the source and file contents.");
        Transfer("copied.txt", "destination/inner/moved.txt", copy: false);
        Require(!File.Exists(Path.Combine(app.Root, "copied.txt")) && File.ReadAllText(Path.Combine(app.Root, "destination", "inner", "moved.txt")) == File.ReadAllText(Path.Combine(app.Root, "notes.txt")), "Move must remove its old path and preserve bytes at the destination.");
        Node(app, "destination/inner/moved.txt");
        Transfer("source", "folder-copy", copy: true);
        Require(Directory.Exists(source) && File.ReadAllBytes(Path.Combine(app.Root, "folder-copy", "nested", "data.bin")).SequenceEqual(bytes) && Directory.Exists(Path.Combine(app.Root, "folder-copy", "nested", "empty")), "Folder copy must retain its source, nested binary bytes and empty folders.");
        Transfer("folder-copy", "folder-moved", copy: false);
        Require(!Directory.Exists(Path.Combine(app.Root, "folder-copy")) && File.ReadAllBytes(Path.Combine(app.Root, "folder-moved", "nested", "data.bin")).SequenceEqual(bytes), "Folder move must relocate the complete tree.");

        Choose(app, "source", "FileNodeMove");
        dialog = Dialog(app);
        input = Named<TextBox>(dialog, "PathNameInput");
        var confirm = Named<Button>(dialog, "PathNameConfirm");
        var error = Named<TextBlock>(dialog, "PathNameError");
        Require(!confirm.IsEnabled, "Moving an entry to itself must not be confirmable.");
        input.Text = "source/child";
        app.Click(confirm);
        Until(() => error.IsVisible && error.Text!.Contains("outside the source", StringComparison.Ordinal));
        Require(!Directory.Exists(Path.Combine(source, "child")), "A refused descendant move must not create directories inside its source.");
        input.Text = "../escape";
        Until(() => !confirm.IsEnabled);
        input.Text = "README.md";
        Until(() => !confirm.IsEnabled && error.Text!.Contains("already exists", StringComparison.Ordinal));
        Cancel(dialog);

        Choose(app, "notes.txt", "FileNodeCopy");
        dialog = Dialog(app);
        input = Named<TextBox>(dialog, "PathNameInput");
        confirm = Named<Button>(dialog, "PathNameConfirm");
        error = Named<TextBlock>(dialog, "PathNameError");
        input.Text = "late-collision.txt";
        File.WriteAllText(Path.Combine(app.Root, "late-collision.txt"), "keep existing");
        app.Click(confirm);
        Until(() => error.IsVisible && error.Text!.Contains("already exists", StringComparison.Ordinal));
        Require(File.ReadAllText(Path.Combine(app.Root, "late-collision.txt")) == "keep existing" && File.Exists(Path.Combine(app.Root, "notes.txt")), "A late host collision must keep both destination and source, with the dialog available for correction.");
        Cancel(dialog);
        Console.WriteLine("PASS file-tree Move/Copy actions: files, folders, contents, cancellation, self/descendant paths and non-overwriting collisions");

        void Cancel(Window modal)
        {
            app.Click(modal.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" }));
            Until(() => !app.Window.OwnedWindows.Any());
        }
        void Transfer(string from, string to, bool copy)
        {
            Choose(app, from, copy ? "FileNodeCopy" : "FileNodeMove");
            var modal = Dialog(app);
            Named<TextBox>(modal, "PathNameInput").Text = to;
            app.Click(Named<Button>(modal, "PathNameConfirm"));
            Until(() => !app.Window.OwnedWindows.Any());
        }
    }

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "files-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "files-compact"));
        var workspace = CreateWorkspaceViaDialog(app);
        Directory.CreateDirectory(Path.Combine(workspace, "compact", "only", "here"));
        File.WriteAllText(Path.Combine(workspace, "compact", "only", "here", "leaf.txt"), "leaf\n");
        app.ShowTool("files");
        app.FileRow("README.md");
        DrawnTreeEntry? Node(string name, bool directory) => app.Find<DrawnTree>("FilesTree").Rows
            .SingleOrDefault(node => node.Tag is ProjectFile file && file.Name == name && file.IsDirectory == directory);
        Until(() => Node("compact/only/here", true) is not null);
        // Dotfiles are ordinary entries of a developer's tree, listed and visible like the reference's.
        File.WriteAllText(Path.Combine(workspace, ".gitignore"), "bin/\n");
        Until(() => Node(".gitignore", false) is not null);
        Directory.CreateDirectory(Path.Combine(workspace, "bin"));
        File.WriteAllText(Path.Combine(workspace, "bin", "ignored.txt"), "ignored\n");
        Until(() => Node("bin", true)?.Tag is ProjectFile { GitIgnored: true });
        var ignored = Node("bin", true)!;
        var ignoredRow = (DrawnTreeLabel)app.FileRow("bin");
        Require(ignored.Italic && ignored.Muted &&
            ReferenceEquals(((Border)ignoredRow.Child!).Background, Ui.Hint) &&
            Equals(ToolTip.GetTip(ignoredRow), "Ignored by git"),
            "Ignored rows must have an italic label, dimmed icon and explanatory tooltip.");
        app.Click(app.FileRow("bin"));
        Until(() => Node("ignored.txt", false) is not null);
        app.Click(app.FileRow("bin/ignored.txt"));
        Until(() => app.Tabs.Any(tab => tab.Path == "bin/ignored.txt"));
        // A rail narrower than its names scrolls sideways instead of cutting them off.
        var filesTree = app.Find<DrawnTree>("FilesTree");
        var previousWidth = filesTree.Width;
        filesTree.Width = 60;
        Until(() => { app.Window.UpdateLayout(); return filesTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } sideways && sideways.Extent.Width > sideways.Viewport.Width + 1; });
        var label = (DrawnTreeLabel)app.FileRow("compact/only/here");
        var full = new TextBlock { Text = label.Entry.Text, FontSize = Ui.FontSize, FontFamily = Ui.InterfaceFont, FontWeight = Ui.InterfaceWeight };
        full.Measure(Size.Infinity);
        Require(label.Bounds.Width >= full.DesiredSize.Width + 42 - 0.5, "A narrow Files rail keeps whole names alongside its icon and chevron.");
        filesTree.Width = previousWidth;
        app.ExpandFolder(Path.Combine("compact", "only", "here"));
        Until(() => Node("leaf.txt", false) is not null && Node("compact/only/here", true)!.Expanded);
        Directory.CreateDirectory(Path.Combine(workspace, "compact", "only", "sibling"));
        Until(() => Node("compact/only", true) is not null && Node("here", true) is not null && Node("leaf.txt", false) is not null);
        Require(Node("compact/only", true)!.Expanded && Node("here", true)!.Expanded,
            "Splitting a compacted run must keep the expanded folders open.");
        Console.WriteLine("PASS upstream files.spec.ts: shows files and compacts single-directory runs in the Files tree");

        // A single click on a folder's name toggles it, like the reference tree; two quick clicks open and close it.
        Directory.CreateDirectory(Path.Combine(workspace, "single"));
        File.WriteAllText(Path.Combine(workspace, "single", "inside.txt"), "inside\n");
        Until(() => Node("single", true) is not null);
        app.Click(app.FileRow("single"));
        Until(() => Node("single", true)!.Expanded && Node("inside.txt", false) is not null);
        app.Click(app.FileRow("single"));
        Until(() => !Node("single", true)!.Expanded);
        app.Click(app.FileRow("single"), twice: true);
        Settle();
        Require(!Node("single", true)!.Expanded, "Two quick clicks on a folder open it and close it again.");
        app.Click(app.FileRow("single"));
        Until(() => Node("single", true)!.Expanded);
        File.WriteAllText(Path.Combine(workspace, "single", "external.txt"), "external\n");
        Until(() => Node("external.txt", false) is not null);
        File.Move(Path.Combine(workspace, "single", "external.txt"), Path.Combine(workspace, "single", "renamed.txt"));
        Until(() => Node("external.txt", false) is null && Node("renamed.txt", false) is not null);
        File.Delete(Path.Combine(workspace, "single", "renamed.txt"));
        Until(() => Node("renamed.txt", false) is null);
        Require(Node("single", true)!.Expanded, "External changes must preserve expanded folders.");
        Console.WriteLine("PASS files tree folders toggle on a single click of their name");

        // Deleting expanded folders, nested or replaced by a file, removes them and keeps expanded siblings open.
        Directory.CreateDirectory(Path.Combine(workspace, "gone", "inner", "deep"));
        File.WriteAllText(Path.Combine(workspace, "gone", "keep.txt"), "k\n");
        File.WriteAllText(Path.Combine(workspace, "gone", "inner", "deep", "x.txt"), "x\n");
        Directory.CreateDirectory(Path.Combine(workspace, "swap", "sub"));
        File.WriteAllText(Path.Combine(workspace, "swap", "a.txt"), "a\n");
        Until(() => Node("gone", true) is not null && Node("swap", true) is not null);
        app.Click(app.FileRow("gone"));
        Until(() => Node("keep.txt", false) is not null);
        app.ExpandFolder(Path.Combine("gone", "inner", "deep"));
        Until(() => Node("x.txt", false) is not null);
        app.Click(app.FileRow("swap"));
        Until(() => Node("a.txt", false) is not null);
        Directory.Delete(Path.Combine(workspace, "gone", "inner"), true);
        Until(() => Node("x.txt", false) is null && Node("deep", true) is null);
        Require(Node("keep.txt", false) is not null && Node("single", true)!.Expanded, "Deleting a nested folder keeps its expanded siblings.");
        Directory.Delete(Path.Combine(workspace, "gone"), true);
        Until(() => Node("gone", true) is null && Node("keep.txt", false) is null);
        Directory.Delete(Path.Combine(workspace, "swap"), true);
        File.WriteAllText(Path.Combine(workspace, "swap"), "now a file\n");
        Until(() => Node("swap", true) is null && Node("swap", false) is not null && Node("a.txt", false) is null);
        Require(Node("single", true)!.Expanded, "Removing folders keeps unrelated expanded folders open.");
        Console.WriteLine("PASS files tree drops deleted and replaced folders");

        File.WriteAllBytes(Path.Combine(workspace, "blob.bin"), [0xFF, 0xFE, 0x00, 0x01]);
        Until(() => Node("blob.bin", false) is not null);
        app.Click(app.FileRow("blob.bin"));
        Until(() => app.Window.GetLogicalDescendants().OfType<Border>().Any(card => card.Name == "BinaryResource" &&
            card.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text!.Contains("4 bytes", StringComparison.Ordinal))));
        Require(!app.Find<TextBlock>("WorkspaceError").IsVisible, "Opening a byte-only file is a card in its tab, not a window error.");
        Console.WriteLine("PASS SharpRail: a byte-only file opens as a card with its size instead of failing");

        RowMenu(app, workspace);
        CreateInsideFolder(app, workspace);
        CreateBesideAndRename(app, workspace);
        DeletedOnDisk(app, workspace);
        if (OperatingSystem.IsMacOS()) DeleteToTrash(app, workspace);
    }

    /// <summary>An open tab follows its file through a rename in the Files tree and one made outside SharpRail.</summary>
    internal static void RenameFollows(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "rename-follows"));
        var workspace = app.Root;
        File.WriteAllText(Path.Combine(workspace, "alpha.txt"), "alpha body\nsecond line\n");
        Node(app, "alpha.txt");
        app.Open("alpha.txt", keep: true);
        var tabId = app.Tabs.Single(tab => tab.Path == "alpha.txt").Id;
        Control Mark() => app.Find<Control>("DockTab_" + tabId.Replace(':', '_').Replace('/', '_'))
            .GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "DeletedTab");
        SharpRail.UI.Editor.CodeDocumentView View() => app.Window.GetVisualDescendants().OfType<SharpRail.UI.Editor.CodeDocumentView>().Single();
        Until(() => app.Window.GetVisualDescendants().OfType<SharpRail.UI.Editor.CodeDocumentView>().Any());
        var view = View();
        view.Editor.ReplaceRange(0, 0, "edited ");
        view.Editor.SelectRange(2, 3);
        view.Editor.Focus();
        Until(() => view.HasPendingChanges && view.Editor.IsFocused);

        void Follows(string path, string how)
        {
            Until(() => app.Tabs.Any(tab => tab.Path == path) && app.Window.Layout.Selected(app.Center)?.Path == path);
            var tab = app.Tabs.Single(tab => tab.Path == path);
            Require(tab.Title == Path.GetFileName(path) && app.Tabs.Count(item => item.Kind == "file") == 1,
                $"{how}: the tab takes the new name instead of opening a second tab or keeping the old one.");
            Require(ReferenceEquals(View(), view) && view.FilePath == path, $"{how}: the same editor follows the file, so undo and scroll stay.");
            Require(view.HasPendingChanges && view.Editor.Text.StartsWith("edited alpha", StringComparison.Ordinal), $"{how}: unsaved edits are kept.");
            Require(view.Editor.Selection.Text == "ite", $"{how}: the selection is kept.");
            Require(view.Editor.IsFocused, $"{how}: focus is kept.");
            Require(!Mark().IsVisible && !app.Find<Border>("FileDeletedOnDisk").IsVisible && !app.Find<TextBlock>("WorkspaceError").IsVisible,
                $"{how}: a renamed file is not reported as deleted.");
        }

        Choose(app, "alpha.txt", "FileNodeRename");
        var dialog = Dialog(app);
        Named<TextBox>(dialog, "PathNameInput").Text = "beta.cs";
        app.Click(Named<Button>(dialog, "PathNameConfirm"));
        Until(() => !app.Window.OwnedWindows.Any());
        view.Editor.Focus();
        Follows("beta.cs", "Files rename");
        Settle(800);
        Follows("beta.cs", "Files rename after the watcher batch");
        Console.WriteLine("PASS SharpRail: renaming an open file in the Files tree keeps its tab, edits and selection under the new name");

        File.Move(Path.Combine(workspace, "beta.cs"), Path.Combine(workspace, "gamma.cs"));
        Follows("gamma.cs", "External mv");
        Console.WriteLine("PASS SharpRail: an open file moved outside SharpRail keeps its tab, edits and selection under the new name");

        _ = app.Window.SaveSelectedAsync();
        Until(() => !view.HasPendingChanges);
        Settle(800);
        Require(File.ReadAllText(Path.Combine(workspace, "gamma.cs")).StartsWith("edited alpha", StringComparison.Ordinal) &&
            !File.Exists(Path.Combine(workspace, "alpha.txt")) && !File.Exists(Path.Combine(workspace, "beta.cs")),
            "Saving after a rename writes the new path and never recreates an old one.");
        Console.WriteLine("PASS SharpRail: saving a followed tab writes its new path");

        File.WriteAllText(Path.Combine(workspace, "notes.md"), "# Notes\n\nbody\n");
        Node(app, "notes.md");
        app.Open("notes.md", keep: true);
        var preview = app.Tabs.Single(tab => tab.Path == "notes.md").Id;
        Control PreviewMark() => app.Find<Control>("DockTab_" + preview.Replace(':', '_').Replace('/', '_'))
            .GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "DeletedTab");
        File.Move(Path.Combine(workspace, "notes.md"), Path.Combine(workspace, "renamed-notes.md"));
        Until(() => app.Tabs.Any(tab => tab.Path == "renamed-notes.md" && tab.Title == "renamed-notes.md") && !app.Tabs.Any(tab => tab.Path == "notes.md"));
        Settle(800);
        Require(!PreviewMark().IsVisible && !app.Find<TextBlock>("WorkspaceError").IsVisible && app.Window.Layout.Selected(app.Center)?.Path == "renamed-notes.md",
            "A renamed preview tab follows its file without an error state.");
        Console.WriteLine("PASS SharpRail: a Markdown preview tab follows its file through an outside rename");

        app.Window.Layout.Select(app.Center, tabId);
        File.Delete(Path.Combine(workspace, "gamma.cs"));
        Until(() => Mark().IsVisible);
        Require(app.Tabs.Any(tab => tab.Path == "gamma.cs"), "A deleted file keeps its tab, marked deleted.");
        Console.WriteLine("PASS SharpRail: a deleted file still marks its tab deleted");
    }

    private static DrawnTreeEntry Node(E2eWorkspace app, string path)
    {
        Until(() => app.Find<DrawnTree>("FilesTree").Rows.Any(node => node.Tag is ProjectFile file && file.Path == path));
        return app.Find<DrawnTree>("FilesTree").Rows.Single(node => node.Tag is ProjectFile file && file.Path == path);
    }

    private static ContextMenu OpenMenu(E2eWorkspace app, string path)
    {
        app.Click(app.FileRow(path), mouseButton: Avalonia.Input.MouseButton.Right);
        var menu = app.FileRow(path).ContextMenu!;
        Until(() => menu.IsOpen);
        return menu;
    }

    private static MenuItem Entry(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);

    private static void Choose(E2eWorkspace app, string path, string entry)
    {
        var menu = OpenMenu(app, path);
        app.Click(Entry(menu, entry), freshGesture: false);
    }

    private static void RowMenu(E2eWorkspace app, string workspace)
    {
        var menu = OpenMenu(app, "README.md");
        Require(Entry(menu, "FileNodeReveal").IsVisible && Entry(menu, "FileNodeCopyPath").IsVisible && Equals(Entry(menu, "FileNodeDelete").Header, "Delete file"),
            "A file row's own menu offers reveal, copy path and delete.");
        Avalonia.Controls.TopLevel.GetTopLevel(Entry(menu, "FileNodeReveal"))!
            .KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Until(() => !menu.IsOpen);
        Console.WriteLine("PASS fork files.spec.ts: a file row has our own context menu, not the webview's");

        Choose(app, "README.md", "FileNodeCopyPath");
        var copied = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => copied.IsCompleted);
        Require(copied.Result == Path.Combine(workspace, "README.md"), "Copy absolute path must copy the file's host path.");
        Console.WriteLine("PASS fork files.spec.ts: file and compacted folder menus copy their host absolute paths (file row)");
    }

    private static void CreateInsideFolder(E2eWorkspace app, string workspace)
    {
        Directory.CreateDirectory(Path.Combine(workspace, "docs", "nested"));
        File.WriteAllText(Path.Combine(workspace, "docs", "keep.txt"), "");
        Node(app, "docs");
        Choose(app, "docs", "FileNodeNewFile");
        var dialog = Dialog(app);
        var input = Named<TextBox>(dialog, "PathNameInput");
        var confirm = Named<Button>(dialog, "PathNameConfirm");
        var error = Named<TextBlock>(dialog, "PathNameError");
        Require(!confirm.IsEnabled, "An empty name cannot be confirmed.");
        input.Text = "keep.txt";
        Until(() => error.IsVisible && error.Text!.Contains("keep.txt already exists", StringComparison.Ordinal) && !confirm.IsEnabled);
        input.Text = "notes.md";
        Until(() => !error.IsVisible && confirm.IsEnabled);
        app.Click(confirm);
        Until(() => !app.Window.OwnedWindows.Any());
        var created = Path.Combine("docs", "notes.md");
        Require(File.Exists(Path.Combine(workspace, created)), "New file must create the file inside the folder.");
        Until(() => app.Tabs.Any(tab => tab.Path == created && !tab.Preview));
        Node(app, created);
        Console.WriteLine("PASS fork files.spec.ts: a folder row creates a file inside it, whose icon follows the name as it is typed (no per-type tree icons)");
        Console.WriteLine("PASS fork files.spec.ts: a new file warns as soon as its name already exists");
    }

    private static void CreateBesideAndRename(E2eWorkspace app, string workspace)
    {
        File.WriteAllText(Path.Combine(workspace, "draft.txt"), "draft\n");
        Node(app, "draft.txt");
        Choose(app, "draft.txt", "FileNodeNewFolder");
        var dialog = Dialog(app);
        Named<TextBox>(dialog, "PathNameInput").Text = "assets";
        app.Click(Named<Button>(dialog, "PathNameConfirm"));
        Until(() => !app.Window.OwnedWindows.Any());
        Require(Directory.Exists(Path.Combine(workspace, "assets")), "New folder beside a file must create it in the file's folder.");
        Node(app, "assets");

        Choose(app, "draft.txt", "FileNodeRename");
        dialog = Dialog(app);
        var input = Named<TextBox>(dialog, "PathNameInput");
        Until(() => input.IsFocused);
        Require(input.Text == "draft.txt" && input.SelectionStart == 0 && input.SelectionEnd == 5, "Rename must select the stem so typing keeps the extension.");
        input.Text = "final.txt";
        app.Click(Named<Button>(dialog, "PathNameConfirm"));
        Until(() => !app.Window.OwnedWindows.Any());
        Require(File.Exists(Path.Combine(workspace, "final.txt")) && !File.Exists(Path.Combine(workspace, "draft.txt")), "Rename must move the file.");
        Node(app, "final.txt");
        Console.WriteLine("PASS fork files.spec.ts: a file row creates a folder beside it, and renames itself");
    }

    private static void DeletedOnDisk(E2eWorkspace app, string workspace)
    {
        var file = Path.Combine(workspace, "fleeting.txt");
        File.WriteAllText(file, "here for now\n");
        Node(app, "fleeting.txt");
        app.Open("fleeting.txt", keep: true);
        Control Mark() => app.Find<Control>("DockTab_file_fleeting.txt").GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "DeletedTab");
        Require(!Mark().IsVisible, "A present file's tab is not marked deleted.");
        File.Delete(file);
        Until(() => Mark().IsVisible);
        if (OperatingSystem.IsMacOS()) Until(() => app.Find<Border>("FileDeletedOnDisk").IsVisible);
        Until(() => !app.Find<DrawnTree>("FilesTree").Rows.Any(node => node.Tag is ProjectFile { Path: "fleeting.txt" }));
        File.WriteAllText(file, "back again\n");
        Until(() => !Mark().IsVisible);
        if (OperatingSystem.IsMacOS()) Until(() => !app.Find<Border>("FileDeletedOnDisk").IsVisible);
        Console.WriteLine("PASS fork files.spec.ts: an open tab says when its file is deleted on disk, and recovers when it returns");
    }

    private static void DeleteToTrash(E2eWorkspace app, string workspace)
    {
        var disposable = Path.Combine(workspace, "gone-soon.txt");
        File.WriteAllText(disposable, "gone soon\n");
        Node(app, "gone-soon.txt");
        app.Open("gone-soon.txt");
        Choose(app, "gone-soon.txt", "FileNodeDelete");
        var confirm = Dialog(app);
        Require(confirm.Title == "Delete gone-soon.txt?", "Deleting a file asks first.");
        app.Click(Named<Button>(confirm, "FileNodeDeleteConfirm"));
        Until(() => !app.Window.OwnedWindows.Any() && !File.Exists(disposable));
        Until(() => !app.Find<DrawnTree>("FilesTree").Rows.Any(node => node.Tag is ProjectFile { Path: "gone-soon.txt" }));
        app.Open("README.md");
        Console.WriteLine("PASS fork files.spec.ts: deleting a previewed file leaves the workbench interactive");
    }
}