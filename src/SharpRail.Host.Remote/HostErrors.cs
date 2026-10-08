using Grpc.Core;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

internal static class HostErrors
{
    /// <summary>The status a named failure travels as: its message, with the code in a trailer.</summary>
    internal static RpcException ToRpc(HostException error) =>
        new(new Status(StatusCode.FailedPrecondition, error.Message), new Metadata { { HostHeaders.ErrorCode, error.Code.ToString() } });
}