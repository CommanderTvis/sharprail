using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.Plugins;

namespace SharpRail.UI;

// Where plugin contributions render in one window: side tools and their dormant placeholders, tab decorations,
// start actions, file viewers, terminal accessories and companions, and the window operations the plugin
// context binds to. Every plugin control is created inside a mount this window owns, so removing a plugin
// removes everything it created.
public sealed partial class WorkbenchWindow
{
    private readonly Dictionary<string, object?> viewerMounts = [];
    private string decorationSignature = "";

    private PluginRegistry Plugins => workbench.PluginRegistry;
    internal bool AtHome => atHome;
    internal string BranchName => git.Branch;

    private void WirePlugins()
    {
        surface.TabIcon = TabIcon;
        surface.TabAdornment = tab => Decoration(tab)?.Decoration.Adornment is { } adornment ? Mount(Decoration(tab)!.Value.PluginId, adornment) : null;
        Plugins.Changed += PluginsChanged;
        Closed += (_, _) => Plugins.Changed -= PluginsChanged;
        Layout.Changed += workbench.RaiseProjectionChanged;
        Layout.Changed += SyncEditors;
        Layout.Changed += () => decorationSignature = DecorationSignature();
        Layout.SelectionChanged += _ => { workbench.RaiseProjectionChanged(); EmitActivated(); };
        Layout.Focused += workbench.RaiseProjectionChanged;
        SyncPluginTools();
    }

    private void PluginsChanged(PluginTables tables)
    {
        if ((tables & (PluginTables.Roster | PluginTables.Active | PluginTables.Manifests | PluginTables.SideTools)) != 0) SyncPluginTools();
        if ((tables & (PluginTables.Roster | PluginTables.Active | PluginTables.TabDecorators | PluginTables.FileIconSlots)) != 0) RefreshDecorations();
        if ((tables & (PluginTables.WorkspaceActions | PluginTables.ProjectActions)) != 0) surface.RefreshEmptyContents();
        if (tables.HasFlag(PluginTables.FileIconSlots))
        {
            RefreshFileIcons();
            RefreshGitPanels();
        }
        if ((tables & (PluginTables.FileViewers | PluginTables.Roster)) != 0) RefreshViewers();
        if ((tables & (PluginTables.TerminalAccessories | PluginTables.Companions)) != 0)
            foreach (var (key, control) in documentContent.ToArray())
                if (control is Terminal.TerminalView terminal) AttachPluginTerminal(terminal, key);
    }

    // The composed catalog: core's tools stay owned by the layout; plugin tools follow the roster, withheld without Git when they need it.
    private void SyncPluginTools()
    {
        Layout.SetExtraTools([.. Plugins.ToolCatalog.Where(tool => !tool.RequiresGit || git.IsRepository)
            .Select(tool => new DockToolInfo(tool.Id, tool.Label, PluginIcons.Glyph(tool.Icon), tool.DefaultSide == PluginToolSide.Left ? "left" : "right"))]);
        InvalidatePluginTools();
    }

    // The registration a tool tab renders: null while its plugin is not active, which renders the placeholder.
    private SideToolRegistration? LiveTool(string toolId) =>
        PluginIdentity.ParseToolId(toolId) is { } parsed && Plugins.Active.Contains(parsed.PluginId) ? Plugins.SideTool(toolId) : null;

    private Control PluginTool(string toolId)
    {
        return RetainedPluginTool(toolId);
    }

    private Control DormantTool(string toolId, string pluginId)
    {
        var tool = Plugins.Tool(toolId);
        var label = tool?.Label ?? Plugins.Label(pluginId);
        var panel = new StackPanel { Name = "PluginToolDormant", Spacing = 12, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = PluginIcons.Resolve(tool?.Icon ?? Plugins.Entry(pluginId)?.Icon, Plugins.Entry(pluginId), Ui.Muted, 24);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(icon);
        var text = Ui.Text(label + " is off");
        text.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(text);
        var open = Ui.Button("Open Settings › Plugins", () => ShowSettings("Plugins"));
        open.Name = "PluginToolOpenSettings";
        open.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(open);
        return panel;
    }

    /// <summary>The host control one plugin contribution renders in; a throwing factory shows its failure instead of breaking the window.</summary>
    internal static Control Mount(string pluginId, Func<Control> create)
    {
        var host = new ContentControl { Name = "PluginMount_" + pluginId, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        try { host.Content = create(); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Plugin {pluginId} failed to render: {error}");
            host.Content = Ui.Text($"This part of {pluginId} failed to render: {error.Message}", Ui.Danger, 12);
        }
        return host;
    }

    private TabRef TabRef(DockTab tab) => tab.Kind switch
    {
        "terminal" => new TerminalTabRef(workspaceRoot, tab.Id),
        "tool" => new ToolTabRef(workspaceRoot, tab.Id),
        "diff" => new FileTabRef(workspaceRoot, EditorKind.Diff, tab.Path),
        _ => new FileTabRef(workspaceRoot, EditorKind.File, tab.Path)
    };

    private (string PluginId, TabDecoration Decoration)? Decoration(DockTab tab) => Plugins.TabDecorators.Count == 0 ? null : Plugins.Decorate(TabRef(tab));

    private Control? TabIcon(DockTab tab, IBrush brush)
    {
        if (Decoration(tab) is { Decoration.Icon: { } decorated } decoration)
            return PluginIcons.Resolve(decorated, Plugins.Entry(decoration.PluginId), brush, 14);
        if (tab.IsTool && Plugins.Tool(tab.Id) is { } tool)
            return PluginIcons.Resolve(tool.Icon, Plugins.Entry(tool.PluginId), brush, 14);
        if (tab.Path.Length > 0 && !tab.IsTool && Plugins.FileIcon(tab.Path, FileIconKind.File) is { } icon)
            return PluginIcons.Resolve(icon.Icon, Plugins.Entry(icon.PluginId), brush, 14);
        return null;
    }

    /// <summary>The icon a plugin's file-icon slot answers for a path, else core's glyph.</summary>
    private Control FileIcon(string path, bool directory, string glyph, IBrush? color = null)
    {
        if (Plugins.FileIcon(path, directory ? FileIconKind.Directory : FileIconKind.File) is { } icon)
            return PluginIcons.Resolve(icon.Icon, Plugins.Entry(icon.PluginId), color, 14);
        return Ui.Icon(glyph, color, 14);
    }

    // Rebuilding tabs closes menus and drops focus, so only a change in what decorations and icon slots answer rebuilds
    // them; a layout change re-renders tabs itself and only records the answers.
    private void RefreshDecorations()
    {
        var signature = DecorationSignature();
        if (signature == decorationSignature) return;
        decorationSignature = signature;
        surface.Rebuild();
    }

    private string DecorationSignature() => string.Join("\n", Layout.State.Groups.SelectMany(group => Layout.Tabs(group.Id)).Select(tab =>
    {
        var decoration = Decoration(tab);
        var icon = tab.IsTool ? Plugins.Tool(tab.Id)?.Icon : tab.Path.Length > 0 ? Plugins.FileIcon(tab.Path, FileIconKind.File)?.Icon : null;
        return decoration is null && icon is null ? "" : $"{tab.Id}\t{decoration?.PluginId}\t{decoration?.Decoration.Icon}\t{decoration?.Decoration.Adornment is not null}\t{icon}";
    }).Where(line => line.Length > 0));

    private Control StartActions(Control open)
    {
        if (Plugins.WorkspaceActions.Count == 0) return open;
        var row = new StackPanel { Name = "WorkspaceStartActions", Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(open);
        var workspace = workspaceRoot;
        var group = Layout.View.FocusedCenter.Length > 0 ? Layout.View.FocusedCenter : Layout.State.Center.Leaves().First();
        foreach (var action in Plugins.WorkspaceActions)
            row.Children.Add(Mount(action.PluginId, () => action.Value.Create(workspace, group)));
        return row;
    }

    private void AddProjectActions(Panel buttons)
    {
        var project = projectRoot;
        foreach (var action in Plugins.ProjectActions)
            buttons.Children.Add(Mount(action.PluginId, () => action.Value.Create(project)));
    }

    // A viewer tab is one a plugin's declared viewer claimed; it shows that viewer's control while the plugin is live.
    private Control ViewerContent(DockTab tab, string key, FileDocument? document)
    {
        var viewer = Plugins.FileViewer(tab.Path);
        viewerMounts[key] = viewer?.Registration;
        if (viewer is null)
        {
            var panel = new StackPanel { Name = "FileViewerGone", Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(Ui.Text("The viewer for this file is off."));
            var open = Ui.Button("Open as text", () => _ = OpenDocumentAsync(tab.Path, true, raw: true), "fileText");
            open.Name = "FileViewerOpenAsText";
            open.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(open);
            return panel;
        }
        var props = new FileViewerProps(workspaceRoot, tab.Path, workbench.FileRevision(workspaceRoot, tab.Path), viewer.Read == PluginFileRead.Text ? document?.Text : null);
        return Mount(viewer.PluginId, () => viewer.Registration.Create(props));
    }

    private void RefreshViewers()
    {
        var stale = viewerMounts.Where(mount => documentContent.ContainsKey(mount.Key) &&
            !Equals(mount.Value, Plugins.FileViewer(mount.Key[(mount.Key.IndexOf(":viewer:", StringComparison.Ordinal) + 8)..])?.Registration)).Select(mount => mount.Key).ToArray();
        foreach (var key in stale) { viewerMounts.Remove(key); DropDocumentContent(key); }
        if (stale.Length > 0) surface.RefreshContents();
    }

    /// <summary>The tab kind an open uses: a live plugin viewer's claim, unless the caller wants the raw text.</summary>
    private string DocumentKind(string path, bool raw) =>
        !raw && Plugins.FileViewer(path) is not null ? "viewer" : Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" ? "markdown" : "file";

    // A viewer with read strategy None never has its text read.
    private async Task<FileDocument> ReadForKindAsync(string path, string kind, CancellationToken token) =>
        kind == "viewer" && Plugins.FileViewer(path)?.Read == PluginFileRead.None ? new FileDocument(path, "") : await host.ReadFileAsync(path, token);

    private void AttachPluginTerminal(Terminal.TerminalView terminal, string key)
    {
        var tabKey = terminal.Launch.TabKey;
        var workspace = terminal.Launch.WorkspaceRoot;
        terminal.Accessories.Children.Clear();
        var hostRef = new CompanionHost(workspace, tabKey);
        var available = Plugins.Companions.Where(row => Safely(() => row.Value.IsAvailable(hostRef))).ToArray();
        if (available.Length > 0)
        {
            var bar = new StackPanel { Name = "TerminalCompanions", Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 2) };
            foreach (var companion in available)
            {
                var title = (companion.Value.InstanceTitle is { } instance ? Safely(() => instance(hostRef)) : null) ?? companion.Value.Title;
                var button = Ui.Button(title, () => ToggleCompanion(terminal, companion.PluginId, companion.Value.Kind));
                button.Name = "TerminalCompanion_" + companion.PluginId + "_" + companion.Value.Kind;
                bar.Children.Add(button);
            }
            terminal.Accessories.Children.Add(bar);
        }
        foreach (var accessory in Plugins.TerminalAccessories)
            terminal.Accessories.Children.Add(Mount(accessory.PluginId, () => accessory.Value.Create(new TerminalAccessory(terminal, workspace, tabKey))));
        var open = slot.Companions.GetValueOrDefault(workspace + "\n" + tabKey);
        var shown = open is null ? null : available.FirstOrDefault(row => row.PluginId + ":" + row.Value.Kind == open);
        terminal.Companion = shown is null ? null : Mount(shown.PluginId, () => shown.Value.Create(hostRef));
    }

    private void ToggleCompanion(Terminal.TerminalView terminal, string pluginId, string kind)
    {
        var key = terminal.Launch.WorkspaceRoot + "\n" + terminal.Launch.TabKey;
        if (slot.Companions.GetValueOrDefault(key) == pluginId + ":" + kind) slot.Companions.Remove(key);
        else slot.Companions[key] = pluginId + ":" + kind;
        SaveProfile();
        AttachPluginTerminal(terminal, key);
    }

    internal void FocusCompanion(CompanionHost companionHost, string pluginId, string kind)
    {
        var key = companionHost.WorkspaceId + "\n" + companionHost.TabKey;
        slot.Companions[key] = pluginId + ":" + kind;
        SaveProfile();
        if (documentContent.GetValueOrDefault(companionHost.WorkspaceId + ":" + companionHost.TabKey) is Terminal.TerminalView terminal)
        {
            AttachPluginTerminal(terminal, key);
            terminal.Companion?.Focus();
        }
    }

    private static bool Safely(Func<bool> predicate)
    {
        try { return predicate(); }
        catch (Exception error) { Console.Error.WriteLine("Plugin predicate failed: " + error.Message); return false; }
    }

    private static string? Safely(Func<string?> predicate)
    {
        try { return predicate(); }
        catch (Exception error) { Console.Error.WriteLine("Plugin predicate failed: " + error.Message); return null; }
    }

    private sealed class TerminalAccessory(Terminal.TerminalView terminal, string workspace, string tabKey) : ITerminalAccessoryApi
    {
        public string WorkspaceId => workspace;
        public string TabKey => tabKey;
        public void Write(string data) => terminal.Write(data);

        public IReadOnlyList<string> BufferTail(int lines, bool omitFaint = false)
        {
            var all = terminal.ReadScreen().Replace("\r", "").TrimEnd('\n').Split('\n');
            return all.Length <= lines ? all : all[^lines..];
        }

        // Shift+Return is the bridge's own; the agent newline encoding is not ported (see Terminal/SPEC.md).
        public void SetKeyEncoding(TerminalKeyEncoding encoding) { }
    }

    internal IEnumerable<(string Workspace, IReadOnlyList<DockTab> Tabs, IReadOnlyList<string> Shown)> TerminalTabs()
    {
        foreach (var (workspace, view) in Layout.State.Workspaces)
        {
            var tabs = view.Documents.Values.SelectMany(items => items).Where(tab => tab.Kind == "terminal").ToArray();
            if (tabs.Length == 0) continue;
            string? SelectedIn(string group) => view.Selected.GetValueOrDefault(group) is { } id && tabs.Any(tab => tab.Id == id) ? id : null;
            var bottom = Layout.State.Groups.Where(group => group.Region == "bottom").Select(group => SelectedIn(group.Id));
            var shown = new[] { SelectedIn(view.FocusedCenter) }.Concat(bottom)
                .Concat(view.Documents.Keys.Select(SelectedIn)).OfType<string>().Distinct().ToArray();
            yield return (workspace, tabs, shown);
        }
    }

    internal EditorRef? ActiveEditor()
    {
        if (!WorkspaceMounted || atHome) return null;
        return Layout.Selected(Layout.View.FocusedCenter) is { } tab ? EditorRef(workspaceRoot, tab) : null;
    }

    private EditorRef? EditorRef(string workspace, DockTab tab) => tab.Kind switch
    {
        "file" or "markdown" or "viewer" => new(workspace + ":" + tab.Id, workspace, tab.Path, EditorKind.File, PendingDocument(workspace, tab.Id) is not null),
        "diff" => new(workspace + ":" + tab.Id, workspace, tab.Path, EditorKind.Diff, false),
        _ => null
    };

    internal IEnumerable<EditorRef> EditorRefs() =>
        Layout.State.Workspaces.SelectMany(pair => pair.Value.Documents.Values.SelectMany(tabs => tabs).Select(tab => EditorRef(pair.Key, tab))).OfType<EditorRef>();

    private string? lastActivated;

    private void EmitActivated()
    {
        if (ActiveEditor() is not { } editor || editor.Id == lastActivated) return;
        lastActivated = editor.Id;
        workbench.PluginLoader.Editors.Emit(new EditorLifecycleEvent(editor, EditorLifecycle.Activated));
    }

    private Dictionary<string, EditorRef> openEditors = [];

    // Opened and Closed follow the editor tabs the layout holds, whichever window gesture added or removed them.
    private void SyncEditors()
    {
        var next = EditorRefs().DistinctBy(editor => editor.Id).ToDictionary(editor => editor.Id);
        var previous = openEditors;
        openEditors = next;
        foreach (var editor in next.Values.Where(editor => !previous.ContainsKey(editor.Id)))
            workbench.PluginLoader.Editors.Emit(new EditorLifecycleEvent(editor, EditorLifecycle.Opened));
        foreach (var editor in previous.Values.Where(editor => !next.ContainsKey(editor.Id)))
            workbench.PluginLoader.Editors.Emit(new EditorLifecycleEvent(editor, EditorLifecycle.Closed));
        EmitActivated();
    }

    private void EmitSaved(string key)
    {
        if (EditorRefs().FirstOrDefault(editor => editor.Id == key) is { } editor)
            workbench.PluginLoader.Editors.Emit(new EditorLifecycleEvent(editor, EditorLifecycle.Saved));
    }

    internal async Task<EditorRef?> OpenEditorAsync(string workspace, string path, EditorOpenOptions options)
    {
        if (!WorkspaceMounted || workspace != workspaceRoot && !atHome) return null;
        var line = options.Line ?? 0;
        if (options.KeyPath is { Count: > 0 } keys)
            try { line = KeyLine((await host.ReadFileAsync(path, lifetime.Token)).Text, keys); }
            catch (IOException) { line = 0; }
        await OpenDocumentAsync(path, !options.Preview, line: line, raw: options.Raw);
        return EditorRefs().FirstOrDefault(editor => editor.WorkspaceId == workspaceRoot && editor.Path == path);
    }

    // The one-based line declaring the last key of a JSON key path, found key by key in order; 0 when unresolved.
    internal static int KeyLine(string text, IReadOnlyList<string> keys)
    {
        var position = 0;
        foreach (var key in keys)
        {
            var found = text.IndexOf("\"" + key.Replace("\"", "\\\"") + "\"", position, StringComparison.Ordinal);
            if (found < 0) return 0;
            position = found + key.Length + 2;
        }
        return text.AsSpan(0, position).Count('\n') + 1;
    }

    internal void CloseEditor(string id)
    {
        foreach (var group in Layout.State.Groups)
            if (Layout.State.Workspaces.GetValueOrDefault(workspaceRoot)?.Documents.GetValueOrDefault(group.Id)?.FirstOrDefault(tab => workspaceRoot + ":" + tab.Id == id) is { } tab)
            { Layout.Close(group.Id, tab.Id); return; }
    }

    internal async Task SaveEditorAsync(string id)
    {
        if (documentContent.GetValueOrDefault(id) is Editor.CodeDocumentView view) await view.SaveAsync();
    }

    internal async Task<string> OpenPluginTerminalAsync(string workspace, TerminalOpenOptions options)
    {
        if (!WorkspaceMounted || workspace != workspaceRoot) await OpenWorkspaceAsync(workspace, false);
        var tabKey = options.TabKey ?? "terminal:" + Guid.NewGuid().ToString("N");
        var existing = Layout.State.Groups.FirstOrDefault(group => Layout.Tabs(group.Id).Any(tab => tab.Id == tabKey));
        if (existing is not null) { Layout.Select(existing.Id, tabKey); return tabKey; }
        var leaves = Layout.State.Center.Leaves().ToArray();
        var target = options.GroupId is { } requested && Layout.State.Groups.Any(group => group.Id == requested) ? requested
            : leaves.Contains(Layout.View.FocusedCenter) ? Layout.View.FocusedCenter : leaves[0];
        Layout.NewTerminal(target, tabKey);
        if (options.Command is { } command)
            Dispatcher.UIThread.Post(async () =>
            {
                if (documentContent.GetValueOrDefault(workspaceRoot + ":" + tabKey) is not Terminal.TerminalView terminal || terminal.Backend is not { } backend) return;
                try { await backend.Started; } catch (Exception) { return; }
                terminal.Write(command + "\r");
            }, DispatcherPriority.Background);
        return tabKey;
    }

    internal async Task<HostWorkspace?> EnterDefaultWorkspaceAsync(string project)
    {
        await OpenWorkspaceAsync(project, true);
        return WorkspaceMounted && projectRoot == project
            ? new HostWorkspace(workspaceRoot, projectRoot, state.Label(workspaceRoot), git.Branch, workspaceRoot, workspaceRoot == projectRoot) : null;
    }

    internal async Task<string?> PickHostPathAsync(FilePickOptions options)
    {
        if (remote) return await Panels.Dialogs.HostPath(this, options.WorkspaceId ?? workspaceRoot, null, true);
        if (options.Directory)
            return (await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        return (await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    }

    internal void RevealToolFor(string workspace, string tool)
    {
        if (workspace != workspaceRoot) return;
        var group = Layout.State.Groups.FirstOrDefault(item => item.Tools.Any(tab => tab.Id == tool));
        if (group is null) Layout.RestoreTool(tool);
        else { Layout.Visible(group.Region, true); if (group.Folded) Layout.Fold(group.Id); Layout.Select(group.Id, tool); }
    }

    internal void SetDiffScope(string workspace, GitDiffScope scope)
    {
        if (workspace != workspaceRoot) return;
        switch (scope)
        {
            case UncommittedDiffScope: changeScope = "Uncommitted"; selectedCommit = null; break;
            case CommitDiffScope commit:
                selectedCommit = scopeCommits?.FirstOrDefault(item => item.Sha == commit.Sha || item.ShortSha == commit.Sha) ??
                    new GitCommit(commit.Sha, commit.Sha[..Math.Min(7, commit.Sha.Length)], "", "", "");
                changeScope = "Commit";
                break;
            case PinnedDiffScope pinned: changeScope = "All changes"; selectedCommit = null; comparison = pinned.BaseRef; RetargetDiffTabs(); break;
            default: changeScope = "All changes"; selectedCommit = null; break;
        }
        SaveGitSelection();
        _ = RefreshAsync();
    }

    internal void Notify(bool failure, string message)
    {
        if (failure) Console.Error.WriteLine(message);
        ShowNotification(message);
    }

    // The Markdown preview reports its selections into the same editor-event stream as the code editor.
    private void ReportSelections(MarkdownPreview preview, DockTab tab)
    {
        var workspace = workspaceRoot;
        preview.SelectionChanged += text =>
        {
            if (EditorRef(workspace, tab) is not { } editor) return;
            var lines = text.Split('\n');
            workbench.PluginLoader.Editors.Emit(new EditorSelectionEvent(editor,
                text.Length == 0 ? null : new EditorSelection(1, 1, lines.Length, lines[^1].Length + 1, text)));
        };
    }

    // A rendered document's link goes through the plugins' document-link slot first, then core's own resolution.
    private void FollowLink(string path, string? anchor) =>
        _ = OpenDocumentAsync(Plugins.DocumentLink(workspaceRoot, path) ?? path, false, anchor);
}
