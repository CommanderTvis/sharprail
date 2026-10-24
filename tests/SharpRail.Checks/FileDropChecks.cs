using System.Net;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        var source = new WorkbenchWindow.WorkspaceFileDrag(app.Workbench, app.Root, "notes.txt");
        var initialStorage = app.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.Combine(app.Root, "notes.txt")));
        Until(() => initialStorage.IsCompleted);
        var nativeFile = initialStorage.GetAwaiter().GetResult()!;
        using var data = WorkbenchWindow.CreateWorkspaceFileData(source, nativeFile);
        Require(data.Items.Count == 1 && ReferenceEquals(data.Items[0].TryGetValue(WorkbenchWindow.WorkspaceFileFormat), source)
            && ReferenceEquals(data.Items[0].TryGetFile(), nativeFile) && data.TryGetFiles()!.Single().Path == nativeFile.Path,
            "One local file must supply its internal reference and native file URL on the same drag item.");
        using var remoteData = WorkbenchWindow.CreateWorkspaceFileData(source, null);
        Require(remoteData.Items.Count == 1 && ReferenceEquals(remoteData.TryGetValue(WorkbenchWindow.WorkspaceFileFormat), source)
            && remoteData.Items[0].Formats.Any(format => format.Kind != DataFormatKind.InProcess)
            && remoteData.TryGetText() == "notes.txt" && !remoteData.Formats.Contains(DataFormat.File),
            "Remote or unavailable local files need a native text representation without claiming a client-local file URL.");
        var folder = app.FileRow("styles");
        var over = new DragEventArgs(DragDrop.DragOverEvent, data, folder, new Point(60, 12), KeyModifiers.None);
        folder.RaiseEvent(over);
        Require(over.DragEffects == DragDropEffects.Move, "A tree drop offers move by default.");
        folder.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, folder, new Point(60, 12), KeyModifiers.None));
        Until(() => File.Exists(Path.Combine(app.Root, "styles", "notes.txt")));
        Require(!File.Exists(Path.Combine(app.Root, "notes.txt")), "The tree's routed drop must perform the move.");
        Until(() => !app.Window.GetVisualDescendants().OfType<SharpRail.UI.Panels.FileTransferProgress>().Any(progress => progress.Busy));
        using var moved = WorkbenchWindow.CreateWorkspaceFileData(new(app.Workbench, app.Root, "styles/notes.txt"), null);
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
        AttachButton(app);
        Console.WriteLine("PASS workbench file drops: routed tree move/copy, agent callbacks, highlight, inactive agent and watcher rejection");
    }

    // The phone's Attach button and share target: picked or shared bytes are uploaded, never shared by client path.
    private static void AttachButton(E2eWorkspace app)
    {
        var backend = new RecordingTerminal();
        using var terminal = new TerminalView(_ => backend, new(app.Root, "attach-probe", "", "attach-client"));
        var receiver = new Border();
        var attached = new List<string>();
        var runsAgent = false;
        receiver.SetValue(FileDrop.AcceptsProperty, () => runsAgent);
        receiver.SetValue(FileDrop.AttachProperty, paths => attached.AddRange(paths));
        terminal.Accessories.Children.Add(receiver);
        // A real picker result: even with a client-local path, the button uploads rather than sharing that path.
        var source = Path.Combine(Path.GetDirectoryName(app.Root)!, "picked", "screenshot.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        File.WriteAllBytes(source, bytes);
        var lookup = app.Window.StorageProvider.TryGetFileFromPathAsync(new Uri(source));
        Until(() => lookup.IsCompleted);
        var picked = lookup.GetAwaiter().GetResult()!;
        var picks = 0;
        app.Window.AttachButtons = true;
        app.Window.PickAttachments = () => { picks++; return Task.FromResult<IReadOnlyList<IStorageItem>>([picked]); };
        // Host browsing: a sibling folder the client cannot read, and a file beside the workspace.
        var outside = Path.GetDirectoryName(app.Root)!;
        var locked = Path.Combine(outside, "locked-folder");
        Directory.CreateDirectory(locked);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(locked, UnixFileMode.None);
        app.Window.PickWorkspaceFile = () => Task.FromResult<string?>("styles/notes.txt");
        app.Window.WireTerminalFileDrop(terminal);
        var window = new Window { Content = terminal, Width = 400, Height = 200 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var attach = terminal.Accessories.Children.OfType<Button>().Single(button => button.Name == "TerminalAttach");
            Require(!attach.IsVisible && terminal.AcceptsAttachments?.Invoke() == false, "A plain shell (no running agent) hides Attach and refuses shares.");
            runsAgent = true;
            terminal.Accessories.Children.Add(new Border()); Dispatcher.UIThread.RunJobs();
            Require(attach.IsVisible, "A running agent session that accepts files shows Attach.");
            var menu = attach.ContextMenu!;
            Require(menu.Name == "TerminalAttachMenu" && menu.Items.OfType<MenuItem>().Select(item => item.Name).SequenceEqual(["TerminalAttachUpload", "TerminalAttachHostPath", "TerminalAttachWorkspace"]),
                "The Attach menu offers exactly upload, host path and workspace file.");
            MenuItem Item(string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
            Item("TerminalAttachUpload").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Until(() => attached.Count == 1);
            Require(picks == 1 && attached[0] != source && File.ReadAllBytes(attached[0]).SequenceEqual(bytes) && backend.Writes.Count == 0,
                "A picked file is uploaded to the host and its host path inserted without submitting the prompt.");
            Item("TerminalAttachHostPath").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var browser = WorkspaceFixture.Dialog(app, "PathBrowserDialog");
            TextBlock Location() => browser.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PathBrowserLocation");
            TextBlock Status() => browser.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "PathBrowserStatus");
            Button Find(string name, string? tag = null)
            {
                Until(() => browser.GetVisualDescendants().OfType<Button>().Any(button => button.Name == name && (tag is null || Equals(button.Tag, tag))));
                return browser.GetVisualDescendants().OfType<Button>().First(button => button.Name == name && (tag is null || Equals(button.Tag, tag)));
            }
            void Press(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            Until(() => Location().Text == app.Root);
            Require(Find("PathBrowserHome").IsVisible && Find("PathBrowserUp").IsEnabled, "The host browser starts at the workspace root with Up and Home.");
            Press(Find("PathBrowserUp"));
            Until(() => Location().Text == outside);
            Press(Find("PathBrowserFolder", locked));
            Until(() => Status().Text?.StartsWith("Couldn't open this folder", StringComparison.Ordinal) == true || OperatingSystem.IsWindows() || Environment.UserName == "root");
            Require(Location().Text == locked && Find("PathBrowserUp").IsEnabled, "An unreadable host folder shows its error inline and Up still leads out.");
            Press(Find("PathBrowserHome"));
            Until(() => Location().Text == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Press(Find("PathBrowserUp"));
            Until(() => Location().Text != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Press(Find("PathBrowserHome"));
            Until(() => Location().Text == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            // Navigate back to the workspace by folders, then pick a file inside it.
            browser.Close(null); Dispatcher.UIThread.RunJobs();
            Item("TerminalAttachHostPath").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            browser = WorkspaceFixture.Dialog(app, "PathBrowserDialog");
            Until(() => Location().Text == app.Root);
            Press(Find("PathBrowserFolder", WorkbenchWindow.HostChild(app.Root, "styles")));
            Until(() => Location().Text == WorkbenchWindow.HostChild(app.Root, "styles"));
            Press(Find("PathBrowserFile", WorkbenchWindow.HostChild(WorkbenchWindow.HostChild(app.Root, "styles"), "notes.txt")));
            Until(() => attached.Count == 2);
            Require(attached[1] == Path.Combine(app.Root, "styles", "notes.txt") && backend.Writes.Count == 0 && File.Exists(attached[1]),
                "A browsed host file's absolute path is inserted unsent and without uploading.");
            Item("TerminalAttachWorkspace").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Until(() => attached.Count == 3);
            Require(attached[2] == Path.Combine(app.Root, "styles/notes.txt") && backend.Writes.Count == 0,
                "A workspace file is inserted like a dragged workspace file, unsent.");
            attached.RemoveRange(1, 2);

            SharpRail.UI.Terminal.SharedAttachments.Add([new("shared.png", false, [9, 8, 7])]);
            Dispatcher.UIThread.RunJobs();
            Require(attached.Count == 1 && SharpRail.UI.Terminal.SharedAttachments.HasPending, "A share waits while no agent terminal of the shown workspace is mounted.");
            SharpRail.UI.Terminal.SharedAttachments.Take();

            backend.Displace(); Dispatcher.UIThread.RunJobs();
            Until(() => terminal.IsWatching);
            terminal.Accessories.Children.Add(new Border()); Dispatcher.UIThread.RunJobs();
            Require(!attach.IsVisible && terminal.AcceptsAttachments?.Invoke() == false, "A watching terminal hides Attach and refuses attachments.");
            attach.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Require(picks == 1 && attached.Count == 1, "A watching terminal never opens the picker or attaches.");
        }
        finally { window.Close(); app.Window.AttachButtons = false; app.Window.PickAttachments = null; app.Window.PickWorkspaceFile = null;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Console.WriteLine("PASS attach button: hidden without a running agent, three-option menu, upload, host browse (up/home/error/pick) and workspace file inserted unsubmitted, share queued without an agent, watcher rejected");
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
            Require(WorkbenchWindow.CanTransferPath("src/file", "src/copy") && !WorkbenchWindow.CanTransferPath("src\\folder", "src/folder/child"),
                "An explicit destination can change the name, while mixed path separators must not bypass descendant rejection.");
            var quoted = TerminalFacts.QuoteAttachPath("folder/é file with space\n.txt");
            Require(quoted.StartsWith('"') && quoted.Contains('é') && !quoted.Contains('\n') && quoted.EndsWith('"'), "Unsafe prompt path characters must be quoted without literal newlines.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS file drag gestures: deferred activation, threshold, destination rules and prompt quoting");
    }
}