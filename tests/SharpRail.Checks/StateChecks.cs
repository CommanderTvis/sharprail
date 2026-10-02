using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

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
            HostStateChange.SavePreset("Mine", "{}"), HostStateChange.RenamePreset("Mine", "Renamed"),
            HostStateChange.OpenProject(project), HostStateChange.OpenProject(other), HostStateChange.CloseProject(other),
            HostStateChange.Label(Path.Combine(project + "-worktrees", "workspace-1"), "Shared name"),
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
            watch.Cancel();
        }
        var expected = Describe(await local.GetStateAsync());
        Require(expected == Describe(await remote.GetStateAsync()), "Local and remote hosts must apply the same changes identically.");
        var state = await local.GetStateAsync();
        Require(state.Settings is { Theme: "light", ThemeMode: "system", SystemDark: "high-contrast-dark", MarkdownLineWidth: 80, FileLineWidthBounded: false } &&
            state.Presets.Single().Name == "Renamed" && state.Projects.SequenceEqual([project]) && state.RecentProjects.SequenceEqual([other]),
            "Changes apply in order: presets rename, closing moves a project to the recents.");
        Require(state.PluginSettings["probe"].GetRawText() == """{"size":1,"enabled":false}""" && state.PluginSettings["other"].GetRawText() == """{"kept":true}""" &&
            state.PluginPaths.Single().EndsWith("state-plugins", StringComparison.Ordinal) && state.Platform is not null,
            "Plugin namespaces merge member by member, roots replace, and the host names its platform.");
        Require(Describe(new HostStateStore(localDirectory).Current) == expected && Describe(new HostStateStore(remoteDirectory).Current) == expected,
            "Both hosts persist their state beside themselves.");

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
        Console.WriteLine("PASS host state changes, broadcasts, validation and persistence match locally and over gRPC");
    }

    // Any known project's workspaces, from any session, identically over gRPC; never an arbitrary folder.
    private static async Task WorkspaceListing(string root)
    {
        var repository = E2E.IsolatedGit.Repository(Path.Combine(root, "listing-repo"));
        var linked = Path.Combine(root, "listing-repo-worktrees", "feature");
        E2E.IsolatedGit.Run(repository, "worktree", "add", "-b", "feature", linked);
        var plain = Path.Combine(root, "listing-plain");
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
            var trees = await service.ListWorkspacesAsync(repository);
            Require(trees.Count == 2 && trees[0] is { IsMain: true, Branch: "main" } && trees[0].Path == repository && trees[1].Path == linked && trees[1].Branch == "feature",
                $"A {label} session lists another project's worktrees, main first.");
            Require((await service.ListWorkspacesAsync(plain)).SequenceEqual([new WorktreeInfo(plain, "", true, false)]), $"A {label} plain folder is its own workspace.");
            try { await service.ListWorkspacesAsync(stranger); throw new InvalidOperationException($"A {label} host listed a folder that is not its project."); }
            catch (Exception error) when (error is UnauthorizedAccessException or Grpc.Core.RpcException) { }
        }
    }
}