using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed partial class ProjectRpc
{
    public ValueTask<InspectProjectReply> InspectProjectPathAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
        new InspectProjectReply { Kind = (int)await sessions.Detached().InspectProjectPathAsync(request.Path, context.CancellationToken) });

    // Read-only; every caller is an authenticated (paired or master) connection. Missing and unreadable folders keep
    // their kind so the client can say which.
    public async ValueTask<HostDirectoryReply> ListHostDirectoryAsync(ProjectRequest request, CallContext context = default)
    {
        HostDirectoryListing listing;
        try { listing = await sessions.Detached().ListHostDirectoryAsync(request.Path, context.CancellationToken); }
        catch (DirectoryNotFoundException error) { throw new RpcException(new Status(StatusCode.NotFound, error.Message)); }
        catch (UnauthorizedAccessException error) { throw new RpcException(new Status(StatusCode.PermissionDenied, error.Message)); }
        catch (Exception error) when (error is IOException or ArgumentException) { throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message)); }
        return new HostDirectoryReply
        {
            Path = listing.Path, Parent = listing.Parent ?? "", Home = listing.Home,
            Entries = listing.Entries.Select(entry => new HostDirectoryEntryReply { Name = entry.Name, IsDirectory = entry.IsDirectory, Size = entry.Size }).ToList()
        };
    }

    public ValueTask<SaveFileReply> PrewarmWorkspaceAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        await sessions.Detached().PrewarmWorkspaceAsync(request.Path, context.CancellationToken);
        return new SaveFileReply();
    });
}