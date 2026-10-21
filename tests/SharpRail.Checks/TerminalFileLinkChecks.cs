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


    // The user's agent transcript: a full-screen TUI on the alternate screen, positioned and styled, launched without a shell.
    internal static (string Directory, string Script, string Home, (string Visible, string Path, int Line, int Column)[] Expected, string[] Plain) AgentScenario(string root)
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
        return (directory, script, home, expected, ["src/", "docs/screenshots", "scripts/", "NOTES.md", "src/Absent.cs"]);
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
        }
        Directory.Delete(root, true);
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
            window.Content = null;
            await backend.CloseAsync();
            Console.WriteLine("PASS " + name + ": " + found.Count + " agent file links underlined");
        }
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