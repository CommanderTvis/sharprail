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
                using var data = new DataTransfer();
                data.Add(DataTransferItem.Create(WorkspaceFileFormat, new WorkspaceFileDrag(workbench, root, file.Path)));
                if (!remote)
                {
                    var uri = new Uri(Path.Combine(root, file.Path));
                    IStorageItem? item = await Task.Run<IStorageItem?>(async () => file.IsDirectory ? await StorageProvider.TryGetFolderFromPathAsync(uri) : await StorageProvider.TryGetFileFromPathAsync(uri), lifetime.Token);
                    if (item is not null) data.Add(DataTransferItem.CreateFile(item));
                }
                if (root != workspaceRoot || switchingWorkspace) return;
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
            var folder = entry?.Tag is ProjectFile file ? file.IsDirectory ? file.Path : Path.GetDirectoryName(file.Path) ?? "" : "";
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
            var folder = entry?.Tag is ProjectFile file ? file.IsDirectory ? file.Path : Path.GetDirectoryName(file.Path) ?? "" : "";
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

    internal static bool CanDropPath(string path, string folder)
    {
        var target = Path.Combine(folder, Path.GetFileName(path));
        var comparison = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !target.Equals(path, comparison) && !target.StartsWith(path + Path.DirectorySeparatorChar, comparison);
    }

    internal void WireTerminalFileDrop(TerminalView terminal)
    {
        var progress = new FileTransferProgress();
        terminal.Accessories.Children.Add(progress);
        DragDrop.SetAllowDrop(terminal, true);
        Control? Receiver() => terminal.Accessories.Children.FirstOrDefault(control =>
            control.GetValue(FileDrop.AcceptsProperty)?.Invoke() == true && control.GetValue(FileDrop.AttachProperty) is not null);
        bool Ready() => !terminal.IsExited && !terminal.IsFailed && !terminal.IsDetached && !terminal.IsWatching && terminal.Backend?.Started.IsCompletedSuccessfully == true;
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
                IReadOnlyList<string> paths;
                if (source is not null)
                {
                    if (!ReferenceEquals(source.Owner, workbench)) return;
                    paths = [Path.Combine(source.Root, source.Path)];
                }
                else
                {
                    if (files.Length == 0) return;
                    var local = files.Select(item => item.TryGetLocalPath()).ToArray();
                    if (!remote && local.All(path => path is not null)) paths = local.Select(path => path!).ToArray();
                    else
                    {
                        IReadOnlyList<string>? uploaded = null;
                        await progress.RunAsync("Attaching files…", lifetime.Token, async token =>
                        {
                            var entries = await ReadDroppedFilesAsync(files, token);
                            if (workspace != workspaceRoot || switchingWorkspace || !Ready()) return;
                            uploaded = await Task.Run(async () => await host.ImportFilesAsync(new(workspace, null, entries), token), token);
                        });
                        if (uploaded is null) return;
                        paths = uploaded;
                    }
                }
                if (workspace == workspaceRoot && !switchingWorkspace && Ready() && ReferenceEquals(receiver, Receiver()))
                {
                    receiver.GetValue(FileDrop.AttachProperty)!(paths);
                    terminal.FocusTerminal();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Report(error); }
        }, RoutingStrategies.Bubble);
    }

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