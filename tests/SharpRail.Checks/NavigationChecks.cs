using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class NavigationChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Dispatcher.UIThread.RunJobs(); Require(done(), "Delayed navigation timed out.");
    }

    private static void Await(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    internal static void Run(string root)
    {
        File.WriteAllText(Path.Combine(root, "navigation.txt"), "Deferred navigation");
        var host = new DelayedHost(new ProjectServices(root));
        var window = new WorkbenchWindow(host, root, new ProfileStore(Path.Combine(root, ".navigation-profile")), E2E.E2eTerminals.Plain);
        window.Show(); Pump(() => window.WorkspaceMounted);
        var primary = window.Layout.State.Center.Leaves().Single();
        Await(window.OpenDocumentAsync("README.md", true));

        var gate = host.Hold("hello.txt");
        var pending = window.OpenDocumentAsync("hello.txt", true);
        window.Layout.Select(primary, "markdown:README.md");
        gate.SetResult(); Await(pending);
        Require(window.Layout.Selected(primary)?.Path == "README.md" && window.Layout.Tabs(primary).Count == 1,
            "Reselecting an active tab failed to defeat a deferred open.");

        Require(window.Layout.NewGroup(primary, "right"), "Navigation split failed.");
        var secondary = window.Layout.State.Center.Leaves().Last();
        window.Layout.Focus(primary); var firstGate = host.Hold("hello.txt");
        var first = window.OpenDocumentAsync("hello.txt", true);
        window.Layout.Focus(secondary); var secondGate = host.Hold("SPEC.md");
        var second = window.OpenDocumentAsync("SPEC.md", true);
        secondGate.SetResult(); Await(second); firstGate.SetResult(); Await(first);
        Require(window.Layout.Tabs(primary).Any(tab => tab.Path == "hello.txt") &&
            window.Layout.Selected(primary)?.Path == "README.md" && window.Layout.Selected(secondary)?.Path == "SPEC.md" &&
            window.Layout.View.FocusedCenter == secondary,
            "Independent pane navigation suppressed another pane's open.");

        window.Layout.Focus(primary); gate = host.Hold("hello.txt");
        pending = window.OpenDocumentAsync("hello.txt", true);
        window.Layout.Close(primary, "file:hello.txt"); gate.SetResult(); Await(pending);
        Require(window.Layout.Tabs(primary).All(tab => tab.Path != "hello.txt"), "Deferred open resurrected a closed tab.");

        window.Layout.Focus(secondary); gate = host.Hold("navigation.txt");
        pending = window.OpenDocumentAsync("navigation.txt", true);
        Require(window.Layout.RemoveGroup(secondary), "Navigation group removal failed.");
        gate.SetResult(); Await(pending);
        Require(window.Layout.Selected(primary)?.Path == "navigation.txt", "Removed-group completion did not reroute to surviving focus.");

        gate = host.Hold("hello.txt"); pending = window.OpenDocumentAsync("hello.txt", true);
        Await(window.OpenDocumentAsync("SPEC.md", true)); gate.SetResult(); Await(pending);
        Require(window.Layout.Selected(primary)?.Path == "SPEC.md", "Older navigation overrode a newer open.");
        for (var index = 0; index < 9; index++)
        {
            var path = $"overflow/{index}/same.md";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllText(Path.Combine(root, path), "# Overflow " + index);
            Await(window.OpenDocumentAsync(path, true));
        }
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var overflow = window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TabOverflow_" + primary);
        Require(overflow.IsVisible, "Clipped tab strip did not reveal overflow search.");
        var point = overflow.TranslatePoint(new Point(overflow.Bounds.Width / 2, overflow.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        var flyout = (Flyout)overflow.Flyout!;
        var content = (Control)flyout.Content!;
        var popup = TopLevel.GetTopLevel(content)!;
        Require(flyout.IsOpen && window.OwnedWindows.Count == 0 && content.Bounds.Width == 288,
            "Tab search must be an anchored dropdown rather than an owned dialog.");
        var search = content.GetLogicalDescendants().OfType<TextBox>().Single(item => item.Name == "TabOverflowSearch");
        var placeholder = search.GetVisualDescendants().OfType<TextBlock>().Single(item => item.IsVisible && item.Text == "Find an open tab…");
        Require(placeholder.Opacity == 1 && placeholder.Foreground is SolidColorBrush placeholderBrush && placeholderBrush.Color == Ui.Muted.Color,
            $"Dropdown placeholder must use the reference's muted text: {placeholder.Name}, opacity={placeholder.Opacity}, foreground={placeholder.Foreground}.");
        search.Text = "overflow/3/";
        Dispatcher.UIThread.RunJobs();
        var results = content.GetLogicalDescendants().OfType<ListBox>().Single(item => item.Name == "TabOverflowResults");
        popup.UpdateLayout();
        var selectedRow = results.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.IsSelected);
        var presenter = selectedRow.GetVisualDescendants().OfType<ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");
        Require(presenter.Background is SolidColorBrush background && background.Color == Ui.Hover.Color,
            "The dropdown selected row must use the reference's muted background.");
        Require(results.ItemCount == 1 && (results.SelectedItem as SharpRail.UI.Docking.DockTab)?.Path == "overflow/3/same.md",
            $"Overflow filter mismatch: {results.ItemCount} results, selected {results.SelectedItem}.");
        popup.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        popup.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Dispatcher.UIThread.RunJobs();
        Require(!flyout.IsOpen && window.Layout.Selected(primary)?.Path == "overflow/3/same.md",
            $"Keyboard overflow mismatch: open={flyout.IsOpen}, selected={window.Layout.Selected(primary)?.Path}.");
        point = overflow.TranslatePoint(new Point(16, 16), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        flyout = (Flyout)overflow.Flyout!; content = (Control)flyout.Content!;
        popup = TopLevel.GetTopLevel(content)!;
        search = content.GetLogicalDescendants().OfType<TextBox>().Single(item => item.Name == "TabOverflowSearch");
        search.Text = "no matching document"; Dispatcher.UIThread.RunJobs();
        Require(content.GetLogicalDescendants().OfType<TextBlock>().Single(item => item.Name == "EmptyResults").IsVisible,
            "An unmatched query must show the dropdown empty state.");
        popup.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs();
        Require(!flyout.IsOpen && overflow.IsFocused, "Escape must dismiss tab search and restore its trigger focus.");
        window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Tab_markdown_overflow_3_same.md").Focus();
        window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, null);
        Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Tabs(primary).All(tab => tab.Path != "overflow/3/same.md"), "Ctrl+W did not close the selected tab.");
        var selectedId = window.Layout.Selected(primary)!.Id;
        var position = window.Layout.Tabs(primary).ToList().FindIndex(tab => tab.Id == selectedId);
        window.KeyPress(Key.Left, RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.ArrowLeft, null);
        Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Tabs(primary).ToList().FindIndex(tab => tab.Id == selectedId) == position - 1,
            "Keyboard reorder did not move the selected tab left.");
        var selectedButton = window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.Name == "Tab_" + selectedId.Replace(':', '_').Replace('/', '_'));
        var close = selectedButton.GetLogicalAncestors().OfType<Grid>().First(grid => grid.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true)
            .GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab");
        point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Require(window.Layout.Tabs(primary).All(tab => tab.Id != selectedId), "Inline close did not remove the selected tab.");
        window.Close();
        Console.WriteLine("PASS delayed navigation: reselect, independent panes, close, removed group and newer open");
    }

    private sealed class DelayedHost(IProjectServices inner) : IProjectServices
    {
        private readonly Dictionary<string, TaskCompletionSource> gates = [];
        internal TaskCompletionSource Hold(string path)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates[path] = gate; return gate;
        }
        public async ValueTask<FileDocument> ReadFileAsync(string path, CancellationToken ct = default)
        {
            gates.Remove(path, out var gate);
            var document = await inner.ReadFileAsync(path, ct);
            if (gate is not null) await gate.Task.WaitAsync(ct);
            return document;
        }
        public ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken ct = default) => inner.SaveFileAsync(request, ct);
        public ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken ct = default) => inner.OpenProjectAsync(path, ct);
        public ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string path, CancellationToken ct = default) => inner.ListFilesAsync(path, ct);
        public ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken ct = default) => inner.ListSpecsAsync(ct);
        public ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparison, CancellationToken ct = default) => inner.ListCommitsAsync(comparison, ct);
        public ValueTask<GitSnapshot> GetGitAsync(string comparison = "", CancellationToken ct = default, string scope = "all") => inner.GetGitAsync(comparison, ct, scope);
        public ValueTask<string> GetDiffAsync(string path, string scope, string comparison = "", CancellationToken ct = default) => inner.GetDiffAsync(path, scope, comparison, ct);
        public ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparison = "", CancellationToken ct = default) => inner.GetDiffSidesAsync(path, scope, comparison, ct);
        public ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken ct = default) => inner.ApplyGitActionAsync(action, ct);
        public ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken ct = default) => inner.ListBranchesAsync(fetchDefault, ct);
        public ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken ct = default) => inner.ListEditorsAsync(ct);
        public ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken ct = default) => inner.OpenInEditorAsync(editorId, worktreePath, ct);
    }
}