using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed partial class LocalProjectAdapter
{
    public ValueTask<ContextConsentResult> ContextConsentAsync(ContextConsentRequest request, CancellationToken cancellationToken = default)
        => OffDispatcher.Run(() => host.ContextConsentAsync(request, cancellationToken));
}

public sealed partial class RemoteProjectAdapter
{
    public async ValueTask<ContextConsentResult> ContextConsentAsync(ContextConsentRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await service.ContextConsentAsync(new() { Operation = request.Operation, ArtifactId = request.ArtifactId, TermsSha256 = request.TermsSha256 }, Context(cancellationToken, deadline: TimeSpan.FromMinutes(10)));
            return new(result.State, result.Detail, result.Version, result.ArtifactId, result.TermsSha256, result.Text, result.AcceptedAtUtc, result.Installation);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { return new("unavailable", "This host does not support Context provisioning."); }
        catch (RpcException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }
}

public sealed partial class LocalProjectAdapter
{
    public ValueTask<ContextResult> ContextAsync(ContextRequest request, CancellationToken cancellationToken = default)
        => OffDispatcher.Run(() => host.ContextAsync(request, cancellationToken));
}

public sealed partial class RemoteProjectAdapter
{
    public async ValueTask<ContextResult> ContextAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await service.ContextAsync(new() { Workspace = request.Workspace, Operation = request.Operation, Query = request.Query, Path = request.Path, Limit = request.Limit }, Context(cancellationToken, deadline: TimeSpan.FromMinutes(3)));
            return new(result.State, result.Detail, result.Enabled, result.Data, result.Command);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { return new("unavailable", "This host does not support Context search."); }
        catch (RpcException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }
}