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