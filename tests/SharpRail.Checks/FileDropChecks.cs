using System.Net;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class FileDropChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Host(string root)
    {
        var fixture = Path.Combine(root, "file-drop-host");
        Directory.CreateDirectory(fixture);
        var stateDirectory = Path.Combine(root, "file-drop-state");
        var core = new ProjectServices(fixture, new HostStateStore(stateDirectory));
        IProjectServices local = new LocalProjectAdapter(core);
        await using var server = RemoteServer.Create(fixture, IPAddress.Loopback, 0, "file-drop", stateDirectory: stateDirectory);
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "file-drop");
            foreach (var (host, label) in new[] { (local, "local"), ((IProjectServices)remote, "remote") })
            {
                await host.OpenProjectAsync(fixture);
                var name = label + " folder";
                var data = Encoding.UTF8.GetBytes("binary\0bytes 🚀");
                var entries = new FileTransferEntry[] { new(name, true, []), new(name + "/empty", true, []), new(name + "/file.txt", false, data) };
                var paths = await host.ImportFilesAsync(new(fixture, "", entries));
                Require(paths.SequenceEqual([Path.Combine(fixture, name)]) && Directory.Exists(Path.Combine(paths[0], "empty")) && File.ReadAllBytes(Path.Combine(paths[0], "file.txt")).SequenceEqual(data), "Uploads preserve binary bytes, names and empty directories on both hosts.");
                await Reject(() => host.ImportFilesAsync(new(fixture, "", entries)).AsTask());
                Require(File.ReadAllBytes(Path.Combine(paths[0], "file.txt")).SequenceEqual(data), "A collision must preserve the existing bytes.");
                foreach (var bad in new[] { "../escape", "/absolute", ".git/config", "folder/../escape", "folder\\escape" })
                    await Reject(() => host.ImportFilesAsync(new(fixture, "", [new(bad, false, data)])).AsTask());
                await Reject(() => host.ImportFilesAsync(new(fixture + "-other", "", [new("wrong", false, data)])).AsTask());
                await Reject(() => host.ApplyFileActionAsync(new("copy", name, name + "/child")).AsTask());
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(paths[0], "file.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                await host.ApplyFileActionAsync(new("copy", name, label + "-copy") { WorkspaceRoot = fixture });
                Require(File.ReadAllBytes(Path.Combine(fixture, label + "-copy", "file.txt")).SequenceEqual(data) && Directory.Exists(paths[0]), "Copy preserves its source and nested contents.");
                if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(Path.Combine(fixture, label + "-copy", "file.txt")) == File.GetUnixFileMode(Path.Combine(paths[0], "file.txt")), "Host copies preserve executable permissions.");
                await host.ApplyFileActionAsync(new("rename", label + "-copy", label + "-moved") { WorkspaceRoot = fixture });
                Require(!Directory.Exists(Path.Combine(fixture, label + "-copy")) && Directory.Exists(Path.Combine(fixture, label + "-moved")), "Move relocates the folder.");
                await Reject(() => host.ApplyFileActionAsync(new("copy", name, label + "-wrong") { WorkspaceRoot = fixture + "-other" }).AsTask());
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Reject(() => host.ImportFilesAsync(new(fixture, "", [new(label + "-cancelled", false, data)]), cancelled.Token).AsTask());
                Require(!File.Exists(Path.Combine(fixture, label + "-cancelled")), "Cancellation must not install files.");
                var attachment = await host.ImportFilesAsync(new(fixture, null, [new("prompt.txt", false, data)]));
                Require(attachment.Count == 1 && File.ReadAllBytes(attachment[0]).SequenceEqual(data) && attachment[0].StartsWith(stateDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Agent uploads return a private path on the host.");
                if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(attachment[0]) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Uploaded attachments must remain private.");
                if (!OperatingSystem.IsWindows())
                {
                    var link = Path.Combine(paths[0], "link");
                    File.CreateSymbolicLink(link, Path.Combine(paths[0], "file.txt"));
                    await Reject(() => host.ApplyFileActionAsync(new("copy", name, label + "-linked")).AsTask());
                    Require(!Path.Exists(Path.Combine(fixture, label + "-linked")), "A linked descendant must reject the whole copy.");
                    File.Delete(link);
                }
                Require(!Directory.EnumerateFileSystemEntries(fixture).Any(path => Path.GetFileName(path).StartsWith(".sharprail-transfer-", StringComparison.Ordinal)), "Completed and refused transfers leave no staging folders.");
            }
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS file drop host: direct/remote uploads, binary and empty folders, copy/move, containment, collisions, private attachments and cancellation");
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or SharpRail.Host.Protocol.RpcException) { return; }
        throw new InvalidOperationException("An invalid transfer was accepted.");
    }

    internal static void WorkbenchDrops(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "file-drop-ui"));
        using var data = new DataTransfer();
        data.Add(DataTransferItem.Create(WorkbenchWindow.WorkspaceFileFormat, new WorkbenchWindow.WorkspaceFileDrag(app.Workbench, app.Root, "notes.txt")));
        var folder = app.FileRow("styles");
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, folder, new Point(60, 12), KeyModifiers.None);
        folder.RaiseEvent(over);
        Require(over.DragEffects == DragDropEffects.Move, "A tree drop offers move by default.");
        folder.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, folder, new Point(60, 12), KeyModifiers.None));
        Until(() => File.Exists(Path.Combine(app.Root, "styles", "notes.txt")));
        Require(!File.Exists(Path.Combine(app.Root, "notes.txt")), "The tree's routed drop must perform the move.");
        Until(() => !app.Window.GetVisualDescendants().OfType<SharpRail.UI.Panels.FileTransferProgress>().Any(progress => progress.Busy));
        using var moved = new DataTransfer();
        moved.Add(DataTransferItem.Create(WorkbenchWindow.WorkspaceFileFormat, new WorkbenchWindow.WorkspaceFileDrag(app.Workbench, app.Root, "styles/notes.txt")));
        folder = app.FileRow("themes");
        var copyModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control;
        over = new DragEventArgs(DragDrop.DragOverEvent, moved, folder, new Point(60, 12), copyModifier);
        folder.RaiseEvent(over);
        Require(over.DragEffects == DragDropEffects.Copy, "The platform modifier offers copy.");
        folder.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, moved, folder, new Point(60, 12), copyModifier));
        Until(() => File.Exists(Path.Combine(app.Root, "themes", "notes.txt")));
        Require(File.Exists(Path.Combine(app.Root, "styles", "notes.txt")), "Copy must preserve the tree source.");

        var storage = app.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.Combine(app.Root, "styles", "notes.txt")));
        Until(() => storage.IsCompleted);
        using var external = new DataTransfer();
        external.Add(DataTransferItem.CreateFile(storage.GetAwaiter().GetResult()!));
        var read = WorkbenchWindow.ReadDroppedFilesAsync([storage.GetAwaiter().GetResult()!], CancellationToken.None);
        Until(() => read.IsCompleted);
        Require(read.GetAwaiter().GetResult() is [{ Path: "notes.txt", IsDirectory: false }] && read.Result[0].Data.SequenceEqual(File.ReadAllBytes(Path.Combine(app.Root, "styles", "notes.txt"))), "External drops read storage bytes off the dispatcher.");

        var backend = new RecordingTerminal();
        using var terminal = new TerminalView(_ => backend, new(app.Root, "drop-probe", "", "drop-client"));
        var receiver = new Border();
        var attached = new List<string>();
        var accepts = true;
        receiver.SetValue(FileDrop.AcceptsProperty, () => accepts);
        receiver.SetValue(FileDrop.AttachProperty, paths => attached.AddRange(paths));
        terminal.Accessories.Children.Add(receiver);
        app.Window.WireTerminalFileDrop(terminal);
        var window = new Window { Content = terminal, Width = 400, Height = 200 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            over = new DragEventArgs(DragDrop.DragOverEvent, moved, terminal, new Point(30, 30), KeyModifiers.None);
            terminal.RaiseEvent(over);
            Require(over.DragEffects == DragDropEffects.Copy && terminal.FindControl<Border>("FileDropHighlight")!.IsVisible, "An agent accepts references and highlights the surface.");
            terminal.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, moved, terminal, new Point(30, 30), KeyModifiers.None));
            Require(attached.SequenceEqual([Path.Combine(app.Root, "styles/notes.txt")]) && backend.Writes.Count == 0, "A terminal routes host references to the plugin without submitting a prompt.");
            terminal.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, external, terminal, new Point(30, 30), KeyModifiers.None));
            Require(attached.Count == 2 && attached[1] == Path.Combine(app.Root, "styles", "notes.txt"), "A native external file is shared directly with a local agent.");
            accepts = false;
            terminal.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, moved, terminal, new Point(30, 30), KeyModifiers.None));
            Require(attached.Count == 2, "An accessory for an inactive agent cannot accept files.");
            accepts = true; backend.Displace(); Dispatcher.UIThread.RunJobs();
            Until(() => terminal.IsWatching);
            terminal.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, moved, terminal, new Point(30, 30), KeyModifiers.None));
            Require(attached.Count == 2, "A watching terminal cannot accept attachments or take over through a drop.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS workbench file drops: routed tree move/copy, agent callbacks, highlight, inactive agent and watcher rejection");
    }

    private sealed class RecordingTerminal : ITerminalBackend
    {
        private readonly TaskCompletionSource detached = new();
        public Control View { get; } = new Border();
        public Task Started => Task.CompletedTask;
        public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;
        public Task Detached => detached.Task;
        public bool Yielded => Watching;
        public bool Watching { get; private set; }
        internal List<string> Writes { get; } = [];
        internal void Displace() { Watching = true; detached.TrySetResult(); }
        public ValueTask<bool> IsBusyAsync() => ValueTask.FromResult(false);
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public void FocusTerminal() { }
        public void Write(string data) => Writes.Add(data);
        public string ReadScreen() => "";
        public void SetAgentNewline(bool enabled) { }
        public void Dispose() { }
    }

    internal static void Gestures()
    {
        var tree = new DrawnTree { Width = 400, Height = 200 };
        var activated = 0; var dragged = 0;
        var entry = new DrawnTreeEntry("notes.txt", "notes.txt") { Activate = _ => activated++ };
        tree.Drag = (_, _) => { dragged++; return Task.CompletedTask; };
        tree.SetRoots([entry]);
        var window = new Window { Width = 400, Height = 200, Content = tree };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        try
        {
            window.MouseDown(new Point(100, 12), MouseButton.Left);
            Require(activated == 0, "A draggable file must not open on press.");
            window.MouseUp(new Point(100, 12), MouseButton.Left);
            Require(activated == 1 && dragged == 0, "A normal click still opens a file.");
            window.MouseDown(new Point(100, 12), MouseButton.Left);
            window.MouseMove(new Point(130, 12), Avalonia.Input.RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(130, 12), MouseButton.Left);
            Require(dragged == 1 && activated == 1, "Dragging begins after a threshold without opening the file.");
            Require(!WorkbenchWindow.CanDropPath("src", "src/nested") && !WorkbenchWindow.CanDropPath("src/file", "src") && WorkbenchWindow.CanDropPath("src/file", "other"), "Self and descendant destinations are rejected.");
            var quoted = TerminalFacts.QuoteAttachPath("folder/é file with space\n.txt");
            Require(quoted.StartsWith('"') && quoted.Contains('é') && !quoted.Contains('\n') && quoted.EndsWith('"'), "Unsafe prompt path characters must be quoted without literal newlines.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS file drag gestures: deferred activation, threshold, destination rules and prompt quoting");
    }
}