using System.Runtime.CompilerServices;

using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

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
}

public sealed class RemoteStateAdapter : IHostStateService, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly IStateRpc service;
    private readonly string token;

    public RemoteStateAdapter(Uri address, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token;
        channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            InitialReconnectBackoff = StateAdapterDefaults.InitialReconnect,
            MaxReconnectBackoff = StateAdapterDefaults.MaxReconnect
        });
        service = channel.Intercept(new HostCallInterceptor()).CreateGrpcService<IStateRpc>();
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
            return new(reply.ProtocolVersion, reply.HostVersion);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { return new(0, ""); }
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
            MarkdownLineWidthBounded = !reply.Settings.MarkdownLineWidthUnbounded
        },
        Presets = reply.Presets.Select(preset => new LayoutPreset(preset.Name, preset.Layout)).ToArray(),
        Projects = reply.Projects.ToArray(),
        RecentProjects = reply.RecentProjects.ToArray(),
        WorkspaceLabels = reply.Labels.ToDictionary(label => label.Path, label => label.Label),
        Workspaces = reply.Workspaces.ToDictionary(list => list.ProjectRoot, list => (IReadOnlyList<string>)list.Paths.ToArray())
    };

    public void Dispose() => channel.Dispose();
}