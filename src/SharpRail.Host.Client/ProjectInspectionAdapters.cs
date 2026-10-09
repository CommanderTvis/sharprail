using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Client;

public sealed partial class LocalProjectAdapter
{
    public ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken cancellationToken = default) => host.InspectProjectPathAsync(path, cancellationToken);
    public ValueTask PrewarmWorkspaceAsync(string path, CancellationToken cancellationToken = default) => host.PrewarmWorkspaceAsync(path, cancellationToken);
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
}