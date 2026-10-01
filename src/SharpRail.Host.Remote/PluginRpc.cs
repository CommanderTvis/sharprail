using System.Text.Json;

using Grpc.Core;

using ProtoBuf.Grpc;

using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Protocol;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Remote;

public sealed class PluginRpc(PluginRuntime plugins, IHostApplicationLifetime lifetime) : IPluginRpc
{
    public async ValueTask<PluginRosterReply> ListAsync(PluginRosterRequest request, CallContext context = default) =>
        new() { Plugins = PluginWire.Map(await plugins.ListAsync(context.CancellationToken)) };

    public async ValueTask<PluginRosterReply> RescanAsync(PluginRosterRequest request, CallContext context = default) =>
        new() { Plugins = PluginWire.Map(await plugins.RescanAsync(context.CancellationToken)) };

    public async ValueTask<PluginRosterReply> RetryAsync(PluginRetryRequest request, CallContext context = default) =>
        new() { Plugins = PluginWire.Map(await plugins.RetryAsync(request.Id, context.CancellationToken)) };

    public async ValueTask<PluginCallReply> CallAsync(PluginCallMessage request, CallContext context = default)
    {
        JsonElement parameters;
        try { parameters = request.ParamsJson.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(request.ParamsJson); }
        catch (JsonException error) { throw Map(new PluginCallException(PluginCallError.InvalidParams, "Params are not JSON: " + error.Message)); }
        try
        {
            var result = await plugins.CallAsync(new(request.PluginId, request.Method,
                parameters.ValueKind == JsonValueKind.Undefined ? null : parameters, request.ClientKey), context.CancellationToken);
            return new() { ResultJson = PluginWire.Json(result) };
        }
        catch (PluginCallException error) { throw Map(error); }
    }

    public async IAsyncEnumerable<PluginPushMessage> SubscribeAsync(PluginSubscribeMessage request, CallContext context = default)
    {
        // Streams end with the host so a graceful shutdown does not wait for subscribers.
        using var call = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        object? key = request.KeyJson.Length == 0 ? null : JsonSerializer.Deserialize<JsonElement>(request.KeyJson);
        IAsyncEnumerable<object?> pushes;
        try { pushes = plugins.SubscribeAsync(new(request.PluginId, request.Channel, key, request.ClientKey), call.Token); }
        catch (PluginCallException error) { throw Map(error); }
        await using var reader = pushes.GetAsyncEnumerator(call.Token);
        while (true)
        {
            try { if (!await reader.MoveNextAsync()) yield break; }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { yield break; }
            yield return new() { PayloadJson = PluginWire.Json(reader.Current) };
        }
    }

    public async ValueTask<PluginFileReply> ReadFileAsync(PluginFileRequest request, CallContext context = default) =>
        await plugins.ReadFileAsync(request.PluginId, request.Path, context.CancellationToken) is { } data ? new() { Found = true, Data = data } : new();

    private static RpcException Map(PluginCallException error) => new(new Status(error.Error switch
    {
        PluginCallError.Unknown => StatusCode.NotFound,
        PluginCallError.Disabled => StatusCode.FailedPrecondition,
        PluginCallError.InvalidParams => StatusCode.InvalidArgument,
        _ => StatusCode.Unknown
    }, error.Message));
}

internal static class PluginWire
{
    internal static byte[] Json(object? value) => JsonSerializer.SerializeToUtf8Bytes(value, value?.GetType() ?? typeof(object), PluginJson.Options);

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    internal static List<PluginRosterMessage> Map(IEnumerable<PluginRosterEntry> roster) => roster.Select(entry => new PluginRosterMessage
    {
        Id = entry.Id,
        Label = entry.Label,
        Description = entry.Description ?? "",
        Icon = entry.Icon,
        Version = entry.Version,
        WireVersion = entry.WireVersion,
        Origin = Name(entry.Origin),
        Status = Name(entry.Status),
        Reason = entry.Reason ?? "",
        DependsOn = entry.DependsOn.ToList(),
        SideTools = entry.Contributes.SideTools.Select(tool => new PluginSideToolMessage
        {
            Tool = tool.Tool,
            Label = tool.Label,
            Icon = tool.Icon,
            DefaultSide = Name(tool.DefaultSide),
            RequiresGit = tool.RequiresGit
        }).ToList(),
        FileViewers = entry.Contributes.FileViewers.Select(viewer => new PluginFileViewerMessage
        {
            Extensions = viewer.Extensions.ToList(),
            Names = viewer.Names.ToList(),
            Read = Name(viewer.Read)
        }).ToList(),
        Channels = entry.Channels.Select(channel => new PluginChannelMessage
        {
            Name = channel.Key,
            Kind = Name(channel.Value.Kind),
            Snapshot = channel.Value.Snapshot ?? "",
            Key = channel.Value.Key.ToList()
        }).ToList(),
        Ui = entry.Ui ?? "",
        Assets = entry.Assets ?? ""
    }).ToList();
}