using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;
using SharpRail.UI.Terminal;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    internal sealed record WorkspaceFileDrag(Workbench Owner, string Root, string Path);
    internal static readonly DataFormat<WorkspaceFileDrag> WorkspaceFileFormat = DataFormat.CreateInProcessFormat<WorkspaceFileDrag>("SharpRail.WorkspaceFile");

    internal static DataTransfer CreateWorkspaceFileData(WorkspaceFileDrag source, IStorageItem? file)
    {
        var item = DataTransferItem.Create(WorkspaceFileFormat, source);
        // macOS requires one pasteboard writer per drag image. Representations of
        // the same file belong on one item, including a native format for remote drags.
        if (file is not null) item.SetFile(file);
        else item.SetText(source.Path);
        var data = new DataTransfer();
        data.Add(item);
        return data;
    }

    private static DrawnTreeEntry? DragEntry(object? source) => source is Control control
        ? control.GetVisualAncestors().Prepend(control).OfType<DrawnTreeLabel>().FirstOrDefault()?.Entry : null;

    private void WireFileDragging(DrawnTree tree, Grid panel)
    {
        var progress = panel.FindControl<FileTransferProgress>("TransferProgress")!;
        tree.Drag = async (entry, pressed) =>
        {
            if (entry.Tag is not ProjectFile file || !WorkspaceMounted || switchingWorkspace || progress.Busy) return;
            var root = workspaceRoot;
            try
            {
                IStorageItem? item = null;
                if (!remote)
                {
                    var uri = new Uri(Path.Combine(root, file.Path));
                    item = await Task.Run<IStorageItem?>(async () => file.IsDirectory ? await StorageProvider.TryGetFolderFromPathAsync(uri) : await StorageProvider.TryGetFileFromPathAsync(uri), lifetime.Token);
                }
                if (root != workspaceRoot || switchingWorkspace) return;
                using var data = CreateWorkspaceFileData(new(workbench, root, file.Path), item);
                await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Report(error); }
        };
        DragDrop.SetAllowDrop(tree, true);
        DrawnTreeEntry? highlighted = null;
        void Clear() { tree.Background = Ui.Sidebar; if (highlighted is not null) { highlighted.DropTarget = false; tree.RefreshRows(); highlighted = null; } }
        tree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Clear());
        tree.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            Clear();
            var entry = DragEntry(e.Source);
            var folder = entry?.Tag is ProjectFile file ? file.IsDirectory ? file.Path : HostPaths.Parent(file.Path) : "";
            var source = e.DataTransfer.TryGetValue(WorkspaceFileFormat);
            var valid = WorkspaceMounted && !switchingWorkspace && !progress.Busy && (source is null ? e.DataTransfer.Contains(DataFormat.File)
                : ReferenceEquals(source.Owner, workbench) && source.Root == workspaceRoot && CanDropPath(source.Path, folder));
            var copy = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control);
            e.DragEffects = valid ? source is null || copy ? DragDropEffects.Copy : DragDropEffects.Move : DragDropEffects.None;
            if (valid && entry is not null) { highlighted = entry; entry.DropTarget = true; tree.RefreshRows(); }
            if (valid && entry is null) tree.Background = Ui.PrimarySubtle;
            e.Handled = true;
        });
        tree.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            Clear();
            var entry = DragEntry(e.Source);
            var folder = entry?.Tag is ProjectFile file ? file.IsDirectory ? file.Path : HostPaths.Parent(file.Path) : "";
            var source = e.DataTransfer.TryGetValue(WorkspaceFileFormat);
            var files = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
            var copy = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control);
            e.Handled = true;
            var workspace = workspaceRoot; var request = projectRequest;
            if (!WorkspaceMounted || switchingWorkspace || progress.Busy) return;
            try
            {
                await progress.RunAsync(source is null ? "Copying files…" : copy ? "Copying…" : "Moving…", lifetime.Token, async token =>
                {
                    if (source is not null)
                    {
                        if (!ReferenceEquals(source.Owner, workbench) || source.Root != workspace || !CanDropPath(source.Path, folder)) return;
                        await Task.Run(async () => await host.ApplyFileActionAsync(new(copy ? "copy" : "rename", source.Path, Path.Combine(folder, Path.GetFileName(source.Path))) { WorkspaceRoot = workspace }, token), token);
                    }
                    else if (files.Length > 0)
                    {
                        var entries = await ReadDroppedFilesAsync(files, token);
                        if (workspace != workspaceRoot || request != projectRequest) return;
                        await Task.Run(async () => await host.ImportFilesAsync(new(workspace, folder, entries), token), token);
                    }
                });
                if (workspace != workspaceRoot || request != projectRequest) return;
                if (entry is { CanExpand: true }) tree.Expand(entry, true);
                await RefreshFilesAsync(request);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Report(error); }
        });
    }

    internal static bool CanDropPath(string path, string folder) => CanTransferPath(path, Path.Combine(folder, Path.GetFileName(path)));

    internal static bool CanTransferPath(string path, string target)
    {
        path = path.Replace('\\', '/'); target = target.Replace('\\', '/');
        var comparison = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !target.Equals(path, comparison) && !target.StartsWith(path + "/", comparison);
    }

    /// <summary>Whether agent terminals show an Attach button (Android and the phone layout, where nothing can be dropped).</summary>
    internal bool AttachButtons { get; set; }

    /// <summary>Chooses files for the Attach button; replaceable so checks can supply a picker result.</summary>
    internal Func<Task<IReadOnlyList<IStorageItem>>>? PickAttachments { get; set; }

    /// <summary>Asks for a workspace-relative file for the Attach menu's workspace choice; replaceable for checks.</summary>
    internal Func<Task<string?>>? PickWorkspaceFile { get; set; }

    internal void WireTerminalFileDrop(TerminalView terminal)
    {
        var progress = new FileTransferProgress();
        terminal.Accessories.Children.Add(progress);
        DragDrop.SetAllowDrop(terminal, true);
        Control? Receiver() => terminal.Accessories.Children.FirstOrDefault(control =>
            control.GetValue(FileDrop.AcceptsProperty)?.Invoke() == true && control.GetValue(FileDrop.AttachProperty) is not null);
        bool Ready() => !terminal.IsExited && !terminal.IsFailed && !terminal.IsDetached && !terminal.IsWatching && terminal.Backend?.Started.IsCompletedSuccessfully == true;
        // Inserts host paths into the receiver's prompt if the terminal, its workspace and its agent are still the ones that asked.
        void Insert(Control receiver, string workspace, IReadOnlyList<string> paths)
        {
            if (workspace != workspaceRoot || switchingWorkspace || !Ready() || !ReferenceEquals(receiver, Receiver())) return;
            receiver.GetValue(FileDrop.AttachProperty)!(paths);
            terminal.FocusTerminal();
        }
        // Uploads external bytes to the host, then inserts the host paths. Returns false when nothing was attached.
        async Task<bool> Upload(Control receiver, string workspace, Func<CancellationToken, Task<IReadOnlyList<FileTransferEntry>>> read)
        {
            IReadOnlyList<string>? uploaded = null;
            await progress.RunAsync("Attaching files…", lifetime.Token, async token =>
            {
                var entries = await read(token);
                if (entries.Count == 0 || workspace != workspaceRoot || switchingWorkspace || !Ready()) return;
                uploaded = await Task.Run(async () => await host.ImportFilesAsync(new(workspace, null, entries), token), token);
            });
            if (uploaded is null) return false;
            Insert(receiver, workspace, uploaded);
            return true;
        }
        terminal.GotFocus += (_, _) => { if (Receiver() is not null) lastAgentTerminal = new(terminal); };
        terminal.Output += DeliverSharedAttachments;
        terminal.AcceptsAttachments = () => !progress.Busy && Ready() && Receiver() is not null;
        terminal.AttachEntries = async entries =>
        {
            if (Receiver() is not { } receiver || progress.Busy || !Ready()) return false;
            try { return await Upload(receiver, terminal.Launch.WorkspaceRoot, _ => Task.FromResult(entries)); }
            catch (OperationCanceledException) { return false; }
        };
        if (AttachButtons)
        {
            Button attach = null!;
            ContextMenu menu = null!;
            MenuItem hostPath = null!;
            attach = Ui.Button("Attach", () => OpenAttachMenu());
            attach.Name = "TerminalAttach";
            attach.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            attach.Margin = new Thickness(8, 2);
            // Only a running Claude Code or Codex session that this client holds takes attachments.
            void Refresh() => attach.IsVisible = Ready() && Receiver() is not null;
            var upload = Ui.Menu(OperatingSystem.IsAndroid() ? "Upload file from phone" : "Upload file from this device", () => _ = AttachPickedAsync());
            upload.Name = "TerminalAttachUpload";
            hostPath = Ui.Menu("Choose path from host", () => _ = AttachPathAsync(ChooseHostPathAsync));
            hostPath.Name = "TerminalAttachHostPath";
            var workspaceFile = Ui.Menu("Choose file from workspace", () => _ = AttachPathAsync(async workspace =>
                await (PickWorkspaceFile ?? ChooseWorkspaceFileAsync)() is { Length: > 0 } relative ? Path.Combine(workspace, relative) : null));
            workspaceFile.Name = "TerminalAttachWorkspace";
            menu = new ContextMenu { Name = "TerminalAttachMenu", Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = attach, Items = { upload, hostPath, workspaceFile } };
            attach.ContextMenu = menu;
            terminal.Accessories.Children.Insert(0, attach);
            terminal.Accessories.Children.CollectionChanged += (_, _) => Refresh();
            terminal.Output += Refresh;
            terminal.AttachedToVisualTree += (_, _) => Refresh();
            Refresh();
            void OpenAttachMenu()
            {
                Refresh();
                if (Receiver() is null || progress.Busy || !Ready()) return;
                hostPath.IsVisible = state.Supports(HostProtocol.HostDirectoryBrowse);
                menu.Open(attach);
            }
            async Task AttachPickedAsync()
            {
                Refresh();
                if (Receiver() is not { } receiver || progress.Busy || !Ready()) return;
                var workspace = terminal.Launch.WorkspaceRoot;
                try
                {
                    var files = await (PickAttachments ?? PickFilesAsync)();
                    if (files.Count == 0) return;
                    await Upload(receiver, workspace, token => ReadDroppedFilesAsync(files, token));
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { Report(error); }
                finally { Refresh(); }
            }
            // A path that already exists on the host is inserted as is; nothing is uploaded.
            async Task AttachPathAsync(Func<string, Task<string?>> choose)
            {
                Refresh();
                if (Receiver() is not { } receiver || progress.Busy || !Ready()) return;
                var workspace = terminal.Launch.WorkspaceRoot;
                try
                {
                    if (await choose(workspace) is { Length: > 0 } path) Insert(receiver, workspace, [path]);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { Report(error); }
                finally { Refresh(); }
            }
        }
        terminal.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var source = e.DataTransfer.TryGetValue(WorkspaceFileFormat);
            var valid = !progress.Busy && Ready() && Receiver() is not null && (source is null ? e.DataTransfer.Contains(DataFormat.File) : ReferenceEquals(source.Owner, workbench));
            e.DragEffects = valid ? DragDropEffects.Copy : DragDropEffects.None;
            terminal.FileDropActive = valid;
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        terminal.AddHandler(DragDrop.DragLeaveEvent, (_, _) => terminal.FileDropActive = false);
        terminal.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            terminal.FileDropActive = false;
            var receiver = Receiver();
            if (progress.Busy || !Ready() || receiver is null) return;
            var source = e.DataTransfer.TryGetValue(WorkspaceFileFormat);
            var files = e.DataTransfer.TryGetFiles()?.ToArray() ?? [];
            var workspace = terminal.Launch.WorkspaceRoot;
            e.Handled = true;
            try
            {
                if (source is not null)
                {
                    if (!ReferenceEquals(source.Owner, workbench)) return;
                    Insert(receiver, workspace, [Path.Combine(source.Root, source.Path)]);
                    return;
                }
                if (files.Length == 0) return;
                var local = files.Select(item => item.TryGetLocalPath()).ToArray();
                if (!remote && local.All(path => path is not null)) Insert(receiver, workspace, local.Select(path => path!).ToArray());
                else await Upload(receiver, workspace, token => ReadDroppedFilesAsync(files, token));
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Report(error); }
        }, RoutingStrategies.Bubble);
    }

    private WeakReference<TerminalView>? lastAgentTerminal;
    private bool deliveringShares;

    private void WireSharedAttachments()
    {
        SharedAttachments.Changed += DeliverSharedAttachments;
        Closed += (_, _) => SharedAttachments.Changed -= DeliverSharedAttachments;
    }

    /// <summary>
    /// Sends files shared into the app to the most recently focused agent terminal of the shown workspace, or the
    /// only ready one. Items wait in the queue while no agent terminal can take them (not connected, no agent yet).
    /// </summary>
    private async void DeliverSharedAttachments()
    {
        if (!SharedAttachments.HasPending || deliveringShares || !WorkspaceMounted || switchingWorkspace) return;
        var ready = documentContent.Values.OfType<TerminalView>()
            .Where(terminal => terminal.Launch.WorkspaceRoot == workspaceRoot && terminal.AcceptsAttachments?.Invoke() == true).ToArray();
        TerminalView? last = null;
        lastAgentTerminal?.TryGetTarget(out last);
        var target = ready.FirstOrDefault(terminal => ReferenceEquals(terminal, last)) ?? (ready.Length == 1 ? ready[0] : null);
        if (target?.AttachEntries is not { } attach) return;
        var entries = SharedAttachments.Take();
        if (entries.Count == 0) return;
        deliveringShares = true;
        try { await attach(entries); }
        catch (Exception error) { Report(error); }
        finally { deliveringShares = false; }
    }

    // Browses the host from the workspace root; folders are listed off the UI thread, cancelled with the dialog or the window.
    private async Task<string?> ChooseHostPathAsync(string workspace)
    {
        var request = projectRequest;
        var dialog = new PathBrowserDialog("Choose path from host", workspace, async (path, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            var listing = await Task.Run(async () => await host.ListHostDirectoryAsync(path, linked.Token), linked.Token);
            return new PathBrowserFolder(listing.Path, listing.Path, listing.Parent, listing.Home,
                listing.Entries.Select(entry => new PathBrowserEntry(entry.Name, HostChild(listing.Path, entry.Name), entry.IsDirectory)).ToArray());
        });
        return await ShowPathBrowserAsync(dialog, request);
    }

    // Lists the shown workspace one folder at a time from its root, as the Files tree does; returns a relative path.
    private async Task<string?> ChooseWorkspaceFileAsync()
    {
        var request = projectRequest;
        var dialog = new PathBrowserDialog("Choose file from workspace", "", async (path, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            var files = await Task.Run(async () => await host.ListFilesAsync(path, linked.Token), linked.Token);
            var parent = path.Length == 0 ? null : path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            return new PathBrowserFolder(path, path.Length == 0 ? "Workspace root" : path, parent, null,
                files.Select(file => new PathBrowserEntry(file.Name, file.Path, file.IsDirectory, file.GitIgnored)).ToArray());
        });
        return await ShowPathBrowserAsync(dialog, request);
    }

    // Joins with the host's separator, not this client's: an Android client may browse a Windows host.
    internal static string HostChild(string directory, string name)
    {
        var separator = directory.Contains('\\') && !directory.Contains('/') ? '\\' : '/';
        return directory.TrimEnd('/', '\\') + separator + name;
    }

    private async Task<string?> ShowPathBrowserAsync(PathBrowserDialog dialog, long request)
    {
        var undim = Dim();
        string? picked;
        try { picked = await dialog.ShowAsync(this); }
        finally { undim(); }
        return request == projectRequest ? picked : null;
    }

    // Images first; the picker still allows any file.
    private async Task<IReadOnlyList<IStorageItem>> PickFilesAsync() => await StorageProvider.OpenFilePickerAsync(new()
    {
        Title = "Attach to the agent",
        AllowMultiple = true,
        FileTypeFilter = [FilePickerFileTypes.ImageAll, FilePickerFileTypes.All]
    });

    internal static Task<IReadOnlyList<FileTransferEntry>> ReadDroppedFilesAsync(IReadOnlyList<IStorageItem> files, CancellationToken token)
    {
        return OperatingSystem.IsBrowser() ? ReadFiles() : Task.Run(ReadFiles, token);

        async Task<IReadOnlyList<FileTransferEntry>> ReadFiles()
        {
            var entries = new List<FileTransferEntry>();
            long bytes = 0;
            async Task Read(IStorageItem item, string path, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 128 || entries.Count >= 4096) throw new IOException("Drop at most 4096 entries with no more than 128 nested folders.");
                if (item.TryGetLocalPath() is { } local && new FileInfo(local).LinkTarget is not null) throw new IOException("Symbolic links cannot be transferred.");
                if (item is IStorageFolder folder)
                {
                    entries.Add(new(path, true, []));
                    await foreach (var child in folder.GetItemsAsync().WithCancellation(token)) await Read(child, path + "/" + child.Name, depth + 1);
                }
                else if (item is IStorageFile file)
                {
                    await using var input = await file.OpenReadAsync();
                    using var output = new MemoryStream();
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        bytes += read;
                        if (bytes > FileLimits.EditableBytes) throw new IOException("Drop files totalling no more than 64 MB.");
                        output.Write(buffer, 0, read);
                        if (OperatingSystem.IsBrowser()) await Task.Yield();
                    }
                    entries.Add(new(path, false, output.ToArray()));
                }
                else throw new IOException("The dropped item cannot be read.");
            }
            foreach (var item in files) await Read(item, item.Name, 0);
            return entries;
        }
    }
}