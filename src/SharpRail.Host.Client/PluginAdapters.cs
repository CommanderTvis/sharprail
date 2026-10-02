using System.Runtime.CompilerServices;
using System.Text.Json;

using Grpc.Core;
using Grpc.Net.Client;

using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Client;

/// <summary>The app's own host's plugin runtime, called directly: params, results and payloads stay objects.</summary>
public sealed class LocalPluginAdapter(IPluginService host) : IPluginService
{
    public ValueTask<IReadOnlyList<PluginRosterEntry>> ListAsync(CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListAsync(cancellationToken));
    public ValueTask<IReadOnlyList<PluginRosterEntry>> RescanAsync(CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.RescanAsync(cancellationToken));
    public ValueTask<IReadOnlyList<PluginRosterEntry>> RetryAsync(string id, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.RetryAsync(id, cancellationToken));
    public ValueTask<object?> CallAsync(PluginCallRequest request, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.CallAsync(request, cancellationToken));
    public IAsyncEnumerable<object?> SubscribeAsync(PluginSubscription subscription, CancellationToken cancellationToken = default) => host.SubscribeAsync(subscription, cancellationToken);
    public ValueTask<byte[]?> ReadFileAsync(string id, string path, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ReadFileAsync(id, path, cancellationToken));
}

/// <summary>
/// A remote host's plugin runtime over gRPC. Values cross as JSON written with <see cref="PluginJson.Options"/>
/// and arrive as <see cref="JsonElement"/>s; a failed call is the same <see cref="PluginCallException"/> as locally.
/// </summary>
public sealed class RemotePluginAdapter : IPluginService, IDisposable
{
    // An external UI half and its sibling assemblies are read through the host in one message each.
    private const int FileMessageBytes = 256 * 1024 * 1024;
    private readonly GrpcChannel channel;
    private readonly IPluginRpc service;
    private readonly string token;

    public RemotePluginAdapter(Uri address, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token;
        channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = FileMessageBytes,
            InitialReconnectBackoff = StateAdapterDefaults.InitialReconnect,
            MaxReconnectBackoff = StateAdapterDefaults.MaxReconnect
        });
        service = channel.CreateGrpcService<IPluginRpc>();
    }

    private CallContext Context(CancellationToken ct, bool stream = false) => new(new CallOptions(
        headers: new Metadata { { "authorization", $"Bearer {token}" } },
        deadline: stream ? null : DateTime.UtcNow.AddSeconds(60), cancellationToken: ct));

    public async ValueTask<IReadOnlyList<PluginRosterEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        PluginWire.Map((await service.ListAsync(new(), Context(cancellationToken))).Plugins);

    public async ValueTask<IReadOnlyList<PluginRosterEntry>> RescanAsync(CancellationToken cancellationToken = default) =>
        PluginWire.Map((await service.RescanAsync(new(), Context(cancellationToken))).Plugins);

    public async ValueTask<IReadOnlyList<PluginRosterEntry>> RetryAsync(string id, CancellationToken cancellationToken = default) =>
        PluginWire.Map((await service.RetryAsync(new() { Id = id }, Context(cancellationToken))).Plugins);

    public async ValueTask<object?> CallAsync(PluginCallRequest request, CancellationToken cancellationToken = default)
    {
        PluginCallReply reply;
        try
        {
            reply = await service.CallAsync(new()
            {
                PluginId = request.PluginId,
                Method = request.Method,
                ParamsJson = PluginWire.Json(request.Params),
                ClientKey = request.ClientKey
            }, Context(cancellationToken));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RpcException error) when (PluginWire.Error(error) is { } mapped) { throw mapped; }
        return PluginWire.Value(reply.ResultJson);
    }

    /// <summary>
    /// Ends with an exception when the transport drops; the caller resubscribes and re-reads the snapshot. Cancelling
    /// ends it with <see cref="OperationCanceledException"/>, as locally.
    /// </summary>
    public async IAsyncEnumerable<object?> SubscribeAsync(PluginSubscription subscription, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pushes = service.SubscribeAsync(new()
        {
            PluginId = subscription.PluginId,
            Channel = subscription.Channel,
            KeyJson = subscription.Key is null ? [] : PluginWire.Json(subscription.Key),
            ClientKey = subscription.ClientKey
        }, Context(cancellationToken, stream: true));
        await using var reader = pushes.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            try { if (!await reader.MoveNextAsync()) yield break; }
            catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (RpcException error) when (PluginWire.Error(error) is { } mapped) { throw mapped; }
            yield return PluginWire.Value(reader.Current.PayloadJson);
        }
    }

    public async ValueTask<byte[]?> ReadFileAsync(string id, string path, CancellationToken cancellationToken = default)
    {
        var reply = await service.ReadFileAsync(new() { PluginId = id, Path = path }, Context(cancellationToken));
        return reply.Found ? reply.Data : null;
    }

    public void Dispose() => channel.Dispose();
}

internal static class PluginWire
{
    internal static byte[] Json(object? value) => JsonSerializer.SerializeToUtf8Bytes(value, value?.GetType() ?? typeof(object), PluginJson.Options);

    internal static object? Value(byte[] json)
    {
        if (json.Length == 0) return null;
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        return element.ValueKind == JsonValueKind.Null ? null : element;
    }

    // Only the codes the plugin call maps; transport failures stay RpcExceptions.
    internal static PluginCallException? Error(RpcException error) => error.StatusCode switch
    {
        StatusCode.NotFound => new(PluginCallError.Unknown, error.Status.Detail),
        StatusCode.FailedPrecondition => new(PluginCallError.Disabled, error.Status.Detail),
        StatusCode.InvalidArgument => new(PluginCallError.InvalidParams, error.Status.Detail),
        StatusCode.Unknown => new(PluginCallError.Failed, error.Status.Detail),
        _ => null
    };

    private static T Parse<T>(string value, T fallback) where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string? Optional(string value) => value.Length == 0 ? null : value;

    internal static IReadOnlyList<PluginRosterEntry> Map(IEnumerable<PluginRosterMessage> roster) => roster.Select(entry =>
        new PluginRosterEntry(entry.Id, entry.Label, entry.Icon, entry.Version, entry.WireVersion,
            Parse(entry.Origin, PluginOrigin.External), Parse(entry.Status, PluginStatus.Refused))
        {
            Description = Optional(entry.Description),
            Reason = Optional(entry.Reason),
            DependsOn = entry.DependsOn.ToArray(),
            Contributes = new()
            {
                SideTools = entry.SideTools.Select(tool => new PluginSideToolContribution(tool.Tool, tool.Label, tool.Icon, Parse(tool.DefaultSide, PluginToolSide.Left))
                {
                    RequiresGit = tool.RequiresGit
                }).ToArray(),
                FileViewers = entry.FileViewers.Select(viewer => new PluginFileViewerContribution
                {
                    Extensions = viewer.Extensions.ToArray(),
                    Names = viewer.Names.ToArray(),
                    Read = Parse(viewer.Read, PluginFileRead.Text)
                }).ToArray()
            },
            Channels = entry.Channels.ToDictionary(channel => channel.Name,
                channel => new PluginRosterChannel(Parse(channel.Kind, PluginChannelKind.Event), Optional(channel.Snapshot), channel.Key.ToArray())),
            Ui = Optional(entry.Ui),
            Assets = Optional(entry.Assets)
        }).ToArray();
}