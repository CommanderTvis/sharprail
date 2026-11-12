using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed partial class LocalProjectAdapter
{
    public ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken cancellationToken = default) => host.InspectProjectPathAsync(path, cancellationToken);
    public ValueTask PrewarmWorkspaceAsync(string path, CancellationToken cancellationToken = default) => host.PrewarmWorkspaceAsync(path, cancellationToken);
    public ValueTask<HostDirectoryListing> ListHostDirectoryAsync(string path, CancellationToken cancellationToken = default) => host.ListHostDirectoryAsync(path, cancellationToken);
}

public sealed partial class RemoteProjectAdapter
{
    public async ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var kind = (await service.InspectProjectPathAsync(new() { Path = path }, Context(cancellationToken))).Kind;
        return Enum.IsDefined((ProjectPathKind)kind) ? (ProjectPathKind)kind : throw new IOException("The host answered with an unknown project path kind.");
    }

    public async ValueTask PrewarmWorkspaceAsync(string path, CancellationToken cancellationToken = default)
        => await service.PrewarmWorkspaceAsync(new() { Path = path }, Context(cancellationToken));

    public async ValueTask<HostDirectoryListing> ListHostDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        HostDirectoryReply reply;
        try { reply = await service.ListHostDirectoryAsync(new() { Path = path }, Context(cancellationToken)); }
        catch (RpcException error) when (error.StatusCode == StatusCode.NotFound) { throw new DirectoryNotFoundException(error.Status.Detail); }
        catch (RpcException error) when (error.StatusCode == StatusCode.PermissionDenied) { throw new UnauthorizedAccessException(error.Status.Detail); }
        return new(reply.Path, reply.Parent.Length == 0 ? null : reply.Parent, reply.Home,
            reply.Entries.Select(entry => new HostDirectoryEntry(entry.Name, entry.IsDirectory, entry.Size)).ToArray());
    }
}