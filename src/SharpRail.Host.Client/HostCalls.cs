using Grpc.Core;
using Grpc.Core.Interceptors;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

/// <summary>
/// One client's identity towards a remote host, shared by the adapters of that client. The identity spans
/// reconnects, so the host recognises a mutation sent again after its connection died.
/// </summary>
public sealed class HostConnection
{
    private readonly Lock gate = new();
    private readonly SortedSet<long> unresolved = [];
    private long lastRequest;
    private int? hostVersion;

    public string ClientId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The host's protocol version from its latest handshake; null until one answers. It decides what the host can serve.</summary>
    public int? HostVersion
    {
        get { lock (gate) return hostVersion; }
        set { lock (gate) hostVersion = value; }
    }

    internal long Begin()
    {
        lock (gate) { unresolved.Add(++lastRequest); return lastRequest; }
    }

    internal void End(long request)
    {
        lock (gate) unresolved.Remove(request);
    }

    /// <summary>Every request still awaiting its reply, which lets the host drop the results of all others.</summary>
    internal string Unresolved()
    {
        lock (gate) return string.Join(',', unresolved);
    }
}

/// <summary>Shapes every unary call of a remote adapter the way the local host behaves.</summary>
internal sealed class HostCallInterceptor(HostConnection connection) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (ReplayHeaders.IsReplayable(context.Method.Name))
            return new(Named(Replayed(request, context, continuation)), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
        var call = continuation(request, context);
        return new(Named(call.ResponseAsync), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
    }

    /// <summary>
    /// Sends a mutation under one request id until a reply is read. A connection lost in flight says nothing
    /// about whether the host ran it, so it is sent again and the host answers with the first run's outcome.
    /// </summary>
    private async Task<TResponse> Replayed<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation) where TRequest : class where TResponse : class
    {
        var deduplicated = HostCapabilities.Supports(connection.HostVersion, HostProtocol.RequestReplay);
        var options = context.Options;
        var id = connection.Begin();
        try
        {
            for (var delay = StateAdapterDefaults.InitialReconnect; ; delay = delay * 2 < StateAdapterDefaults.MaxReconnect ? delay * 2 : StateAdapterDefaults.MaxReconnect)
            {
                var headers = new Metadata();
                foreach (var entry in options.Headers ?? []) headers.Add(entry);
                headers.Add(ReplayHeaders.Client, connection.ClientId);
                headers.Add(ReplayHeaders.Request, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                headers.Add(ReplayHeaders.Resume, connection.Unresolved());
                using var call = continuation(request, new(context.Method, context.Host, options.WithHeaders(headers)));
                try { return await call.ResponseAsync; }
                catch (RpcException error) when (deduplicated && error.StatusCode == StatusCode.Unavailable &&
                    !options.CancellationToken.IsCancellationRequested && (options.Deadline is not { } deadline || DateTime.UtcNow + delay < deadline))
                {
                    await Task.Delay(delay, options.CancellationToken);
                }
            }
        }
        finally { connection.End(id); }
    }

    /// <summary>A failure the host named arrives as the exception the local host throws.</summary>
    private static async Task<T> Named<T>(Task<T> response)
    {
        try { return await response; }
        catch (RpcException error) when (error.Trailers.GetValue(HostHeaders.ErrorCode) is { } name && Enum.TryParse<HostErrorCode>(name, out var code))
        {
            throw new HostException(code, error.Status.Detail);
        }
    }
}