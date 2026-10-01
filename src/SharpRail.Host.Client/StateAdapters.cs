using System.Runtime.CompilerServices;
using System.Text.Json;

using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Client;

/// <summary>Interactive clients retry a dropped host quickly instead of gRPC's default two-minute backoff ceiling.</summary>
internal static class StateAdapterDefaults
{
    internal static readonly TimeSpan InitialReconnect = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaxReconnect = TimeSpan.FromSeconds(3);
}

/// <summary>Feature gates: a feature is served only when a handshake arrived and its version reaches the feature's.</summary>
public static class HostCapabilities
{
    /// <summary>Null means no handshake yet, which is unsupported.</summary>
    public static bool Supports(int? hostVersion, int introducedAt) => hostVersion is { } version && version >= introducedAt;
}

public sealed class LocalStateAdapter(IHostStateService host) : IHostStateService
{
    public ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default) => host.GetHandshakeAsync(cancellationToken);
    public ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default) => host.GetStateAsync(cancellationToken);
    public ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default) => host.ChangeAsync(changes, cancellationToken);
    public IAsyncEnumerable<HostState> WatchAsync(CancellationToken cancellationToken = default) => host.WatchAsync(cancellationToken);
    public IAsyncEnumerable<LifecycleEvent> WatchLifecycleAsync(CancellationToken cancellationToken = default) => host.WatchLifecycleAsync(cancellationToken);
}

public sealed class RemoteStateAdapter : IHostStateService, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly IStateRpc service;
    private readonly string token;
    private readonly HostConnection connection;

    /// <summary>Adapters of one client share <paramref name="connection"/>, which learns the host's version from each handshake.</summary>
    public RemoteStateAdapter(Uri address, string token, HostConnection? connection = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token; this.connection = connection ?? new();
        channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            InitialReconnectBackoff = StateAdapterDefaults.InitialReconnect,
            MaxReconnectBackoff = StateAdapterDefaults.MaxReconnect
        });
        service = channel.Intercept(new HostCallInterceptor(this.connection)).CreateGrpcService<IStateRpc>();
    }

    private CallContext Context(CancellationToken ct, bool stream = false) => new(new CallOptions(
        headers: new Metadata { { "authorization", $"Bearer {token}" } },
        deadline: stream ? null : DateTime.UtcNow.AddSeconds(60), cancellationToken: ct));

    /// <summary>A host that does not implement the handshake predates it and reports version 0.</summary>
    public async ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await service.HandshakeAsync(new() { ClientProtocolVersion = HostProtocol.Current }, Context(cancellationToken));
            connection.HostVersion = reply.ProtocolVersion;
            return new(reply.ProtocolVersion, reply.HostVersion);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { connection.HostVersion = 0; return new(0, ""); }
    }

    public async ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default)
        => Map(await service.GetStateAsync(new(), Context(cancellationToken)));

    public async ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default)
        => Map(await service.ChangeAsync(new()
        {
            Changes = changes.Select(change => new StateChangeMessage { Kind = change.Kind, Key = change.Key, Value = change.Value }).ToList()
        }, Context(cancellationToken)));

    /// <summary>Ends with an exception when the transport drops; the caller resubscribes and receives a fresh snapshot.</summary>
    public async IAsyncEnumerable<HostState> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var reply in service.WatchAsync(new(), Context(cancellationToken, stream: true)).WithCancellation(cancellationToken))
            yield return Map(reply);
    }

    public async IAsyncEnumerable<LifecycleEvent> WatchLifecycleAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in service.WatchLifecycleAsync(new(), Context(cancellationToken, stream: true)).WithCancellation(cancellationToken))
            yield return new(message.Channel, message.Kind, message.ProjectRoot, message.WorkspaceId,
                message.Workspace is { } workspace ? WorkspaceMessages.Map(workspace) : null);
    }

    private static HostState Map(StateReply reply) => new()
    {
        Revision = reply.Revision,
        Settings = new()
        {
            Theme = reply.Settings.Theme,
            ThemeMode = reply.Settings.ThemeMode,
            SystemLight = reply.Settings.SystemLight,
            SystemDark = reply.Settings.SystemDark,
            FileLineWidth = reply.Settings.FileLineWidth,
            FileLineWidthBounded = !reply.Settings.FileLineWidthUnbounded,
            MarkdownLineWidth = reply.Settings.MarkdownLineWidth,
            MarkdownLineWidthBounded = !reply.Settings.MarkdownLineWidthUnbounded,
            TerminalReplayKb = reply.Settings.TerminalReplayKb
        },
        Presets = reply.Presets.Select(preset => new LayoutPreset(preset.Name, preset.Layout)).ToArray(),
        Projects = reply.Projects.ToArray(),
        RecentProjects = reply.RecentProjects.ToArray(),
        ProjectRecords = reply.ProjectRecords.Select(record => new ProjectRecord(record.Id, record.Path, record.Slug, record.LastOpened)).ToArray(),
        WorkspaceLabels = reply.Labels.ToDictionary(label => label.Path, label => label.Label),
        WorkspaceBases = reply.Bases.ToDictionary(entry => entry.Path, entry => entry.Reference),
        WorkspaceDiffBases = reply.DiffBases.ToDictionary(entry => entry.Path, entry => entry.Reference),
        Workspaces = reply.Workspaces.Select(WorkspaceMessages.Map).ToArray(),
        PluginSettings = reply.PluginSettings.ToDictionary(space => space.Id, space => JsonSerializer.Deserialize<JsonElement>(space.Json)),
        PluginPaths = reply.PluginPaths.ToArray(),
        Plugins = PluginWire.Map(reply.Plugins),
        TerminalAgents = reply.TerminalAgents.Select(agent => new TerminalAgent(new(agent.WorkspaceId, agent.TabKey), new(agent.Kind, agent.Command)
        {
            SessionId = agent.SessionId.Length == 0 ? null : agent.SessionId,
            Cwd = agent.Cwd.Length == 0 ? null : agent.Cwd,
            Model = agent.Model.Length == 0 ? null : agent.Model
        })).ToArray(),
        Platform = Enum.TryParse<HostPlatform>(reply.Platform, ignoreCase: true, out var platform) ? platform : null
    };

    public void Dispose() => channel.Dispose();
}