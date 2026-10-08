using Grpc.Core;
using Grpc.Core.Interceptors;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

/// <summary>Shapes every unary call of a remote adapter the way the local host behaves.</summary>
internal sealed class HostCallInterceptor : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(request, context);
        return new(Named(call.ResponseAsync), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
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