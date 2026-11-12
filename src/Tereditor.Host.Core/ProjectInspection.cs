using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core;

public sealed partial class ProjectServices
{
    public async ValueTask<ProjectPathKind> InspectProjectPathAsync(string path, CancellationToken cancellationToken = default)
        => await ProjectPaths.InspectAsync(path, cancellationToken);

    public ValueTask<HostDirectoryListing> ListHostDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => new(Task.Run(() => ProjectPaths.ListDirectory(path, cancellationToken), cancellationToken));
}