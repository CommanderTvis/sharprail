using System.Net;
using System.Text;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class TerminalFileLinkChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Terminal file links: " + message); }

    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "file-links");
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "space folder"));
        var file = Path.Combine(directory, "src", "Main.cs");
        await File.WriteAllTextAsync(file, "first\nsecond\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), "readme");
        await File.WriteAllTextAsync(Path.Combine(directory, "space folder", "file name.cs"), "spaces");
        await File.WriteAllTextAsync(Path.Combine(directory, "unicode-界é.cs"), "unicode");
        try
        {
            void One(string text, string target, int line = 1, int column = 1, string? visible = null)
            {
                var links = TerminalFileLinks.Find(text, directory, directory);
                Require(links.Count == 1, $"Expected one link for '{text}', got {links.Count}.");
                var link = links[0];
                Require(link.Path == target && link.Line == line && link.Column == column, $"Wrong destination for '{text}': {link}.");
                if (visible is not null) Require(text.Substring(link.Start, link.Length) == visible, $"Wrong underline for '{text}'.");
            }
            One("src/Main.cs", file);
            One("Update(src/Main.cs)", file, visible: "src/Main.cs");
            One("`src/Main.cs:12:3`", file, 12, 3, "src/Main.cs:12:3");
            One("see ./src/Main.cs:10-20.", file, 10, visible: "./src/Main.cs:10-20");
            One("src/Main.cs: (9, 4): error", file, 9, 4, "src/Main.cs: (9, 4)");
            One("src\\Main.cs", file);
            One("--file=src/Main.cs", file);
            One("README.md.", Path.Combine(directory, "README.md"), visible: "README.md");
            One("~/src/Main.cs", file);
            One($"({file}).", file, visible: file);
            var spaced = Path.Combine(directory, "space folder", "file name.cs");
            One($"See {spaced}:42:8 for details", spaced, 42, 8, spaced + ":42:8");
            One(new Uri(spaced).AbsoluteUri, spaced);
            var punctuated = Path.Combine(directory, "space folder", "file (copy).cs");
            await File.WriteAllTextAsync(punctuated, "parentheses");
            One($"({punctuated}).", punctuated, visible: punctuated);
            One("unicode-界é.cs", Path.Combine(directory, "unicode-界é.cs"));
            await File.WriteAllTextAsync(Path.Combine(directory, "DEVELOPMENT.md"), "development");
            One("See DEVELOPMENT.md.", Path.Combine(directory, "DEVELOPMENT.md"), visible: "DEVELOPMENT.md");
            One("./DEVELOPMENT.md", Path.Combine(directory, "DEVELOPMENT.md"));
            foreach (var value in new[] { "NOTES.md", "1.13.0", "github.com", "e.g.", "done.", "main", "--lsp-ui" })
                Require(TerminalFileLinks.Find(value, directory, directory).Count == 0, "Unexpected link: " + value);
            foreach (var value in new[] { ".", "..", "...", "missing/Main.cs", "src/Absent.cs", "https://example.test/src/Main.cs", "src/Main.cs:12oops" })
                Require(TerminalFileLinks.Find(value, directory, directory).Count == 0, "Unexpected link: " + value);
            Require(TerminalFileLinks.Find("README.md", null, directory).Count == 0, "Unknown cwd linked a relative file.");
            Require(TerminalFileLinks.Find(new Uri(file).AbsoluteUri, null, directory).Count == 1, "Unknown cwd prevented a file URI.");
            Require(TerminalFileLinks.Find(file, null, directory).Count == 1, "Unknown cwd prevented an absolute link.");
            Require(TerminalFileLinks.Find("src/Main.cs README.md", directory, directory).Count == 2, "Multiple references merged.");
            Require(TerminalFileLinks.Find(new string('x', 16385), directory, directory).Count == 0, "Overlong line accepted.");
            await Mentioned(root, directory);
            await using var host = new PtyTerminalService(shell: "/bin/sh");
            var state = new HostStateStore(null) { Terminals = host };
            await Exercise(new LocalTerminalAdapter(host), directory, "local", new LocalProjectAdapter(new ProjectServices(directory, state)));
            await using var app = RemoteServer.Create(directory, IPAddress.Loopback, 0, "file-links-token", terminals: host);
            await app.StartAsync();
            try
            {
                var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var remote = new RemoteTerminalAdapter(address, "file-links-token");
                using var project = new RemoteProjectAdapter(address, "file-links-token");
                await Exercise(remote, directory, "remote", project);
            }
            finally { await app.StopAsync(); }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS terminal file parsing, existence, source positions, shell cwd and local/remote resolution");
    }

    // Absolute paths anywhere on the host link, directories included, and bare names resolve in the absolute directory
    // printed most recently before them (an agent naming a folder, then listing its files) before the shell's directory.
    private static async Task Mentioned(string root, string directory)
    {
        var outside = Path.Combine(root, "file-links-outside", "apps", "web", "src", "chat");
        Directory.CreateDirectory(Path.Combine(outside, "parts"));
        try
        {
            var composer = Path.Combine(outside, "Composer.tsx");
            await File.WriteAllTextAsync(composer, "composer");
            await File.WriteAllTextAsync(Path.Combine(outside, "ModelEffortPicker.tsx"), "picker");
            await File.WriteAllTextAsync(Path.Combine(outside, "README.md"), "outside readme");
            var file = TerminalFileLinks.Find(composer + ":3", directory, directory);
            Require(file is [{ IsDirectory: false, Line: 3 }] && file[0].Path == composer, "An absolute file outside the workspace did not link.");
            var folder = TerminalFileLinks.Find("in " + outside + ".", directory, directory);
            Require(folder is [{ IsDirectory: true }] && folder[0].Path == outside && folder[0].Length == outside.Length,
                "An absolute directory outside the workspace did not link exactly.");
            Require(TerminalFileLinks.Find("~/src", directory, directory) is [{ IsDirectory: true }], "A ~/ directory did not link.");
            Require(TerminalFileLinks.Find(Path.Combine(outside, "Missing.tsx") + " " + outside + "-missing", directory, directory).Count == 0,
                "A nonexistent absolute path linked.");
            Require(TerminalFileLinks.Find("Composer.tsx", directory, directory).Count == 0, "A bare name linked without a mentioned directory.");
            var text = $"The chat UI lives in {outside}\n- Composer.tsx\n- ModelEffortPicker.tsx and README.md, parts and src/Main.cs";
            var links = TerminalFileLinks.Find(text, directory, directory);
            string[] visible = [.. links.Select(link => text.Substring(link.Start, link.Length))];
            Require(visible.SequenceEqual([outside, "Composer.tsx", "ModelEffortPicker.tsx", "README.md", "src/Main.cs"]),
                "Bare names after a mentioned directory linked wrongly: " + string.Join(", ", visible));
            Require(links[1].Path == composer && links[2].Path == Path.Combine(outside, "ModelEffortPicker.tsx"), "A bare name missed the mentioned directory.");
            Require(links[3].Path == Path.Combine(outside, "README.md"), "The shell's directory took priority over the mentioned one.");
            Require(links[4].Path == Path.Combine(directory, "src", "Main.cs"), "A name absent from the mentioned directory missed the shell's.");
            Require(TerminalFileLinks.Find("Composer.tsx " + outside, directory, directory).Count == 1, "A directory mentioned later resolved an earlier name.");
            Require(TerminalFileLinks.Find(outside + "\nComposer.tsx", null, directory) is [_, { IsDirectory: false }],
                "A mentioned directory did not resolve without a known shell directory.");
        }
        finally { Directory.Delete(Path.Combine(root, "file-links-outside"), true); }
    }

    // The renderer's always-on underline layer must cover validated files, not only commit ids.
    internal static void RunUi(string root)
    {
        var directory = Path.Combine(root, "file-link-highlights");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        var development = Path.Combine(directory, "DEVELOPMENT.md");
        File.WriteAllText(development, "development");
        File.WriteAllText(Path.Combine(directory, "sub", "Inner.md"), "inner");
        var host = new PtyTerminalService(shell: "/bin/sh");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var session = Wait(host.AttachAsync(new("highlights", directory, "file-link-highlights"), timeout.Token).AsTask());
            var output = Task.Run(async () => { await foreach (var unused in session.ReadAsync(timeout.Token)) { } });
            var opened = new List<Ghostty.Avalonia.TerminalLink>();
            var launch = new SharpRail.UI.Terminal.TerminalLaunch(directory, "highlights", directory, "client")
            { OpenFileLink = link => { opened.Add(link); return Task.CompletedTask; } };
            using var backend = new SharpRail.UI.Terminal.SkiaTerminal(launch, host);
            var view = (Ghostty.Avalonia.GhosttySkiaView)backend.Child!;
            Require(view.ResolveHighlightedLinks is not null, "Bare file names need a visible-link resolver without a commit resolver.");
            const string text = "Read DEVELOPMENT.md and ./DEVELOPMENT.md, not NOTES.md 1.13.0 github.com e.g. done. Inner.md";
            IReadOnlyList<Ghostty.Avalonia.TerminalLink> Underlined() => Wait(Task.Run(() => view.ResolveHighlightedLinks!(text, timeout.Token)));
            var links = Underlined();
            Require(links.Select(link => text.Substring(link.Start, link.Length)).SequenceEqual(["DEVELOPMENT.md", "./DEVELOPMENT.md"]) &&
                links.All(link => link.Path == development), "Visible root files were not underlined exactly: " + string.Join(", ", links));
            var folderText = $"Folders: {directory} and sub";
            var folders = Wait(Task.Run(() => view.ResolveHighlightedLinks!(folderText, timeout.Token)));
            Require(folders is [{ IsDirectory: true }] && folders[0].Path == directory && folderText.Substring(folders[0].Start, folders[0].Length) == directory,
                "Only an absolutely named directory should be underlined: " + string.Join(", ", folders));
            Wait(view.OpenFileLink!(links[0]));
            Require(opened.SingleOrDefault()?.Path == development, "Activating an underlined bare file did not open it.");
            Wait(session.WriteAsync(Encoding.UTF8.GetBytes("cd sub\n"), timeout.Token).AsTask());
            var inner = Path.Combine(directory, "sub", "Inner.md");
            while (!links.Any(link => link.Path == inner))
            {
                Thread.Sleep(30);
                timeout.Token.ThrowIfCancellationRequested();
                links = Underlined();
            }
            Wait(host.CloseAsync("highlights", timeout.Token).AsTask());
            Wait(output);
        }
        finally
        {
            Wait(host.DisposeAsync().AsTask());
            Directory.Delete(directory, true);
        }
        Console.WriteLine("PASS terminal visible file underlines: bare and ./ names, cwd changes, false-positive words and activation");
    }


    /// <summary>A terminal link to a folder inside the workspace selects it in Files; a link to a file still opens it.</summary>
    internal static void RunWorkbench(string root)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(root, "terminal-folder-links"));
        E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        var workspace = app.Window.WorkspaceRoot;
        app.FileRow("notes.txt");
        Wait(app.Window.OpenTerminalLinkAsync(workspace, new(0, 1, Path.Combine(workspace, "styles"), 0, 0, true)));
        var tree = app.Find<SharpRail.Plugins.UI.Kit.DrawnTree>("FilesTree");
        E2E.E2eWorkspace.Until(() => tree.SelectedItem is SharpRail.Plugins.UI.Kit.DrawnTreeEntry { Id: "styles" });
        Wait(app.Window.OpenTerminalLinkAsync(workspace, new(0, 1, Path.Combine(workspace, "notes.txt"), 0, 0, false)));
        E2E.E2eWorkspace.Until(() => app.Tabs.Any(tab => tab.Path == "notes.txt"));
        Console.WriteLine("PASS terminal folder link: a folder in the workspace is selected in Files and a file still opens");
    }

    // The user's agent transcript: a full-screen TUI on the alternate screen, positioned and styled, launched without a shell.
    internal static (string Directory, string Script, string Home, (string Visible, string Path, int Line, int Column)[] Expected, string[] Plain, string[] Lines) AgentScenario(string root)
    {
        var directory = Path.Combine(root, "project");
        var sibling = Path.Combine(root, "sibling");
        var home = Path.Combine(root, "home");
        foreach (var folder in new[] { "docs/screenshots", "scripts", "src/SharpRail.Host.Core", "tests/SharpRail.Checks" })
            Directory.CreateDirectory(Path.Combine(directory, folder));
        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        foreach (var file in new[] { "AGENTS.md", "README.md", "STATUS.md", "SharpRail.slnx", "mise.toml", "DEVELOPMENT.md", "ARCHITECTURE.md", "global.json",
                     "docs/agent-reference.md", "scripts/benchmark.mjs", "src/SharpRail.Host.Core/Workspaces.SPEC.md", "tests/SharpRail.Checks/Program.cs" })
            File.WriteAllText(Path.Combine(directory, file), string.Concat(Enumerable.Repeat("line\n", 30)));
        File.WriteAllText(Path.Combine(sibling, "Other.cs"), "other");
        File.WriteAllText(Path.Combine(home, ".claude", "CLAUDE.md"), "home");
        string In(string relative) => Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        var absolute = In("ARCHITECTURE.md");
        var json = In("global.json");
        var external = Path.Combine(sibling, "Other.cs");
        string[] lines =
        [
            "\e[1m●\e[0m Read \e[1mAGENTS.md\e[0m, README.md, STATUS.md, SharpRail.slnx and mise.toml",
            "  \e[2m⎿\e[0m  See docs/agent-reference.md and scripts/benchmark.mjs, also ./DEVELOPMENT.md",
            "  Spec \e[36msrc/SharpRail.Host.Core/Workspaces.SPEC.md\e[39m with tests/SharpRail.Checks/Program.cs",
            "  At tests/SharpRail.Checks/Program.cs:10 and src/SharpRail.Host.Core/Workspaces.SPEC.md:5:3",
            "  \e[34mdocs/agent-reference.md:20\e[0m and ~/.claude/CLAUDE.md",
            "  " + absolute, "  " + json, "  " + external,
            "  Directories src/ docs/screenshots scripts/ and missing NOTES.md src/Absent.cs"
        ];
        var script = Path.Combine(root, "agent.sh");
        // Absolute paths may soft-wrap, so each line gets three rows.
        var body = new StringBuilder("#!/bin/sh\nprintf '\\033[?1049h\\033[?1000h\\033[?25l\\033[2J\\033[H'\n");
        for (var i = 0; i < lines.Length; i++)
            body.Append("printf '%b' '\\033[").Append(i * 3 + 2).Append(";1H").Append(lines[i].Replace("\e", "\\033").Replace("'", "'\\''")).Append("'\n");
        body.Append("exec sleep 600\n");
        File.WriteAllText(script, body.ToString());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        (string, string, int, int)[] expected =
        [
            ("AGENTS.md", In("AGENTS.md"), 1, 1), ("README.md", In("README.md"), 1, 1), ("STATUS.md", In("STATUS.md"), 1, 1),
            ("SharpRail.slnx", In("SharpRail.slnx"), 1, 1), ("mise.toml", In("mise.toml"), 1, 1),
            ("docs/agent-reference.md", In("docs/agent-reference.md"), 1, 1), ("scripts/benchmark.mjs", In("scripts/benchmark.mjs"), 1, 1),
            ("./DEVELOPMENT.md", In("DEVELOPMENT.md"), 1, 1),
            ("src/SharpRail.Host.Core/Workspaces.SPEC.md", In("src/SharpRail.Host.Core/Workspaces.SPEC.md"), 1, 1),
            ("tests/SharpRail.Checks/Program.cs", In("tests/SharpRail.Checks/Program.cs"), 1, 1),
            ("tests/SharpRail.Checks/Program.cs:10", In("tests/SharpRail.Checks/Program.cs"), 10, 1),
            ("src/SharpRail.Host.Core/Workspaces.SPEC.md:5:3", In("src/SharpRail.Host.Core/Workspaces.SPEC.md"), 5, 3),
            ("docs/agent-reference.md:20", In("docs/agent-reference.md"), 20, 1),
            ("~/.claude/CLAUDE.md", Path.Combine(home, ".claude", "CLAUDE.md"), 1, 1),
            (absolute, absolute, 1, 1), (json, json, 1, 1), (external, external, 1, 1)
        ];
        return (directory, script, home, expected, ["src/", "docs/screenshots", "scripts/", "NOTES.md", "src/Absent.cs"], lines);
    }

    // The Metal renderers need a real AppKit window, so the scenario runs in a child of this binary.
    internal static void RunNativeRenderers()
    {
        var self = Environment.ProcessPath!;
        var start = new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") start.ArgumentList.Add(typeof(TerminalFileLinkChecks).Assembly.Location);
        start.ArgumentList.Add("--native-terminal-links");
        using var child = System.Diagnostics.Process.Start(start)!;
        if (!child.WaitForExit(TimeSpan.FromMinutes(2))) { child.Kill(true); throw new TimeoutException("Native terminal link check timed out."); }
        Require(child.ExitCode == 0, "native renderer scenario failed.");
    }

    // Every renderer the product composes, wired exactly as App does (the loopback wrapper locally, the relay remotely),
    // shows the agent transcript; each must underline every existing file form with exact ranges and open line/column.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    internal static async Task RunRenderers(Avalonia.Controls.Window window)
    {
        var root = CheckRoot.Create("renderer-terminal-links");
        var scenario = AgentScenario(root);
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        // Every system launch goes through this seam: a file link must never reach Finder or a default app.
        var launched = new List<Uri>();
        var previousLaunch = Ghostty.Avalonia.TerminalLinkLauncher.Launch;
        Ghostty.Avalonia.TerminalLinkLauncher.Launch = (_, uri) => { launched.Add(uri); return Task.CompletedTask; };
        Environment.SetEnvironmentVariable("HOME", scenario.Home);
        SharpRail.Plugins.UI.Kit.Ui.Apply(SharpRail.UI.Rendering.Themes.All[0]);
        await using var host = new PtyTerminalService(shell: scenario.Script);
        await using var loopback = new LoopbackServer(host);
        var local = new SharpRail.UI.Terminal.LoopbackTerminals(new LocalTerminalAdapter(host), loopback);
        const string token = "renderer-links-token";
        await using var server = RemoteServer.Create(scenario.Directory, IPAddress.Loopback, 0, token, terminals: host);
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemoteTerminalAdapter(address, token);
            var connection = new SharpRail.UI.Terminal.RemoteTerminalConnection(address, token, remote);
            await Renderer("Metal texture (local)", SharpRail.UI.Terminal.TerminalBackends.Ghostty(local, () => SharpRail.UI.Terminal.TerminalRenderers.Texture));
            await Renderer("Skia (local)", SharpRail.UI.Terminal.TerminalBackends.Ghostty(local, () => SharpRail.UI.Terminal.TerminalRenderers.Skia));
            await Renderer("Metal texture (remote relay)", SharpRail.UI.Terminal.TerminalBackends.Ghostty(connection, () => SharpRail.UI.Terminal.TerminalRenderers.Texture));
        }
        finally
        {
            await server.StopAsync();
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Ghostty.Avalonia.TerminalLinkLauncher.Launch = previousLaunch;
        }
        Directory.Delete(root, true);
        await FileHyperlinksStayInApp(window);
        await UnderlinesFollowFrames(window);
        Console.WriteLine("PASS agent terminal links in Metal (local and relay) and Skia: bare, ./, relative, :line, :line:col, absolute, external and ~/ files underlined exactly; directories and missing names plain; activation opens line/column");

        async Task Renderer(string name, SharpRail.UI.Terminal.TerminalFactory factory)
        {
            var opened = new List<Ghostty.Avalonia.TerminalLink>();
            var launch = new SharpRail.UI.Terminal.TerminalLaunch(scenario.Directory, "agent-" + Guid.NewGuid().ToString("N"), Path.Combine(root, "clipboard"), "client")
            { OpenFileLink = link => { opened.Add(link); return Task.CompletedTask; } };
            using var backend = factory(launch);
            window.Width = 1400; window.Height = 900;
            window.Content = backend.View;
            window.Show();
            await backend.Started;
            IReadOnlyList<(string Visible, Ghostty.Avalonia.TerminalLink Link)> found = [];
            Func<Ghostty.Avalonia.TerminalLink, Task>? open = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && found.Count < scenario.Expected.Length)
            {
                await Task.Delay(100);
                (found, open) = ((Avalonia.Controls.Decorator)backend.View).Child switch
                {
                    Ghostty.Avalonia.GhosttyTextureView texture => (texture.HighlightedLinks, texture.OpenFileLink),
                    Ghostty.Avalonia.GhosttySkiaView skia => (skia.HighlightedLinks, skia.OpenFileLink),
                    _ => (found, open)
                };
            }
            var shown = string.Join(" | ", found.Select(item => item.Visible));
            foreach (var (visible, path, line, column) in scenario.Expected)
                Require(found.Any(item => item.Visible == visible && item.Link.Path == path && item.Link.Line == line && item.Link.Column == column),
                    $"{name} did not underline '{visible}' -> {path}:{line}:{column}; underlined: [{shown}].");
            Require(found.Count == scenario.Expected.Length && !found.Any(item => scenario.Plain.Contains(item.Visible)), $"{name} underlined extra text: [{shown}].");
            await open!(found.Single(item => item.Visible.EndsWith("Workspaces.SPEC.md:5:3", StringComparison.Ordinal)).Link);
            Require(opened.SingleOrDefault() is { Line: 5, Column: 3 } target && target.Path == scenario.Expected[11].Path, name + " activation opened the wrong place.");
            // A plain click on the underlined text opens it in the application, at its line and column.
            var view = ((Avalonia.Controls.Decorator)backend.View).Child!;
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            Avalonia.Point? Cell(int column, int row) => view switch
            {
                Ghostty.Avalonia.GhosttyTextureView texture => texture.CellCenter(column, row),
                Ghostty.Avalonia.GhosttySkiaView skia => skia.CellCenter(column, row),
                _ => null
            };
            void Click(Avalonia.Point point, Avalonia.Input.KeyModifiers modifiers)
            {
                var raw = modifiers == Avalonia.Input.KeyModifiers.Meta ? Avalonia.Input.RawInputModifiers.Meta : Avalonia.Input.RawInputModifiers.None;
                view.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, view, pointer, view, point, 1,
                    new Avalonia.Input.PointerPointProperties(raw, Avalonia.Input.PointerUpdateKind.Other), modifiers));
                view.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(view, pointer, view, point, 2,
                    new Avalonia.Input.PointerPointProperties(raw | Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), modifiers, 1));
                view.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(view, pointer, view, point, 3,
                    new Avalonia.Input.PointerPointProperties(raw, Avalonia.Input.PointerUpdateKind.LeftButtonReleased), modifiers, Avalonia.Input.MouseButton.Left));
            }
            async Task<Ghostty.Avalonia.TerminalLink> Opened(string what)
            {
                var until = DateTime.UtcNow.AddSeconds(10);
                while (opened.Count < 2 && DateTime.UtcNow < until) await Task.Delay(50);
                Require(opened.Count == 2, $"{name}: {what} did not open in the application.");
                var result = opened[1];
                opened.RemoveAt(1);
                return result;
            }
            var spec = scenario.Lines[3].IndexOf("src/SharpRail.Host.Core/Workspaces.SPEC.md:5:3", StringComparison.Ordinal);
            Click(Cell(spec + 5, 3 * 3 + 1) ?? throw new InvalidOperationException(name + " has no cell geometry."), Avalonia.Input.KeyModifiers.None);
            Require(await Opened("a plain click on an underlined file") is { Line: 5, Column: 3 } clicked && clicked.Path == scenario.Expected[11].Path,
                name + " plain click opened the wrong place.");
            Require(launched.Count == 0, $"{name} sent a terminal link to the system launcher: {string.Join(", ", launched)}.");
            window.Content = null;
            await backend.CloseAsync();
            Console.WriteLine("PASS " + name + ": " + found.Count + " agent file links underlined");
        }
    }

    // Ghostty opens its own links (OSC 8 hyperlinks, as agents and `ls --hyperlink` print) on Command-click. A file
    // hyperlink must open in the application through the terminal's resolver, never in Finder or a default app.
    // Ghostty only hovers links while no program tracks the mouse, as with Claude Code at its prompt.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static async Task FileHyperlinksStayInApp(Avalonia.Controls.Window window)
    {
        var root = CheckRoot.Create("terminal-file-hyperlinks");
        var file = Path.Combine(root, "Linked.cs");
        File.WriteAllText(file, "linked");
        var launched = new List<Uri>();
        var previousLaunch = Ghostty.Avalonia.TerminalLinkLauncher.Launch;
        Ghostty.Avalonia.TerminalLinkLauncher.Launch = (_, uri) => { launched.Add(uri); return Task.CompletedTask; };
        var resolved = new List<string>();
        var opened = new List<Ghostty.Avalonia.TerminalLink>();
        try
        {
            using var view = new Ghostty.Avalonia.GhosttyTextureView(new Ghostty.Avalonia.GhosttyLaunch(root, ExternalIo: new Ghostty.Avalonia.GhosttyExternalIo()))
            {
                ResolveFileLinks = (text, _) =>
                {
                    lock (resolved) resolved.Add(text);
                    var uri = new Uri(file).AbsoluteUri;
                    return Task.FromResult<IReadOnlyList<Ghostty.Avalonia.TerminalLink>>(text == uri ? [new(0, uri.Length, file, 1, 1, false)] : []);
                },
                OpenFileLink = link => { opened.Add(link); return Task.CompletedTask; }
            };
            window.Width = 900; window.Height = 600;
            window.Content = view;
            window.Show();
            await Task.Delay(500);
            // The URI names this machine, as `ls --hyperlink` writes it; the terminal's host resolves the path.
            view.WriteOutput(Encoding.UTF8.GetBytes($"\e[?25l\e[2J\e[H\e]8;;file://{System.Net.Dns.GetHostName()}{new Uri(file).AbsolutePath}\e\\hyperlinked file\e]8;;\e\\ and \e]8;;https://example.com/page\e\\a web page\e]8;;\e\\"));
            await Task.Delay(300);
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            async Task CommandClick(int column)
            {
                var point = view.CellCenter(column, 0) ?? throw new InvalidOperationException("Terminal file links: no Metal cell geometry.");
                var meta = Avalonia.Input.RawInputModifiers.Meta;
                view.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, view, pointer, view, point, 1,
                    new Avalonia.Input.PointerPointProperties(meta, Avalonia.Input.PointerUpdateKind.Other), Avalonia.Input.KeyModifiers.Meta));
                await Task.Delay(100);
                view.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(view, pointer, view, point, 2,
                    new Avalonia.Input.PointerPointProperties(meta | Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), Avalonia.Input.KeyModifiers.Meta, 1));
                view.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(view, pointer, view, point, 3,
                    new Avalonia.Input.PointerPointProperties(meta, Avalonia.Input.PointerUpdateKind.LeftButtonReleased), Avalonia.Input.KeyModifiers.Meta, Avalonia.Input.MouseButton.Left));
            }
            await CommandClick(3);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (opened.Count == 0 && launched.Count == 0 && DateTime.UtcNow < until) await Task.Delay(50);
            Require(launched.Count == 0, "A Command-click on an OSC 8 file hyperlink went to the system launcher (Finder): " + string.Join(", ", launched));
            Require(opened.SingleOrDefault()?.Path == file, "A Command-click on an OSC 8 file hyperlink did not open it in the application; resolved: " + string.Join(", ", resolved));
            await CommandClick("hyperlinked file and ".Length + 2);
            until = DateTime.UtcNow.AddSeconds(10);
            while (launched.Count == 0 && DateTime.UtcNow < until) await Task.Delay(50);
            Require(launched.SingleOrDefault()?.AbsoluteUri == "https://example.com/page" && opened.Count == 1, "A web hyperlink no longer reached the browser.");
            Require(Equals(Avalonia.Controls.ToolTip.GetTip(view), "https://example.com/page"), "The hovered Metal hyperlink did not show its target: " + Avalonia.Controls.ToolTip.GetTip(view));
            // OSC 22: Ghostty reports the requested shape; off a link the view shows it as an Avalonia cursor.
            view.WriteOutput("\e]22;wait\e\\"u8);
            var plain = view.CellCenter(5, 3) ?? throw new InvalidOperationException("Terminal file links: no Metal cell geometry.");
            until = DateTime.UtcNow.AddSeconds(10);
            while (view.PointerShape != Avalonia.Input.StandardCursorType.Wait && DateTime.UtcNow < until)
            {
                view.RaiseEvent(new Avalonia.Input.PointerEventArgs(Avalonia.Input.InputElement.PointerMovedEvent, view, pointer, view, plain, 4,
                    new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.Other), Avalonia.Input.KeyModifiers.None));
                await Task.Delay(50);
            }
            Require(view.PointerShape == Avalonia.Input.StandardCursorType.Wait && Avalonia.Controls.ToolTip.GetTip(view) is null,
                $"OSC 22 wait showed {view.PointerShape} in Metal, or the link tooltip outlived the hover.");
            window.Content = null;
        }
        finally
        {
            Ghostty.Avalonia.TerminalLinkLauncher.Launch = previousLaunch;
            Directory.Delete(root, true);
        }
        Console.WriteLine("PASS Metal OSC 8 file hyperlinks open in the application (never the system launcher); web hyperlinks still reach the browser and show their target; OSC 22 sets the cursor");
    }

    // Underlines belong to the frame they are presented in. While output streams and the view scrolls, every
    // underline in a captured window frame must be the link's own colour (the renderer draws it from the cell's
    // style) and sit under link text in that same frame. An overlay drawn from older cell geometry is the
    // terminal's foreground colour and lands under other rows or blank cells.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    internal static async Task UnderlinesFollowFrames(Avalonia.Controls.Window window)
    {
        const string name = "LinkFile.cs";
        static Task<IReadOnlyList<Ghostty.Avalonia.TerminalLink>> Resolve(string text, CancellationToken token)
        {
            var links = new List<Ghostty.Avalonia.TerminalLink>();
            for (var at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
                links.Add(new(at, name.Length, "/project/" + name, 1, 1, false));
            return Task.FromResult<IReadOnlyList<Ghostty.Avalonia.TerminalLink>>(links);
        }
        foreach (var renderer in new[] { "Metal texture", "Skia" })
        {
            Avalonia.Controls.Control view;
            Action<byte[]> write;
            Func<(int Columns, int Rows)> grid;
            IDisposable owner;
            if (renderer == "Skia")
            {
                var skia = new Ghostty.Avalonia.GhosttySkiaView { ResolveHighlightedLinks = Resolve };
                (view, write, grid, owner) = (skia, bytes => skia.Write(bytes), () => (skia.Size.Columns, skia.Size.Rows), skia);
            }
            else
            {
                var texture = new Ghostty.Avalonia.GhosttyTextureView(new Ghostty.Avalonia.GhosttyLaunch(Path.GetTempPath(), ExternalIo: new Ghostty.Avalonia.GhosttyExternalIo())) { ResolveHighlightedLinks = Resolve };
                (view, write, grid, owner) = (texture, bytes => texture.WriteOutput(bytes), () => (texture.Size.Columns, texture.Size.Rows), texture);
            }
            using var disposing = owner;
            window.Width = 900; window.Height = 600;
            window.Content = view;
            window.Show();
            await Task.Delay(500);
            write(Encoding.UTF8.GetBytes("\e[?25l\e[2J\e[H"));
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), Avalonia.Input.PointerType.Mouse, true);
            var frames = 0;
            var underlined = 0;
            async Task Sample(string phase, bool scaled = false)
            {
                using var image = await NativeTextureChecks.CaptureWindow();
                var (columns, rows) = grid();
                var scale = image.Width / window.Bounds.Width;
                var problem = Misplaced(image, view.Bounds.Width * scale / columns, view.Bounds.Height * scale / rows, out var found);
                // A frame presented mid-resize is the previous texture scaled to the new bounds, which blends the
                // underline's colour; it must still carry every underline, but its colours are not exact.
                Require(scaled || problem is null, $"{renderer} {phase}, frame {frames}: {problem}");
                var missing = Bare(image, view.Bounds.Width * scale / columns, view.Bounds.Height * scale / rows);
                Require(missing is null, $"{renderer} {phase}, frame {frames}: {missing}");
                underlined += found;
                frames++;
            }
            // The link line resolves once; from then on every frame showing that line text must underline it, however
            // the view rebuilds, without waiting for another resolution (a new line may underline in a later frame).
            var linkLine = $"\e[38;2;255;0;0m{name}\e[0m opened\r\n";
            write(Encoding.UTF8.GetBytes(linkLine));
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(100);
                using var image = await NativeTextureChecks.CaptureWindow();
                var (columns, rows) = grid();
                var scale = image.Width / window.Bounds.Width;
                if (Bare(image, view.Bounds.Width * scale / columns, view.Bounds.Height * scale / rows) is null) break;
            }
            // Stream: the link line on every third line, plain text between, scrolling the screen as it goes.
            for (var line = 0; line < 120; line++)
            {
                write(Encoding.UTF8.GetBytes(line % 3 == 0 ? linkLine : $"plain line {line} words here\r\n"));
                if (line % 6 == 5) { await Task.Delay(15); if (line % 12 == 11) await Sample("while output streams"); }
            }
            await Task.Delay(600);
            await Sample("after output settled");
            Require(underlined > 0, renderer + " drew no link underline in the frame.");
            // Scroll back through history and down again without waiting for a new link resolution.
            var point = new Avalonia.Point(view.Bounds.Width / 2, view.Bounds.Height / 2);
            foreach (var delta in new[] { 3, 3, 3, 3, -3, -3, 5, -5 })
            {
                view.RaiseEvent(new Avalonia.Input.PointerWheelEventArgs(view, pointer, view, point, 10, new Avalonia.Input.PointerPointProperties(),
                    Avalonia.Input.KeyModifiers.None, new Avalonia.Vector(0, delta)));
                await Sample("while scrolling");
                await Task.Delay(40);
                await Sample("after a scroll step");
            }
            // Rebuilds: an output burst that rewrites a line in place, then resizes that reflow the grid. Links already
            // resolved stay underlined in every frame; nothing waits for a new resolution.
            for (var burst = 0; burst < 6; burst++)
            {
                write(Encoding.UTF8.GetBytes($"\rprogress {burst} of 6\e[K"));
                await Sample("during an output burst");
            }
            write(Encoding.UTF8.GetBytes("\r\e[K"));
            foreach (var (width, height) in new[] { (860.0, 600.0), (900.0, 560.0), (900.0, 600.0) })
            {
                window.Width = width; window.Height = height;
                await Task.Delay(30);
                await Sample("while resizing", scaled: true);
                await Task.Delay(120);
                await Sample("after a resize");
            }
            window.Content = null;
            Console.WriteLine($"PASS {renderer}: link underlines drawn in-frame under link text across {frames} frames of streaming and scrolling ({underlined} underline runs)");
        }
    }

    // Finds horizontal underline runs (at least three cells wide; glyph strokes are shorter) and returns why one is
    // wrong: not the link's red, or with no red link text directly above it in the same image.
    private static string? Misplaced(SkiaSharp.SKBitmap image, double cellWidth, double cellHeight, out int runs)
    {
        runs = 0;
        var background = image.GetPixel(1, 1);
        static bool Red(SkiaSharp.SKColor color) => color.Red > 170 && color.Green < 100 && color.Blue < 100;
        bool Ink(SkiaSharp.SKColor color) =>
            Math.Abs(color.Red - background.Red) + Math.Abs(color.Green - background.Green) + Math.Abs(color.Blue - background.Blue) > 90;
        var minimum = (int)(cellWidth * 3);
        for (var y = (int)cellHeight / 2; y < image.Height; y++)
            for (var x = 0; x < image.Width;)
            {
                if (!Ink(image.GetPixel(x, y))) { x++; continue; }
                var start = x;
                var red = 0;
                for (; x < image.Width && Ink(image.GetPixel(x, y)); x++) if (Red(image.GetPixel(x, y))) red++;
                if (x - start < minimum) continue;
                runs++;
                if (red < (x - start) * 0.8) return $"an underline at y={y}, x={start}..{x} is not the link's colour (an overlay, not the renderer)";
                var text = 0;
                for (var above = Math.Max(0, y - (int)cellHeight); above < y - 1; above++)
                    for (var column = start; column < x; column++)
                        if (Red(image.GetPixel(column, above))) text++;
                if (text < cellWidth) return $"an underline at y={y}, x={start}..{x} has no link text above it in the same frame";
            }
        return null;
    }

    // Returns why a frame shows link text without its underline: a red glyph pixel with no red underline run (at least
    // three cells wide) below it within one cell. The bottom row may be clipped, so it is skipped.
    private static string? Bare(SkiaSharp.SKBitmap image, double cellWidth, double cellHeight)
    {
        var pixels = image.Pixels;
        var (width, height) = (image.Width, image.Height);
        static bool Red(SkiaSharp.SKColor color) => color.Red > 170 && color.Green < 100 && color.Blue < 100;
        var minimum = (int)(cellWidth * 3);
        var underline = new bool[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width;)
            {
                if (!Red(pixels[y * width + x])) { x++; continue; }
                var start = x;
                while (x < width && Red(pixels[y * width + x])) x++;
                if (x - start >= minimum) for (var i = start; i < x; i++) underline[y * width + i] = true;
            }
        for (var y = 0; y < height - (int)(cellHeight * 1.5); y++)
            for (var x = 0; x < width; x++)
            {
                if (underline[y * width + x] || !Red(pixels[y * width + x])) continue;
                var below = false;
                for (var down = y + 1; down <= Math.Min(height - 1, y + (int)cellHeight) && !below; down++) below = underline[down * width + x];
                if (!below) return $"link text at x={x}, y={y} has no underline in this frame";
            }
        return null;
    }

    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.Result; }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    private static async Task Exercise(ITerminalService service, string directory, string id, IProjectServices project)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = await service.AttachAsync(new(id, directory, "file-links-check"), timeout.Token);
        var output = Task.Run(async () => { await foreach (var unused in session.ReadAsync(timeout.Token)) { } });
        try
        {
            var first = await service.FindFileLinksAsync(id, "src/Main.cs:2:3", timeout.Token);
            Require(first.Count == 1 && first[0].Line == 2 && first[0].Column == 3, id + " initial cwd or source position missing.");
            await session.WriteAsync(Encoding.UTF8.GetBytes("cd src\n"), timeout.Token);
            IReadOnlyList<TerminalFileLink> moved;
            do
            {
                await Task.Delay(30, timeout.Token);
                moved = await service.FindFileLinksAsync(id, "Main.cs", timeout.Token);
            } while (moved.Count == 0);
            Require((await service.FindFileLinksAsync(id, "src/Main.cs", timeout.Token)).Count == 0, id + " used stale launch directory.");
            var path = Path.Combine(directory, "src", "Main.cs");
            Require(moved[0].Path == path, id + " changed directory resolved wrong file.");
            Require((await service.FindFileLinksAsync("unknown", path, timeout.Token)).Count == 0, id + " accepted unknown session.");
            var external = Path.Combine(Path.GetDirectoryName(directory)!, id + "-linked-file.txt");
            await File.WriteAllTextAsync(external, "external target", timeout.Token);
            try
            {
                var resolved = await service.FindFileLinksAsync(id, external, timeout.Token);
                Require(resolved.Count == 1, id + " did not resolve an external absolute file.");
                Require((await project.ReadFileAsync(external, timeout.Token)).Text == "external target", id + " external file did not open.");
                File.Delete(external);
                Require((await service.FindFileLinksAsync(id, external, timeout.Token)).Count == 0, id + " linked a deleted file.");
            }
            finally { File.Delete(external); }
        }
        finally
        {
            await service.CloseAsync(id, timeout.Token);
            await output;
        }
    }
}