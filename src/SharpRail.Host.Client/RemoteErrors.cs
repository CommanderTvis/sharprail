using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

/// <summary>Transport failure classification for callers that do not consume wire contracts.</summary>
public static class RemoteErrors
{
    public static bool IsTransport(Exception error) => error is RpcException;
    public static bool IsUnauthenticated(Exception error) => error is RpcException { StatusCode: StatusCode.Unauthenticated };
    public static bool IsFailedPrecondition(Exception error) => error is RpcException { StatusCode: StatusCode.FailedPrecondition };
}