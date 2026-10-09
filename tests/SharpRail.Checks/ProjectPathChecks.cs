using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Project path resolution and inspection, equal through the embedded host and a real gRPC host.</summary>
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
                    catch (Exception error) when (error is ArgumentException or Grpc.Core.RpcException && error.Message.Contains("must be absolute or start with ~/", StringComparison.Ordinal)) { }
                try { await host.OpenProjectAsync("~/sharprail-absent-" + Guid.NewGuid().ToString("N")); throw new InvalidOperationException("A missing home folder opened."); }
                catch (Exception error) when (error is DirectoryNotFoundException or Grpc.Core.RpcException &&
                    error.Message.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal))
                { }
            }
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS project paths resolve ~, refuse relative input and are classified identically locally and over gRPC");
    }
}