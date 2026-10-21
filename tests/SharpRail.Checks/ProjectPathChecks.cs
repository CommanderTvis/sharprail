using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Project path resolution and inspection, equal through the embedded host and a real WebSocket RPC host.</summary>
internal static class ProjectPathChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run(string root)
    {
        var home = Path.Combine(root, "path-home");
        Require(ProjectPaths.Resolve("~", home) == home && ProjectPaths.Resolve("~/a/../b", home) == Path.Combine(home, "b") &&
            ProjectPaths.Resolve(Path.Combine(root, "x", "..", "y"), home) == Path.Combine(root, "y"), "Project paths resolve ~ against the host home and normalise.");
        foreach (var relative in new[] { "relative", "./here", "~user/x", "" })
            try { ProjectPaths.Resolve(relative, home); throw new InvalidOperationException($"A relative project path was accepted: {relative}"); }
            catch (ArgumentException) { }

        // Upstream #775: one stored spelling of a directory. Separators are the host's, a trailing one goes except at a
        // root, and case is kept. Windows drive and backslash forms run only on a Windows host and are unverified here.
        var sep = Path.DirectorySeparatorChar;
        Require(ProjectPaths.Resolve(Path.Combine(root, "Mixed", "Case") + sep, home) == Path.Combine(root, "Mixed", "Case") &&
            ProjectPaths.DirectoryIdentity(Path.GetPathRoot(root)!) == Path.GetPathRoot(root) &&
            ProjectPaths.Heal(Path.Combine(root, "healed") + sep + sep) == Path.Combine(root, "healed") && ProjectPaths.Heal("relative/") == "relative/",
            "A directory identity is resolved, keeps its case and loses a trailing separator except at a root.");
        if (OperatingSystem.IsWindows())
            Require(ProjectPaths.Resolve(@"~\repo", home) == Path.Combine(home, "repo") && ProjectPaths.DirectoryIdentity("C:/Users/me/repo/") == @"C:\Users\me\repo" &&
                ProjectPaths.DirectoryIdentity(@"C:\") == @"C:\", "Windows spells a directory one way whatever git printed.");
        Require(SharpRail.UI.HostPaths.Parent("src/auth/login.ts") == "src/auth" && SharpRail.UI.HostPaths.Parent("top.txt") == "" &&
            SharpRail.UI.HostPaths.Join("src", @"auth\new.ts") == "src/auth/new.ts" && SharpRail.UI.HostPaths.Join("", "a/b.md") == "a/b.md" &&
            SharpRail.UI.HostPaths.IsUnder("src/auth/x", "src") && !SharpRail.UI.HostPaths.IsUnder("srcx/a", "src") && !SharpRail.UI.HostPaths.IsUnder("Src/a", "src"),
            "Workspace-relative paths join and split with '/' only, case-sensitively.");
        var stateDirectory = Path.Combine(root, "path-state");
        Directory.CreateDirectory(stateDirectory);
        var healed = Path.Combine(root, "healed-project");
        Directory.CreateDirectory(healed); Directory.CreateDirectory(healed + "-recent");
        File.WriteAllText(Path.Combine(stateDirectory, HostStateStore.FileName),
            System.Text.Json.JsonSerializer.Serialize(new { Projects = new[] { healed + sep }, RecentProjects = new[] { healed + "-recent" + sep + sep } }));
        var loaded = new HostStateStore(stateDirectory).Current;
        Require(loaded.Projects.SequenceEqual([healed]) && loaded.RecentProjects.SequenceEqual([healed + "-recent"]), $"Stored directories are healed to the one spelling on load: {string.Join(",", loaded.Projects)} / {string.Join(",", loaded.RecentProjects)}.");
        var listing = E2E.IsolatedGit.Repository(Path.Combine(root, "path-listing"));
        Directory.CreateDirectory(Path.Combine(listing, "src", "auth"));
        Directory.CreateDirectory(Path.Combine(listing, "src", "ui"));
        File.WriteAllText(Path.Combine(listing, "src", "auth", "login.ts"), "x");
        var services = new ProjectServices(listing);
        Require((await services.ListFilesAsync("src")).Select(file => file.Path).Order().SequenceEqual(["src/auth", "src/ui"]) &&
            (await services.ListFilesAsync("src/auth")).Single().Path == "src/auth/login.ts", "Listed paths are workspace-relative with '/'.");
        Console.WriteLine("PASS upstream #775: one directory spelling, healed records and '/' workspace-relative paths (Windows forms unverified)");

        using var git = new E2E.IsolatedGit(Path.Combine(root, "path-git"));
        var plain = Path.Combine(root, "path-plain");
        Directory.CreateDirectory(Path.Combine(plain, "inner"));
        var file = Path.Combine(plain, "file.txt");
        File.WriteAllText(file, "text");
        var repository = E2E.IsolatedGit.Repository(Path.Combine(root, "path-repo"));
        Directory.CreateDirectory(Path.Combine(repository, "sub"));
        (string Path, ProjectPathKind Kind)[] expected =
        [
            (plain, ProjectPathKind.Initable), (Path.Combine(plain, "inner"), ProjectPathKind.Initable), (file, ProjectPathKind.NotDirectory),
            (Path.Combine(plain, "absent"), ProjectPathKind.Missing), (repository, ProjectPathKind.Repository), (Path.Combine(repository, "sub"), ProjectPathKind.Repository)
        ];
        File.WriteAllText(Path.Combine(repository, "plain.txt"), "a\nb\n");
        File.WriteAllText(Path.Combine(repository, "ascii.pdf"), "%PDF-1.4\nline\nline\n");
        File.WriteAllBytes(Path.Combine(repository, "invalid.txt"), [0xFF, (byte)'\n', 0xFE, (byte)'\n']);
        var untracked = (await new ProjectServices(repository).GetGitAsync()).Changes.ToDictionary(change => change.Path, change => change.Added);
        Require(untracked["plain.txt"] == 2 && untracked["ascii.pdf"] == 0 && untracked["invalid.txt"] == 0, "Untracked line counts are for content the shared classification calls text.");
        foreach (var name in new[] { "plain.txt", "ascii.pdf", "invalid.txt" }) File.Delete(Path.Combine(repository, name));
        IProjectServices local = new LocalProjectAdapter(new ProjectServices(root));
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, "path-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "path-test");
            foreach (var (name, host) in new (string, IProjectServices)[] { ("local", local), ("remote", remote) })
            {
                foreach (var (path, kind) in expected)
                    Require(await host.InspectProjectPathAsync(path) == kind, $"The {name} host must classify {Path.GetFileName(path)} as {kind}.");
                Require(await host.InspectProjectPathAsync("~") is ProjectPathKind.Repository or ProjectPathKind.Initable, $"The {name} host must resolve ~ to its home.");
                foreach (var act in new Func<Task>[] { async () => await host.InspectProjectPathAsync("relative/path"), async () => await host.OpenProjectAsync("relative/path") })
                    try { await act(); throw new InvalidOperationException($"The {name} host accepted a relative project path."); }
                    catch (Exception error) when (error is ArgumentException or SharpRail.Host.Protocol.RpcException && error.Message.Contains("must be absolute or start with ~/", StringComparison.Ordinal)) { }
                try { await host.OpenProjectAsync("~/sharprail-absent-" + Guid.NewGuid().ToString("N")); throw new InvalidOperationException("A missing home folder opened."); }
                catch (Exception error) when (error is DirectoryNotFoundException or SharpRail.Host.Protocol.RpcException &&
                    error.Message.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal))
                { }
            }
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS project paths resolve ~, refuse relative input and are classified identically locally and over WebSocket RPC");
    }
}