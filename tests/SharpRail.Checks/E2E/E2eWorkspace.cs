using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks.E2E;

internal sealed class E2eWorkspace : IDisposable
{
    internal WorkbenchWindow Window { get; }
    internal E2eHost Host { get; }
    internal E2eTerminals Terminals { get; }
    internal string Center => Window.Layout.View.FocusedCenter;
    internal IReadOnlyList<DockTab> Tabs => Window.Layout.Tabs(Center);
    internal string Root { get; }

    internal E2eWorkspace(string root, bool openFiles = true, string? profileRoot = null, E2eTerminals? terminals = null, string? startPath = null, Action<E2eHost>? prepare = null)
    {
        Root = root;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "README.md"), "# sample-project\n");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "plain-text-fixture\n");
        File.WriteAllText(Path.Combine(root, "DIAGRAM.md"), "# Diagram demo\n\n```mermaid\nflowchart TD; Start --> Finish\n```\n\n```mermaid\nflowchart TD; Start --> --> broken\n```\n\n```bash\necho plain-fence-stays-code\n```\n");
        File.WriteAllText(Path.Combine(root, "LARGE.md"), "# Large document\n\n" + string.Join("\n\n", Enumerable.Repeat("A fixture paragraph.", 100)));
        File.WriteAllText(Path.Combine(root, "ALERTS.md"), "# Alert callouts\n\n> [!NOTE]\n> Useful information users should know.\n\n> [!TIP]\n> Helpful advice for doing things better.\n\n> [!IMPORTANT]\n> Key information to achieve a goal.\n\n> [!WARNING]\n> Urgent info needing immediate attention.\n\n> [!CAUTION]\n> Advises about risky outcomes.\n\n> A plain blockquote, no marker, so it stays a quote.\n");
        File.WriteAllText(Path.Combine(root, "LINKS.md"), "# Link demo\n\nJump to [Section two](#section-two), open [the spec](SPEC.md), and see the logo:\n\n![logo](logo.png)\n\n## Section two\n\nTarget of the in-document anchor.\n");
        File.WriteAllText(Path.Combine(root, "SPEC.md"), "---\nid: sample-root\ntype: goal-and-requirements\ntitle: Sample Project\n---\n\n## Goal\n\nA throwaway fixture project.\n");
        Directory.CreateDirectory(Path.Combine(root, "styles"));
        File.WriteAllText(Path.Combine(root, "styles", "COLOR.md"), "# Colour system\n\nSee [`themes/SPEC.md`](../themes/SPEC.md).\n");
        Directory.CreateDirectory(Path.Combine(root, "themes"));
        File.WriteAllText(Path.Combine(root, "themes", "SPEC.md"), "# Theme spec target\n\nReached through a parent-relative Markdown link.\n");
        File.WriteAllBytes(Path.Combine(root, "logo.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
        Host = new(new ProjectServices(root));
        prepare?.Invoke(Host);
        Terminals = terminals ?? new();
        Window = new(Host, startPath ?? root, new ProfileStore(profileRoot ?? root + "-profile"), Terminals.Factory) { Width = 1352, Height = 848 };
        Window.Show();
        if (startPath is not null) return;
        Until(() => Window.WorkspaceMounted);
        if (openFiles) Click(Find<Button>("Tab_files"));
    }

    internal static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) Pump();
        Dispatcher.UIThread.RunJobs(); Require(condition(), "E2E condition timed out.");
    }

    internal static void Settle(int milliseconds = 300)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) Pump();
    }

    private static void Pump()
    {
        using var stop = new CancellationTokenSource(10);
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    internal T Find<T>(string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
        return Window.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
    }

    internal Button Tab(string path) => Find<Button>("Tab_" + (path.EndsWith(".txt", StringComparison.Ordinal) ? "file_" : "markdown_") + path.Replace('/', '_'));

    internal Control FileRow(string path)
    {
        var tree = Find<TreeView>("FilesTree");
        Until(() => tree.Items.Count > 0);
        var node = tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(item => item.Tag is ProjectFile file && file.Path == path);
        return (Control)node.Header!;
    }

    internal void ExpandFolder(string path)
    {
        var tree = Find<TreeView>("FilesTree");
        var node = tree.GetLogicalDescendants().OfType<TreeViewItem>().Single(item => item.Tag is ProjectFile file && file.Path == path);
        Click(node.GetVisualDescendants().OfType<ToggleButton>().First());
        Until(() => node.IsExpanded && node.Items.OfType<TreeViewItem>().All(item => item.Tag is ProjectFile));
    }

    internal void Click(Control control, bool twice = false, bool freshGesture = true, MouseButton mouseButton = MouseButton.Left)
    {
        var name = control.Name;
        var tag = control.Tag;
        var owner = control.GetLogicalAncestors().OfType<Control>().FirstOrDefault(item => item.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true)?.Name;
        var filePath = control.GetLogicalAncestors().OfType<TreeViewItem>().Select(item => item.Tag).OfType<ProjectFile>().FirstOrDefault()?.Path;
        if (freshGesture)
        {
            Window.MouseMove(new Point(Window.Bounds.Width - 2, Window.Bounds.Height - 2));
            Settle(550);
        }
        if (TopLevel.GetTopLevel(control) is null)
        {
            if (name is not null && owner is not null) control = Find<Control>(owner).GetLogicalDescendants().OfType<Control>().Single(item => item.Name == name);
            else if (name is not null && tag is not null)
            {
                Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
                control = Window.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == name && Equals(item.Tag, tag));
            }
            else if (name is not null) control = Find<Control>(name);
            else if (filePath is not null) control = FileRow(filePath);
        }
        Window.UpdateLayout();
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel(control)!.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var input = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(Math.Min(24, control.Bounds.Width / 2), control.Bounds.Height / 2), input)!.Value;
        Require(control.Bounds.Width > 0 && control.Bounds.Height > 0, "E2E input target is not arranged.");
        input.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var hit = input.InputHitTest(point) as Control;
        Require(hit is not null && (ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control)),
            $"E2E target {control.Name ?? control.GetType().Name} ({control.Bounds}) is occluded at {point} by {hit?.GetType().Name}/{hit?.Name}; ancestors: {string.Join(",", hit?.GetVisualAncestors().OfType<Control>().Select(item => item.GetType().Name + "/" + item.Name) ?? [])}.");
        input.MouseDown(point, mouseButton); input.MouseUp(point, mouseButton);
        if (twice) { input.MouseDown(point, mouseButton); input.MouseUp(point, mouseButton); }
        Dispatcher.UIThread.RunJobs();
    }

    internal void ContextAction(Control control, string title)
    {
        Click(control, mouseButton: MouseButton.Right);
        var menu = control.ContextMenu!;
        Until(() => menu.IsOpen);
        var item = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, title));
        Require(item.IsEnabled, "E2E menu action is disabled: " + title);
        TopLevel.GetTopLevel(item)!.UpdateLayout();
        Click(item, freshGesture: false);
    }

    internal void Open(string path, bool keep = false)
    {
        Click(FileRow(path), keep);
        Until(() => Tabs.Any(tab => tab.Path == path && (!keep || !tab.Preview)) && Window.Layout.Selected(Center)?.Path == path);
    }

    public void Dispose() => Window.Close();
}

internal sealed class E2eHost(IProjectServices inner) : IProjectServices
{
    private readonly Dictionary<string, TaskCompletionSource> gates = [];
    private TaskCompletionSource? openGate;
    internal TaskCompletionSource HoldOpen() => openGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Dictionary<string, int> Reads { get; } = [];
    internal TaskCompletionSource Hold(string path)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gates.Add(path, gate); return gate;
    }
    public async ValueTask<FileDocument> ReadFileAsync(string path, CancellationToken ct = default)
    {
        Reads[path] = Reads.GetValueOrDefault(path) + 1;
        gates.Remove(path, out var gate);
        var document = await inner.ReadFileAsync(path, ct);
        if (gate is not null) await gate.Task.WaitAsync(ct);
        return document;
    }
    public ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken ct = default) => inner.SaveFileAsync(request, ct);
    public async ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken ct = default)
    {
        var gate = openGate; openGate = null;
        var workspace = await inner.OpenProjectAsync(path, ct);
        if (gate is not null) await gate.Task.WaitAsync(ct);
        return workspace;
    }
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
