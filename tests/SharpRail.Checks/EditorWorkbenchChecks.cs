using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Editor;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class EditorWorkbenchChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Dispatcher.UIThread.RunJobs();
        Require(done(), "Editor workbench operation timed out.");
    }
    private static void Await(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static ScintillaEditor Editor(Window window) => window.GetLogicalDescendants().OfType<ScintillaEditor>().Single();
    private static bool Dotted(Window window, string tab) =>
        window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "DockTab_file_" + tab)
            .GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "ModifiedTab").IsVisible;
    private static void Answer(Window window, string label)
    {
        Pump(() => window.OwnedWindows.OfType<DialogWindow>().Any());
        var dialog = window.OwnedWindows.OfType<DialogWindow>().Single();
        dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: var text } && text == label)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(() => !dialog.IsVisible);
    }
    private static void Save(Window window, RawInputModifiers command = RawInputModifiers.Meta)
    {
        window.KeyPress(Key.S, command, PhysicalKey.S, "s");
        window.KeyRelease(Key.S, command, PhysicalKey.S, "s");
    }
    internal static void Run(string fixture)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(fixture, "editor-workbench"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + Path.Combine(root, "missing"));
        File.WriteAllText(Path.Combine(root, "first.cs"), "class First {}\n");
        File.WriteAllText(Path.Combine(root, "second.cs"), "class Second {}\n");
        var host = new LocalProjectAdapter(new ProjectServices(root));
        var window = new WorkbenchWindow(host, root, new ProfileStore(Path.Combine(root, ".profile")));
        window.Show(); Pump(() => window.WorkspaceMounted);
        try
        {
            Await(window.OpenDocumentAsync("first.cs")); window.UpdateLayout();
            var editor = Editor(window); editor.Focus();
            window.KeyTextInput("// edited\n"); Dispatcher.UIThread.RunJobs();
            Require(editor.IsFocused, "Keeping an edited preview lost keyboard focus.");
            window.KeyTextInput("// continued typing\n"); Dispatcher.UIThread.RunJobs();
            Require(editor.Text.Contains("continued typing", StringComparison.Ordinal), "Typing stopped after keeping a preview.");
            var group = window.Layout.View.FocusedCenter;
            Require(editor.IsModified && window.Layout.Tabs(group).Single(tab => tab.Path == "first.cs").Preview == false,
                "Editing did not retain the preview tab.");
            Require(Dotted(window, "first.cs"), "Modified tab did not show the unsaved dot.");
            window.Layout.Close(group, "file:first.cs");
            Answer(window, "Cancel");
            Require(window.Layout.Tabs(group).Any(tab => tab.Path == "first.cs") && editor.IsModified, "Cancelling the save prompt closed a dirty editor.");
            window.Close(); Answer(window, "Cancel");
            Require(window.IsVisible, "Window closed with unsaved editor text.");
            var edited = editor.Text;
            Await(window.OpenDocumentAsync("first.cs", true));
            Require(ReferenceEquals(editor, Editor(window)) && editor.Text == edited, "Reopening replaced the live editor buffer.");
            Await(window.OpenDocumentAsync("second.cs", true));
            Await(window.OpenDocumentAsync("first.cs", true));
            Require(ReferenceEquals(editor, Editor(window)) && editor.Text == edited, "Tab switching discarded edits.");
            editor.Focus();
            Save(window);
            Pump(() => !editor.IsModified);
            Require(File.ReadAllText(Path.Combine(root, "first.cs")) == edited, "Cmd+S did not save the workbench file.");
            Require(!Dotted(window, "first.cs"), "Saved tab kept the unsaved dot.");
            window.KeyTextInput("// ctrl\n"); Dispatcher.UIThread.RunJobs();
            Save(window, RawInputModifiers.Control);
            Pump(() => !editor.IsModified);
            edited = editor.Text;
            Require(File.ReadAllText(Path.Combine(root, "first.cs")) == edited, "Ctrl+S did not save the workbench file.");
            window.KeyTextInput("// conflict\n"); Dispatcher.UIThread.RunJobs();
            File.WriteAllText(Path.Combine(root, "first.cs"), "external change\n");
            Save(window);
            Pump(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "WorkspaceError" && (text.Text?.Contains("changed on disk", StringComparison.Ordinal) ?? false)));
            Require(editor.IsModified && File.ReadAllText(Path.Combine(root, "first.cs")) == "external change\n", "Conflicting save overwrote external edits or lost the buffer.");
            window.Layout.Close(group, "file:first.cs");
            Answer(window, "Don't Save");
            Require(window.Layout.Tabs(group).All(tab => tab.Path != "first.cs") && File.ReadAllText(Path.Combine(root, "first.cs")) == "external change\n",
                "Don't Save did not close the tab without writing.");
            Await(window.OpenDocumentAsync("second.cs", true)); window.UpdateLayout();
            Editor(window).Focus(); window.KeyTextInput("// saved on close\n"); Dispatcher.UIThread.RunJobs();
            window.Layout.Close(group, "file:second.cs");
            Answer(window, "Save");
            Pump(() => window.Layout.Tabs(group).All(tab => tab.Path != "second.cs"));
            Require(File.ReadAllText(Path.Combine(root, "second.cs")).StartsWith("// saved on close", StringComparison.Ordinal), "Save in the close prompt did not write the file.");
            Console.WriteLine("PASS workbench editor preview retention, unsaved dot, save/don't save/cancel prompts, reopening, tab switching, Cmd/Ctrl+S and conflict recovery");
        }
        finally
        {
            foreach (var editor in window.GetLogicalDescendants().OfType<ScintillaEditor>()) editor.MarkSaved();
            window.Close();
        }
        ScrollBars(root);
    }

    // The dock separator's hit area overlaps the pane edge; the editor scrollbar must stay reachable there.
    private static void ScrollBars(string root)
    {
        File.WriteAllText(Path.Combine(root, "long.cs"), string.Join('\n', Enumerable.Range(0, 400).Select(line => $"// line {line}")));
        var window = new WorkbenchWindow(new LocalProjectAdapter(new ProjectServices(root)), root, new ProfileStore(Path.Combine(root, ".scroll-profile")));
        window.Show(); Pump(() => window.WorkspaceMounted);
        try
        {
            Await(window.OpenDocumentAsync("long.cs")); window.UpdateLayout();
            var bar = window.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Single(item => item.Name == "EditorVerticalScroll");
            Pump(() => { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); return bar.IsVisible && bar.Bounds.Height > 0; });
            // Probe the thin resting indicator at the bar's outer edge, where the separator's hit area competes.
            var bottom = bar.TranslatePoint(new Point(bar.Bounds.Width - 2, bar.Bounds.Height - 24), window)!.Value;
            window.MouseMove(bottom); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var hit = window.InputHitTest(bottom) as Visual;
            Require(hit is not null && (hit == bar || hit.GetVisualAncestors().Contains(bar)), $"The editor scrollbar must receive the pointer at the pane edge (hit {hit?.GetType().Name}).");
            var value = bar.Value;
            window.MouseDown(bottom, MouseButton.Left); window.MouseUp(bottom, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Require(bar.Value > value && Editor(window).FirstVisibleLine > 0, "Clicking the track below the thumb must page down.");
            Console.WriteLine("PASS workbench editor scrollbars stay reachable beside the pane separator and page in the pressed direction");
        }
        finally { window.Close(); }
    }
}
