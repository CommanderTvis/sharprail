using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit.Markdown;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>Plugins' file actions (W21) in the context menus of the Files panel, the code editor and the Markdown preview.</summary>
internal static class FileActionChecks
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "file-actions"));
        Until(() => app.Window.WorkspaceMounted);
        var ran = new List<FileActionTarget>();
        static string Describe(FileActionTarget target) =>
            target.StartLine is { } start ? $"{target.Path} {start}-{target.EndLine}" : target.Path + (target.IsDirectory ? "/" : "");
        app.Workbench.PluginRegistry.AddFileAction("probe", new("probe", target => target.Path == "DIAGRAM.md" ? null : "Probe " + Describe(target), ran.Add));

        MenuItem[] Opened(ContextMenu menu) { Until(() => menu.IsOpen); return [.. menu.Items.OfType<MenuItem>()]; }
        void Choose(ContextMenu menu, string title)
        {
            var item = Opened(menu).SingleOrDefault(item => Equals(item.Header, title)) ??
                throw new InvalidOperationException($"The menu offers [{string.Join(", ", menu.Items.OfType<MenuItem>().Select(item => item.Header))}], not '{title}'.");
            Require(item.Name == "FileAction_probe_probe", "A contributed item is named after its plugin and action.");
            TopLevel.GetTopLevel(item)!.UpdateLayout();
            app.Click(item, freshGesture: false);
            Until(() => !menu.IsOpen);
        }
        ContextMenu FileMenu(string path)
        {
            var row = app.FileRow(path);
            app.Click(row, mouseButton: MouseButton.Right);
            return row.GetLogicalAncestors().OfType<TreeViewItem>().First().ContextMenu!;
        }

        Choose(FileMenu("notes.txt"), "Probe notes.txt");
        Choose(FileMenu("styles"), "Probe styles/");
        Require(ran is [{ Path: "notes.txt", IsDirectory: false, StartLine: null }, { Path: "styles", IsDirectory: true }] && ran[0].WorkspaceId == app.Window.WorkspaceRoot,
            "A Files row offers the action for its file or folder in the window's workspace.");
        var withheld = FileMenu("DIAGRAM.md");
        Require(Opened(withheld).All(item => item.Name?.StartsWith("FileAction_", StringComparison.Ordinal) != true) && withheld.Items[^1] is not Separator,
            "An action that names no label for a target is not offered, and leaves no separator behind.");
        withheld.Close();
        Choose(FileMenu("notes.txt"), "Probe notes.txt");
        Require(ran.Count == 3, "Reopening a row's menu offers the action once.");
        ran.Clear();

        app.Open("README.md", keep: true);
        Until(() => app.Window.GetLogicalDescendants().OfType<MarkdownPreview>().Any(candidate => candidate.IsEffectivelyVisible));
        var preview = app.Window.GetLogicalDescendants().OfType<MarkdownPreview>().Single(candidate => candidate.IsEffectivelyVisible);
        var heading = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().First();
        app.Click(heading, mouseButton: MouseButton.Right);
        Choose(preview.ContextMenu!, "Probe README.md");
        heading.SelectAll(); Dispatcher.UIThread.RunJobs();
        app.Click(heading, mouseButton: MouseButton.Right);
        Choose(preview.ContextMenu!, "Probe README.md 1-1");
        Require(ran is [{ StartLine: null }, { Path: "README.md", StartLine: 1, EndLine: 1 }],
            "The Markdown preview offers the action for the file, and for the source lines of a selection.");
        ran.Clear();

        if (OperatingSystem.IsMacOS())
        {
            File.WriteAllText(Path.Combine(app.Root, "lines.txt"), "one\ntwo\nthree\nfour\n");
            Until(() => app.Window.GetLogicalDescendants().OfType<TreeViewItem>().Any(item => item.Tag is SharpRail.Host.Abstractions.ProjectFile { Path: "lines.txt" }));
            app.Open("lines.txt", keep: true);
            Until(() => app.Window.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Any(candidate => candidate.IsEffectivelyVisible && candidate.Text.StartsWith("one", StringComparison.Ordinal)));
            var editor = app.Window.GetLogicalDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Single(candidate => candidate.IsEffectivelyVisible);
            app.Click(editor, mouseButton: MouseButton.Right);
            Choose(editor.ContextMenu!, "Probe lines.txt");
            // "two\nthree\n": the selection stops at the start of line four, which it does not include.
            editor.SelectRange(4, 10); Dispatcher.UIThread.RunJobs();
            app.Click(editor, mouseButton: MouseButton.Right);
            Choose(editor.ContextMenu!, "Probe lines.txt 2-3");
            Require(ran is [{ StartLine: null }, { Path: "lines.txt", StartLine: 2, EndLine: 3 }], "The code editor offers the action for the file, and for its selected lines.");
        }
        app.Workbench.PluginRegistry.RemovePlugin("probe");
        Console.WriteLine("PASS file actions: Files rows, the Markdown preview and the code editor offer plugins' actions for a file and its selected lines");
    }
}