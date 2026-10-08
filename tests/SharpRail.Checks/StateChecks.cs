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
        string.Join(",", state.WorkspaceDiffBases.OrderBy(entry => entry.Key).Select(entry => entry.Key + "=" + entry.Value)));

    internal static async Task Run(string root)
    {
        var project = Path.Combine(root, "state-project");
        var other = Path.Combine(root, "state-other");
        HostStateChange[] changes =
        [
            HostStateChange.Setting("theme", "light"), HostStateChange.Setting("theme-mode", "system"),
            HostStateChange.Setting("system-light", "light"), HostStateChange.Setting("system-dark", "high-contrast-dark"),
            HostStateChange.Setting("markdown-width", "80"), HostStateChange.Setting("file-bounded", "false"),
            HostStateChange.SavePreset("Mine", "{}"), HostStateChange.RenamePreset("Mine", "Renamed"),
            HostStateChange.OpenProject(project), HostStateChange.OpenProject(other), HostStateChange.CloseProject(other),
            HostStateChange.Label(Path.Combine(project + "-worktrees", "workspace-1"), "Shared name"),
            HostStateChange.DiffBase(Path.Combine(project + "-worktrees", "workspace-1"), "origin/release")
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
        }
        var expected = Describe(await local.GetStateAsync());
        Require(expected == Describe(await remote.GetStateAsync()), "Local and remote hosts must apply the same changes identically.");
        var state = await local.GetStateAsync();
        Require(state.Settings is { Theme: "light", ThemeMode: "system", SystemDark: "high-contrast-dark", MarkdownLineWidth: 80, FileLineWidthBounded: false } &&
            state.Presets.Single().Name == "Renamed" && state.Projects.SequenceEqual([project]) && state.RecentProjects.SequenceEqual([other]),
            "Changes apply in order: presets rename, closing moves a project to the recents.");
        Require(Describe(new HostStateStore(localDirectory).Current) == expected && Describe(new HostStateStore(remoteDirectory).Current) == expected,
            "Both hosts persist their state beside themselves.");
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
}