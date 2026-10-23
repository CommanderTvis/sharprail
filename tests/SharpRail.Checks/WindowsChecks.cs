using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

using Ghostty.Avalonia;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.Scintilla;
using SharpRail.UI;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

internal static class WindowsChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Windows support: " + message); }

    internal static async Task RunHost(string root)
    {
        Require(ConPtySession.Quote(@"C:\Program Files\PowerShell\pwsh.exe") == "\"C:\\Program Files\\PowerShell\\pwsh.exe\"", "Shell paths with spaces must remain one argument.");
        Require(ConPtySession.Quote("a\\\"b\\") == "\"a\\\\\\\"b\\\\\"", "Windows argument quoting must preserve quotes and trailing slashes.");
        foreach (var path in new[] { @"C:\work\project", "/home/test/project", @"\\server\share\project" })
        {
            Require(HostPaths.IsAbsolute(path), "Profiles must preserve paths owned by either host platform.");
            var profile = new ProfileStore(Path.Combine(root, "windows-profile"));
            profile.Data.Windows[0].LastProject = path;
            profile.Data.Windows[0].LastProjectRoot = path;
            profile.Save();
            Require(new ProfileStore(profile.DirectoryPath).Data.Windows[0].LastProject == path, "Reloading a profile discarded a remote host path.");
        }
        Require(!HostPaths.IsAbsolute(@"C:relative") && !HostPaths.IsAbsolute("relative") && !HostPaths.IsAbsolute("/bad\0path"), "Invalid profile paths were accepted.");
        Require(HostPaths.Relative(@"C:\Work", @"c:\work\src\file.cs") == "src/file.cs" &&
            HostPaths.Relative("/work", "/work/src/file.cs") == "src/file.cs" &&
            HostPaths.Relative("/work", "/work-other/file.cs") == "/work-other/file.cs", "Terminal links must use the host's path rules.");
        var workspace = Path.Combine(root, "windows host λ");
        Directory.CreateDirectory(workspace);
        foreach (var path in new[] { "../outside", Path.Combine("..", "outside"), ".git/config" })
        {
            try { ProjectServices.Contain(workspace, path); throw new InvalidOperationException("Workspace containment accepted " + path); }
            catch (UnauthorizedAccessException) { }
        }
        await GitWorkspaces(root);
        Console.WriteLine("PASS Windows portable checks: quoting, remote profile paths, terminal links and containment");
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP ConPTY and Windows filesystem runtime checks: Windows is required."); return; }
        foreach (var path in new[] { @"..\outside", @".git\config", @".git.\config", "file.txt:stream" })
        {
            try { ProjectServices.Contain(workspace, path); throw new InvalidOperationException("Windows containment accepted " + path); }
            catch (UnauthorizedAccessException) { }
        }
        await using (var local = new PtyTerminalService(shell: WindowsProcesses.DefaultShell()))
        {
            local.McpEndpoint = "http://127.0.0.1:1";
            local.EnvironmentContributor = _ => new Dictionary<string, string> { ["SHARPRAIL_WINDOWS_CHECK"] = "λ😀", ["sharprail_token"] = "must-not-leak" };
            await Exercise(new LocalTerminalAdapter(local), workspace, "local");
        }
        await using var app = RemoteServer.Create(workspace, IPAddress.Loopback, 0, "windows-check-token", Path.Combine(root, "windows-host-state"));
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemoteTerminalAdapter(address, "windows-check-token");
            await Exercise(remote, workspace, "remote");
            using var project = new RemoteProjectAdapter(address, "windows-check-token");
            await Files(project, workspace);
        }
        finally { await app.StopAsync(); }
        await Files(new LocalProjectAdapter(new ProjectServices(workspace)), workspace);
        Console.WriteLine("PASS Windows runtime: ConPTY local/remote input, Unicode, resize, replay, watching, busy descendants, process cleanup and file operations");
    }

    private static async Task Exercise(ITerminalService terminals, string workspace, string mode)
    {
        var id = "windows-" + mode;
        await using var first = await terminals.AttachAsync(new(id, workspace, "holder", 120, 40) { TabKey = id });
        var screen = new Screen(first);
        await screen.Ready;
        await screen.Command("Write-Output ('READY_' + 'λ😀')");
        await screen.Contains("READY_λ😀");
        await screen.Command("Write-Output ('PID_' + $PID); Write-Output ('CWD_' + ((Get-Location).Path -eq '" + workspace.Replace("'", "''", StringComparison.Ordinal) + "')); Write-Output ('TOKEN_' + [string]::IsNullOrEmpty($env:SHARPRAIL_TOKEN))");
        var pid = int.Parse(await screen.Match("PID_([0-9]+)"));
        await screen.Contains("CWD_True");
        await screen.Contains("TOKEN_True");
        if (mode == "local")
        {
            await screen.Command("Write-Output ('ENV_' + $env:SHARPRAIL_WINDOWS_CHECK)");
            await screen.Contains("ENV_λ😀");
        }
        await first.ResizeAsync(113, 37);
        await screen.Command("Write-Output ('SIZE_' + [Console]::WindowWidth + '_' + [Console]::WindowHeight)");
        await screen.Contains("SIZE_113_37");
        await using var watcher = await terminals.AttachAsync(new(id, workspace, "watcher") { Yield = true, Watch = true });
        Require(watcher.Watching && watcher.Grid == (113, 37), "A watcher must retain the holder's grid.");
        var watching = new Screen(watcher);
        await screen.Command("Write-Output ('WATCH_' + 'LIVE')");
        await watching.Contains("WATCH_LIVE");
        var offset = first.Position;
        await first.DisposeAsync();
        await using var resumed = await terminals.AttachAsync(new(id, workspace, "holder", 113, 37, offset));
        Require(!resumed.Created, "Reconnecting started another shell.");
        var after = new Screen(resumed);
        await after.Command("Write-Output ('RESUMED_' + $PID)");
        await after.Contains("RESUMED_" + pid);
        await after.Command("$child = Start-Process -FilePath $env:ComSpec -ArgumentList '/c ping -n 60 127.0.0.1 >nul' -NoNewWindow -PassThru; Write-Output ('CHILD_' + $child.Id)");
        var child = int.Parse(await after.Match("CHILD_([0-9]+)"));
        Require(await terminals.IsBusyAsync(id), "A running child must trigger close confirmation.");
        await terminals.CloseAsync(id).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        await Until(() => Task.FromResult(Ended(pid) && Ended(child)), "Closing a tab left its shell or child running.");
        await using var exited = await terminals.AttachAsync(new(id + "-exit", workspace, "holder"));
        var ending = new Screen(exited);
        await ending.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        await ending.Command("exit 23");
        Require(await exited.Exit.WaitAsync(TimeSpan.FromSeconds(20)) == 23, "The PowerShell exit code was lost.");
        await terminals.CloseAsync(exited.Id);
    }

    private static bool Ended(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static async Task Files(IProjectServices project, string workspace)
    {
        await project.OpenProjectAsync(workspace);
        await project.ApplyFileActionAsync(new("create-file", "windows file.txt"));
        await project.SaveFileAsync(new(workspace, "windows file.txt", "", "hello λ😀\r\n"));
        Require((await project.ReadFileAsync("windows file.txt")).Text == "hello λ😀\r\n", "Windows file save changed Unicode or CRLF.");
        await project.ApplyFileActionAsync(new("rename", "windows file.txt", "renamed.txt"));
        Require((await project.ListFilesAsync("")).Any(file => file.Name == "renamed.txt"), "Renamed file was absent from the listing.");
        await project.ApplyFileActionAsync(new("trash", "renamed.txt"));
        Require(!File.Exists(Path.Combine(workspace, "renamed.txt")), "The Recycle Bin operation left the file in the workspace.");
    }

    private static async Task GitWorkspaces(string root)
    {
        var repository = Path.Combine(root, "windows git λ");
        Directory.CreateDirectory(Path.Combine(repository, "nested"));
        E2E.IsolatedGit.Repository(repository, ("nested/file.txt", "original\n"));
        var state = new HostStateStore(Path.Combine(root, "windows-git-state"));
        var local = new LocalProjectAdapter(new ProjectServices(repository, state));
        await Verify(local);
        await using var app = RemoteServer.Create(repository, IPAddress.Loopback, 0, "windows-git-token", Path.Combine(root, "windows-git-remote"));
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemoteProjectAdapter(address, "windows-git-token");
            await Verify(remote);
        }
        finally { await app.StopAsync(); }

        async Task Verify(IProjectServices project)
        {
            var opened = await project.OpenProjectAsync(repository);
            Require(opened.RootPath == Path.GetFullPath(repository), "Git changed the workspace's path identity.");
            Require((await project.ListFilesAsync("nested")).Single().Path == "nested/file.txt", "File paths must use the same separators as Git changes.");
            await project.SaveFileAsync(new(repository, "nested/file.txt", "original\n", "edited λ😀\n"));
            Require((await project.GetGitAsync()).Changes.Any(change => change.Path == "nested/file.txt"), "Git failed to report a saved file.");
            await project.SaveFileAsync(new(repository, "nested/file.txt", "edited λ😀\n", "original\n"));
            var path = Path.Combine(root, "windows worktree " + Guid.NewGuid().ToString("N")[..8]);
            var snapshot = await project.ApplyGitActionAsync(new("create-worktree", path, "windows-" + Guid.NewGuid().ToString("N")[..8]));
            Require(snapshot.Worktrees.Any(tree => tree.Path == Path.GetFullPath(path)), "A created worktree was not discoverable by its Windows path.");
            await project.ApplyGitActionAsync(new("remove-worktree", path));
            Require(!Directory.Exists(path), "Removing the worktree did not use its canonical host path.");
        }
    }

    internal static void RunUi(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var editor = new ScintillaEditor("alpha\r\nλ😀", EditorChecks.Font);
        var window = new Window { Width = 640, Height = 320, Content = editor };
        window.Show(); Pump(); editor.Focus();
        try
        {
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
            window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
            window.KeyTextInput("edited λ😀"); Pump();
            Require(editor.Text == "edited λ😀", "Ctrl+A and Unicode input did not edit the document.");
            editor.Text = "alpha\r\nλ😀";
            window.KeyTextInput("!"); Pump();
            Require(editor.Text == "!alpha\r\nλ😀" && editor.IsModified, "Text input did not edit the clean document.");
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z"); Pump();
            Require(editor.Text == "alpha\r\nλ😀" && !editor.IsModified, "Ctrl+Z did not restore the original document and save point.");
            using var view = new GhosttySkiaView { Typeface = EditorChecks.Font };
            window.Content = view; Pump();
            view.Write(Encoding.UTF8.GetBytes("Windows λ😀\r\n\e[31mred\e[0m")); Pump();
            Require(view.ReadScreen().Contains("Windows λ😀", StringComparison.Ordinal), "The Windows native terminal library did not render Unicode.");
            using var image = window.CaptureRenderedFrame();
            Require(image is not null && image.PixelSize.Width > 0, "The Windows Skia view did not paint.");
            using var backend = TerminalBackends.Ghostty(new NeverAttach(), () => TerminalRenderers.Texture)(new(root, "windows-renderer", root, "client"));
            Require(backend is SkiaTerminal, "A saved Metal preference must select Skia on Windows.");
        }
        finally { window.Content = null; window.Close(); Pump(); }
        Console.WriteLine("PASS Windows UI: native editor, Ctrl shortcuts, Skia native loading and rendering, and Metal preference migration");
    }

    private sealed class NeverAttach : ITerminalService
    {
        public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("An unmounted backend must not attach.");
        public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static void Pump()
    { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }

    private static async Task Until(Func<Task<bool>> condition, string message)
    {
        var end = Awake.Now.AddSeconds(30);
        while (!await condition())
        {
            if (Awake.Now > end) throw new InvalidOperationException(message);
            await Task.Delay(25);
        }
    }

    private sealed class Screen
    {
        private readonly ITerminalSession session;
        private readonly StringBuilder text = new();
        private readonly StringBuilder raw = new();
        private Exception? readerError;
        private bool readerEnded;
        private int chunks;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Ready => WaitForPrompt();
        private string Text { get { lock (text) return text.ToString(); } }

        private async Task WaitForPrompt()
        {
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception error)
            {
                lock (text) throw new InvalidOperationException("PowerShell prompt missing.\nScreen: " + text + "\nRaw output: " + raw + "\n" + State(), error);
            }
        }

        internal Screen(ITerminalSession session)
        {
            this.session = session;
            _ = Task.Run(async () =>
            {
                var replies = new List<byte[]>();
                using var vt = new Vt(session.Grid.Columns, session.Grid.Rows, 4000, bytes => replies.Add(bytes.ToArray()));
                try
                {
                    vt.Write(session.Replay.Span);
                    await Update(session.Replay);
                    await foreach (var chunk in session.ReadAsync())
                    {
                        vt.Resize(session.Grid.Columns, session.Grid.Rows, 8, 16);
                        vt.Write(chunk.Span);
                        await Update(chunk);
                    }
                    lock (text) readerEnded = true;
                }
                catch (Exception error)
                {
                    lock (text) readerError = error;
                    ready.TrySetException(error);
                }

                async Task Update(ReadOnlyMemory<byte> bytes)
                {
                    lock (text)
                    {
                        text.Clear(); text.Append(vt.ScreenText());
                        raw.Append(Encoding.UTF8.GetString(bytes.Span));
                        chunks++;
                        if (raw.Length > 8192) raw.Remove(0, raw.Length - 8192);
                    }
                    foreach (var reply in replies) await session.WriteAsync(reply);
                    replies.Clear();
                    if (Regex.IsMatch(Text, @"(?m)^PS [^\r\n]*> *$")) ready.TrySetResult();
                }
            });
        }

        // Whether output stopped because the stream ended, the shell exited or another client took the session.
        private string State() =>
            $"Session: created={session.Created}, watching={session.Watching}, position={session.Position}, replay={session.Replay.Length} bytes, chunks={chunks}, " +
            $"reader={(readerEnded ? "ended" : "reading")}, exit={session.Exit.Status}, detached={session.Detached.IsCompleted}";

        internal Task Command(string value) => session.WriteAsync(Encoding.UTF8.GetBytes(value + "\r")).AsTask();
        internal Task Contains(string value) => Wait(() => Text.Contains(value, StringComparison.Ordinal), "Terminal output missing: " + value);
        internal async Task<string> Match(string expression)
        {
            await Wait(() => Regex.IsMatch(Text, expression), "Terminal output missing: " + expression);
            return Regex.Match(Text, expression).Groups[1].Value;
        }

        private async Task Wait(Func<bool> condition, string message)
        {
            try
            {
                await Until(() =>
                {
                    lock (text)
                        if (readerError is { } error) throw new IOException("Terminal output reader failed.", error);
                    return Task.FromResult(condition());
                }, message);
            }
            catch (Exception error)
            {
                lock (text) throw new InvalidOperationException(message + "\nScreen: " + text + "\nRaw output: " + raw + "\n" + State(), error);
            }
        }
    }
}