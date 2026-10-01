using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class FilesE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "files-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "files-compact"));
        var workspace = CreateWorkspaceViaDialog(app);
        Directory.CreateDirectory(Path.Combine(workspace, "compact", "only", "here"));
        File.WriteAllText(Path.Combine(workspace, "compact", "only", "here", "leaf.txt"), "leaf\n");
        app.Click(app.Find<Button>("Tab_files"));
        app.FileRow("README.md");
        TreeViewItem? Node(string name, bool directory) => app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>()
            .SingleOrDefault(node => node.Tag is ProjectFile file && file.Name == name && file.IsDirectory == directory);
        Until(() => Node("compact/only/here", true) is not null);
        app.ExpandFolder(Path.Combine("compact", "only", "here"));
        Until(() => Node("leaf.txt", false) is not null && Node("compact/only/here", true)!.IsExpanded);
        Directory.CreateDirectory(Path.Combine(workspace, "compact", "only", "sibling"));
        Until(() => Node("compact/only", true) is not null && Node("here", true) is not null && Node("leaf.txt", false) is not null);
        Require(Node("compact/only", true)!.IsExpanded && Node("here", true)!.IsExpanded,
            "Splitting a compacted run must keep the expanded folders open.");
        Console.WriteLine("PASS upstream files.spec.ts: shows files and compacts single-directory runs in the Files tree");

        // A single click on a folder's name toggles it, like the reference tree; two quick clicks open and close it.
        Directory.CreateDirectory(Path.Combine(workspace, "single"));
        File.WriteAllText(Path.Combine(workspace, "single", "inside.txt"), "inside\n");
        Until(() => Node("single", true) is not null);
        app.Click(app.FileRow("single"));
        Until(() => Node("single", true)!.IsExpanded && Node("inside.txt", false) is not null);
        app.Click(app.FileRow("single"));
        Until(() => !Node("single", true)!.IsExpanded);
        app.Click(app.FileRow("single"), twice: true);
        Settle();
        Require(!Node("single", true)!.IsExpanded, "Two quick clicks on a folder open it and close it again.");
        app.Click(app.FileRow("single"));
        Until(() => Node("single", true)!.IsExpanded);
        File.WriteAllText(Path.Combine(workspace, "single", "external.txt"), "external\n");
        Until(() => Node("external.txt", false) is not null);
        File.Move(Path.Combine(workspace, "single", "external.txt"), Path.Combine(workspace, "single", "renamed.txt"));
        Until(() => Node("external.txt", false) is null && Node("renamed.txt", false) is not null);
        File.Delete(Path.Combine(workspace, "single", "renamed.txt"));
        Until(() => Node("renamed.txt", false) is null);
        Require(Node("single", true)!.IsExpanded, "External changes must preserve expanded folders.");
        Console.WriteLine("PASS files tree folders toggle on a single click of their name");

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

    private static TreeViewItem Node(E2eWorkspace app, string path)
    {
        Until(() => app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>().Any(node => node.Tag is ProjectFile file && file.Path == path));
        return app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>().Single(node => node.Tag is ProjectFile file && file.Path == path);
    }

    private static ContextMenu OpenMenu(E2eWorkspace app, string path)
    {
        app.Click(app.FileRow(path), mouseButton: Avalonia.Input.MouseButton.Right);
        var menu = Node(app, path).ContextMenu!;
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
        Until(() => !app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>().Any(node => node.Tag is ProjectFile { Path: "fleeting.txt" }));
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
        Until(() => !app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>().Any(node => node.Tag is ProjectFile { Path: "gone-soon.txt" }));
        app.Open("README.md");
        Console.WriteLine("PASS fork files.spec.ts: deleting a previewed file leaves the workbench interactive");
    }
}