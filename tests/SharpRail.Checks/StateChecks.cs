using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Host state parity: the same changes through direct and gRPC adapters yield the same snapshots, broadcasts and files.</summary>
internal static class StateChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Describe(HostState state) => string.Join("|",
        state.Settings.ToString(),
        string.Join(",", state.Presets.Select(preset => preset.Name + "=" + preset.Layout)),
        string.Join(",", state.Projects), string.Join(",", state.RecentProjects),
        string.Join(",", state.WorkspaceLabels.OrderBy(entry => entry.Key).Select(entry => entry.Key + "=" + entry.Value)),
        string.Join(",", state.WorkspaceBases.OrderBy(entry => entry.Key).Select(entry => entry.Key + "=" + entry.Value)),
        string.Join(",", state.WorkspaceDiffBases.OrderBy(entry => entry.Key).Select(entry => entry.Key + "=" + entry.Value)),
        string.Join(",", state.PluginSettings.OrderBy(entry => entry.Key).Select(entry => entry.Key + "=" + entry.Value.GetRawText())),
        string.Join(",", state.PluginPaths), state.Platform.ToString());

    internal static async Task Run(string root)
    {
        var project = Path.Combine(root, "state-project");
        var other = Path.Combine(root, "state-other");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(other);
        HostStateChange[] changes =
        [
            HostStateChange.Setting("theme", "light"), HostStateChange.Setting("theme-mode", "system"),
            HostStateChange.Setting("system-light", "light"), HostStateChange.Setting("system-dark", "high-contrast-dark"),
            HostStateChange.Setting("markdown-width", "80"), HostStateChange.Setting("file-bounded", "false"),
            HostStateChange.Setting("notifications", "false"),
            HostStateChange.Setting("custom-highlighting", """[{"Name":"Example","Scope":"source.example","Patterns":["*.example"],"Grammar":"{\"scopeName\":\"source.example\",\"patterns\":[]}"}]"""),
            HostStateChange.SavePreset("Mine", "{}"), HostStateChange.RenamePreset("Mine", "Renamed"),
            HostStateChange.OpenProject(project), HostStateChange.OpenProject(other), HostStateChange.CloseProject(other),
            HostStateChange.Label(Path.Combine(project + "-worktrees", "workspace-1"), "Shared name"),
            HostStateChange.DiffBase(Path.Combine(project + "-worktrees", "workspace-1"), "origin/release"),
            HostStateChange.PluginSettings("probe", """{"size":1}"""), HostStateChange.PluginSettings("other", """{"kept":true}"""),
            HostStateChange.PluginSettings("probe", """{"enabled":false}"""), HostStateChange.PluginPaths([Path.Combine(root, "state-plugins")])
        ];

        var localDirectory = Path.Combine(root, "state-local");
        var local = new LocalStateAdapter(new HostStateStore(localDirectory));
        var remoteDirectory = Path.Combine(root, "state-remote");
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, "state-test", remoteDirectory);
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var remote = new RemoteStateAdapter(new Uri(address), "state-test");

        foreach (var (name, service) in new (string, IHostStateService)[] { ("local", local), ("remote", remote) })
        {
            using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var stream = service.WatchAsync(watch.Token).GetAsyncEnumerator(watch.Token);
            Require(await stream.MoveNextAsync() && stream.Current.Projects.Count == 0, $"A {name} watcher first receives the current snapshot.");
            var changed = await service.ChangeAsync(changes);
            Require(await stream.MoveNextAsync() && Describe(stream.Current) == Describe(changed) && stream.Current.Revision == changed.Revision,
                $"A {name} watcher receives one snapshot per atomic change.");
            try
            {
                await service.ChangeAsync([HostStateChange.Setting("file-width", "39")]);
                throw new InvalidOperationException($"The {name} host accepted an invalid width.");
            }
            catch (Exception error) when (error is ArgumentException or Grpc.Core.RpcException) { }
            try
            {
                await service.ChangeAsync([HostStateChange.DiffBase(project, "--output=x")]);
                throw new InvalidOperationException($"The {name} host stored an option-shaped review target.");
            }
            catch (Exception error) when (error is ArgumentException or Grpc.Core.RpcException) { }
            watch.Cancel();
            try
            {
                await service.ChangeAsync([HostStateChange.Setting("custom-highlighting", """[{"Scope":"broken"}]""")]);
                throw new InvalidOperationException($"The {name} host accepted malformed custom highlighting.");
            }
            catch (Exception error) when (error is ArgumentException or Grpc.Core.RpcException) { }
        }
        var expected = Describe(await local.GetStateAsync());
        Require(expected == Describe(await remote.GetStateAsync()), "Local and remote hosts must apply the same changes identically.");
        var state = await local.GetStateAsync();
        Require(state.Settings.CustomHighlighting.Contains("source.example", StringComparison.Ordinal), "Custom highlighting did not persist through the host.");
        Require(state.Settings is { Theme: "light", ThemeMode: "system", SystemDark: "high-contrast-dark", MarkdownLineWidth: 80, FileLineWidthBounded: false, NotificationsEnabled: false } &&
            state.Presets.Single().Name == "Renamed" && state.Projects.SequenceEqual([project]) && state.RecentProjects.SequenceEqual([other]),
            "Changes apply in order: presets rename, closing moves a project to the recents.");
        Require(state.PluginSettings["probe"].GetRawText() == """{"size":1,"enabled":false}""" && state.PluginSettings["other"].GetRawText() == """{"kept":true}""" &&
            state.PluginPaths.Single().EndsWith("state-plugins", StringComparison.Ordinal) && state.Platform is not null,
            "Plugin namespaces merge member by member, roots replace, and the host names its platform.");
        Require(Describe(new HostStateStore(localDirectory).Current) == expected && Describe(new HostStateStore(remoteDirectory).Current) == expected,
            "Both hosts persist their state beside themselves.");
        var remoteRecords = (await remote.GetStateAsync()).ProjectRecords;
        Require(state.ProjectRecords.Select(record => (record.Path, record.Slug)).Order().SequenceEqual(remoteRecords.Select(record => (record.Path, record.Slug)).Order()) &&
            state.ProjectRecords.Count == 2 && remoteRecords.All(record => record.Id.Length > 0 && record.LastOpened > 0),
            "Project records must reach remote clients with their identity.");
        var localShake = await local.GetHandshakeAsync();
        var remoteShake = await remote.GetHandshakeAsync();
        Require(localShake == remoteShake && localShake.ProtocolVersion == HostProtocol.Current, "Local and remote handshakes must match.");
        using var wrong = new RemoteStateAdapter(new Uri(address), "wrong-token");
        try { await wrong.GetHandshakeAsync(); throw new InvalidOperationException("A wrong token must not learn the protocol version."); }
        catch (Grpc.Core.RpcException error) when (error.StatusCode is Grpc.Core.StatusCode.Unauthenticated or Grpc.Core.StatusCode.PermissionDenied) { }
        Require(new HostHandshake(0, "").Supports(0) && !new HostHandshake(0, "").Supports(1) && new HostHandshake(2, "").Supports(2) && new HostHandshake(3, "").Supports(2),
            "A handshake supports features introduced at or below its version.");
        Require(!HostCapabilities.Supports(null, 0) && !HostCapabilities.Supports(0, 1) && HostCapabilities.Supports(1, 1) && HostCapabilities.Supports(2, 1),
            "No handshake or a lower version is unsupported; equal or greater is supported.");
        await using (var oldHost = await OldHost.StartAsync())
        {
            using var legacy = new RemoteStateAdapter(new Uri(oldHost.Address), "state-test");
            var shake = await legacy.GetHandshakeAsync();
            Require(shake.ProtocolVersion == 0 && !HostCapabilities.Supports(shake.ProtocolVersion, 1), "A host without the handshake reports version 0.");
        }
        Console.WriteLine("PASS host handshake matches locally and remotely, rejects bad tokens and maps an older host to version 0");

        await WorkspaceListing(root);

        // Recents hide folders that no longer exist or became files, without forgetting them.
        var recents = new HostStateStore(Path.Combine(root, "state-recents"));
        var gone = Path.Combine(root, "state-gone");
        var replaced = Path.Combine(root, "state-replaced");
        Directory.CreateDirectory(gone);
        Directory.CreateDirectory(replaced);
        await recents.ChangeAsync([.. new[] { gone, replaced, other }.SelectMany(path => new[] { HostStateChange.OpenProject(path), HostStateChange.CloseProject(path) })]);
        Require(recents.Current.RecentProjects.SequenceEqual([other, replaced, gone]), "Closed projects are recent, newest first.");
        Directory.Delete(gone);
        Directory.Delete(replaced);
        File.WriteAllText(replaced, "");
        Require(recents.Current.RecentProjects.SequenceEqual([other]), "Recents stop listing a folder that is gone or became a file.");
        Directory.CreateDirectory(gone);
        Require(recents.Current.RecentProjects.SequenceEqual([other, gone]), "A restored folder returns to recents in its place.");

        // Sessions end with the app; a persisted agent record whose workspace folder is gone can never resume, so a load drops it.
        var agentsDirectory = Path.Combine(root, "state-agents");
        var living = Directory.CreateDirectory(Path.Combine(root, "state-agents-living")).FullName;
        var vanished = Directory.CreateDirectory(Path.Combine(root, "state-agents-vanished")).FullName;
        var agents = new HostStateStore(agentsDirectory);
        agents.SetTerminalAgent(new(living, "tab"), new("claude", "claude"));
        agents.SetTerminalAgent(new(vanished, "tab"), new("claude", "claude"));
        Directory.Delete(vanished);
        Require(new HostStateStore(agentsDirectory).Current.TerminalAgents.Select(agent => agent.Terminal.WorkspaceId).SequenceEqual([living]),
            "Loading state forgets agent records of workspaces whose folder is gone.");
        Console.WriteLine("PASS host state changes, broadcasts, validation and persistence match locally and over gRPC");
    }

    /// <summary>A gRPC host that serves no state methods, like a build that predates the handshake.</summary>
    private sealed class OldHost : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication app;
        private OldHost(Microsoft.AspNetCore.Builder.WebApplication app, string address) { this.app = app; Address = address; }
        internal string Address { get; }

        internal static async Task<OldHost> StartAsync()
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Use((Microsoft.AspNetCore.Http.HttpContext context, Func<Task> _) => { context.Response.Headers["grpc-status"] = "12"; context.Response.ContentType = "application/grpc"; return Task.CompletedTask; });
            await app.StartAsync();
            return new(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    // Any known project's workspaces, from any session, identically over gRPC; never an arbitrary folder.
    private static async Task WorkspaceListing(string root)
    {
        var repository = E2E.IsolatedGit.Repository(Path.Combine(root, "listing-repo"));
        var linked = Path.Combine(root, "listing-repo-worktrees", "feature");
        E2E.IsolatedGit.Run(repository, "worktree", "add", "-b", "feature", linked);
        var plain = Path.Combine(root, "listing-plain") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(plain);
        var stranger = Path.Combine(root, "listing-stranger");
        Directory.CreateDirectory(stranger);
        var directory = Path.Combine(root, "listing-state");
        await using var server = RemoteServer.Create(plain, IPAddress.Loopback, 0, "listing-test", directory);
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var state = new RemoteStateAdapter(new Uri(address), "listing-test");
        await state.ChangeAsync([HostStateChange.OpenProject(repository), HostStateChange.OpenProject(plain)]);
        var store = new HostStateStore(directory);
        IProjectServices local = new LocalProjectAdapter(new ProjectServices(plain, store));
        using var remote = new RemoteProjectAdapter(new Uri(address), "listing-test");
        await remote.OpenProjectAsync(plain);
        foreach (var (service, label) in new[] { (local, "local"), ((IProjectServices)remote, "remote") })
        {
            var trees = (await service.ListWorkspacesAsync(repository)).Workspaces;
            Require(trees.Count == 2 && trees[0] is { Kind: WorkspaceKinds.Default, Branch: "main" } && trees[0].Path == repository && trees[1].Path == linked && trees[1].Branch == "feature",
                $"A {label} session lists another project's worktrees, main first.");
            var workspace = await service.OpenProjectAsync(plain);
            Require((await service.ListWorkspacesAsync(plain)).Workspaces is [{ Kind: WorkspaceKinds.Default, Branch: "" } plainWorkspace] && plainWorkspace.Path == workspace.RootPath,
                $"A {label} plain folder with a trailing separator lists its mounted workspace identity.");
            var withoutSeparator = Path.TrimEndingDirectorySeparator(plain);
            Require((await service.ListWorkspacesAsync(withoutSeparator)).Workspaces.Single().Path == withoutSeparator,
                $"A {label} known folder also authorizes the same path without its trailing separator.");
            try { await service.ListWorkspacesAsync(stranger); throw new InvalidOperationException($"A {label} host listed a folder that is not its project."); }
            catch (Exception error) when (error is UnauthorizedAccessException or Grpc.Core.RpcException) { }
        }
        Directory.Delete(linked, recursive: true);
        foreach (var service in new[] { local, remote })
        {
            Require((await service.ListWorkspacesAsync(repository)).Workspaces.All(tree => tree.Path != linked), "A deleted checkout must not remain in the host workspace catalog.");
            await service.OpenProjectAsync(repository);
            Require((await service.GetGitAsync()).Worktrees.All(tree => tree.Path != linked), "Git snapshots must omit deleted checkout directories too.");
        }
    }
}