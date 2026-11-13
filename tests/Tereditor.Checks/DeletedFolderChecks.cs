using System.Collections.Concurrent;
using System.Text.Json;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.Plugins.Api;

namespace Tereditor.Checks;

/// <summary>
/// A project or workspace whose folder is deleted ends its shells and stops its language servers once the grace passes; a
/// folder back before then keeps them, and a folder that returns later starts fresh ones. The stored entry stays.
/// </summary>
internal static class DeletedFolderChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task Until(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(30);
        }
    }

    internal static async Task Run(string fixture)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP deleted folders: the check drives /bin/sh"); return; }
        var root = Path.Combine(fixture, "deleted-folders");
        var deleted = Directory.CreateDirectory(Path.Combine(root, "deleted")).FullName;
        var kept = Directory.CreateDirectory(Path.Combine(root, "kept")).FullName;
        var tree = Directory.CreateDirectory(Path.Combine(root, "kept-tree")).FullName;
        foreach (var folder in new[] { deleted, kept, tree }) await File.WriteAllTextAsync(Path.Combine(folder, "code.ts"), "const target = 1;");
        var disabled = Environment.GetEnvironmentVariable("TEREDITOR_LSP_DISABLE"); Environment.SetEnvironmentVariable("TEREDITOR_LSP_DISABLE", "0");
        var state = new HostStateStore(Path.Combine(root, "state"));
        await using var terminals = new PtyTerminalService("/bin/sh");
        state.Terminals = terminals;
        using var signalsDone = new CancellationTokenSource();
        try
        {
            var now = TimeSpan.Zero;
            state.FolderClock = () => now;
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
            await state.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            await state.ChangeAsync([HostStateChange.OpenProject(deleted), HostStateChange.OpenProject(kept)]);
            state.ChangeWorkspaces(_ => [new("d", deleted, WorkspaceKinds.Default, deleted, "main", "main"), new("k", kept, WorkspaceKinds.Default, kept, "main", "main"),
                new("t", kept, WorkspaceKinds.Managed, tree, "tree", "main")]);
            var removed = new ConcurrentBag<string>();
            state.WorkspaceRemoved += removed.Add;
            var exits = new ConcurrentDictionary<string, LanguageExit>();
            state.Languages.ExitObserved = (workspace, _, exit) => exits[workspace] = exit;
            var notices = new ConcurrentQueue<TerminalSignal>();
            _ = Task.Run(async () =>
            {
                try { await foreach (var signal in terminals.WatchSignalsAsync(signalsDone.Token)) if (signal.Kind == TerminalSignalKind.Notification) notices.Enqueue(signal); }
                catch (OperationCanceledException) { }
            });

            async Task<string> Server(string workspace) =>
                (await state.Languages.ExecuteAsync(new(workspace, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").State;
            async Task Start(string workspace)
            {
                await state.Languages.ExecuteAsync(new(workspace, Operation: "status", Server: "typescript"));
                await Until(async () => await Server(workspace) == "ready", "the server in " + Path.GetFileName(workspace));
            }
            async Task<ITerminalSession> Shell(string workspace)
            {
                var session = await terminals.AttachAsync(new(PtyTerminalService.SessionFor(new TerminalRef(workspace, "shell")), workspace, "deleted-folders") { TabKey = "shell" });
                _ = Task.Run(async () => { try { await foreach (var _ in session.ReadAsync()) { } } catch (Exception) { } });
                return session;
            }
            foreach (var workspace in new[] { deleted, kept, tree }) await Start(workspace);
            var deletedShell = await Shell(deleted);
            var keptShell = await Shell(kept);
            var treeShell = await Shell(tree);
            await state.SweepFoldersAsync();

            // Both project folders vanish; one missing look, or one shorter than the grace, ends nothing.
            Directory.Delete(deleted, true);
            Directory.Delete(kept, true);
            now = TimeSpan.FromSeconds(1);
            await state.SweepFoldersAsync();
            now = TimeSpan.FromSeconds(5);
            await state.SweepFoldersAsync();
            await Task.Delay(200);
            Require(!deletedShell.Exit.IsCompleted && !keptShell.Exit.IsCompleted && exits.IsEmpty, "A folder missing for less than the grace ended its processes.");

            // One comes back before the grace passes and keeps everything; the other is deleted for good.
            Directory.CreateDirectory(kept);
            await File.WriteAllTextAsync(Path.Combine(kept, "code.ts"), "const target = 1;");
            now = TimeSpan.FromSeconds(20);
            await state.SweepFoldersAsync();
            await Until(() => Task.FromResult(deletedShell.Exit.IsCompleted), "the deleted project's shell to end");
            await Until(() => Task.FromResult(exits.ContainsKey(deleted)), "the deleted project's server to stop");
            Require(exits[deleted] is { Unexpected: false } stop && stop.Reason.Contains("its folder was deleted", StringComparison.Ordinal),
                "The deleted project's server stopped without its reason: " + exits[deleted].Reason);
            Require(!keptShell.Exit.IsCompleted && !treeShell.Exit.IsCompleted && await Server(kept) == "ready" && await Server(tree) == "ready" && !exits.ContainsKey(kept),
                "A folder back within the grace, or another project, lost its shell or server.");
            await Until(() => Task.FromResult(notices.Any(notice => notice.Title == "Closed deleted")), "the closing notice");
            Require(notices.Count(notice => notice.Title == "Closed deleted") == 1, "The closing notice was sent more than once.");
            now = TimeSpan.FromSeconds(40);
            await state.SweepFoldersAsync();
            Require(notices.Count(notice => notice.Title == "Closed deleted") == 1, "A later sweep acted on the deleted folder again.");
            Require(removed.IsEmpty, "A deleted folder removed its workspace, which tells plugins to forget its data.");
            Require(!state.Current.Projects.Contains(deleted), "The deleted project is still on the rail.");
            Console.WriteLine("PASS Deleted folders: a folder deleted past the grace ends its shells and servers once; one back in time keeps them");

            // The folder returns: the stored entry was kept, so it is back on the rail with servers and fresh shells allowed.
            Directory.CreateDirectory(deleted);
            await File.WriteAllTextAsync(Path.Combine(deleted, "code.ts"), "const target = 1;");
            now = TimeSpan.FromSeconds(41);
            await state.SweepFoldersAsync();
            Require(state.Current.Projects.Contains(deleted) && state.Current.Workspaces.Any(workspace => workspace.Id == "d"), "A returned folder's project is not back on the rail.");
            await Start(deleted);
            await using (var fresh = await terminals.AttachAsync(new(PtyTerminalService.SessionFor(new TerminalRef(deleted, "shell")), deleted, "deleted-folders") { TabKey = "shell" }))
                Require(fresh.Created, "A returned folder did not open a fresh shell.");
            Console.WriteLine("PASS Deleted folders: a returned folder starts its servers and shells again");

            // A workspace's own folder is watched the same way, and ends only its own processes.
            Directory.Delete(tree, true);
            now = TimeSpan.FromSeconds(50);
            await state.SweepFoldersAsync();
            now = TimeSpan.FromSeconds(61);
            await state.SweepFoldersAsync();
            await Until(() => Task.FromResult(treeShell.Exit.IsCompleted), "the deleted workspace's shell to end");
            await Until(() => Task.FromResult(exits.ContainsKey(tree)), "the deleted workspace's server to stop");
            Require(!keptShell.Exit.IsCompleted && await Server(kept) == "ready" && !exits.ContainsKey(kept), "A deleted workspace ended its project's processes.");
            Require(removed.IsEmpty && notices.Any(notice => notice.Title == "Closed kept-tree"), "A deleted workspace was removed, or not announced.");
            Console.WriteLine("PASS Deleted folders: a deleted workspace folder ends only its own shells and servers");
        }
        finally
        {
            signalsDone.Cancel();
            state.Terminals = null;
            await state.StopLanguagesAsync();
            Environment.SetEnvironmentVariable("TEREDITOR_LSP_DISABLE", disabled);
        }
    }
}