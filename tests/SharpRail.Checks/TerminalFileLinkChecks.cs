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