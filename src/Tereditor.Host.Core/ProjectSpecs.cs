using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core;

public sealed partial class ProjectServices
{
    public async ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default)
        => (await SpecCatalog.GraphAsync(root, cancellationToken)).Specs;

    public async ValueTask<SpecGraph> GetSpecGraphAsync(CancellationToken cancellationToken = default)
        => await SpecCatalog.GraphAsync(root, cancellationToken);

    public async ValueTask<bool> HasDurableSpecsAsync(CancellationToken cancellationToken = default)
    {
        // A project that cannot be walked simply has no suggestion to offer; it must never fail the caller.
        try { return (await SpecCatalog.GraphAsync(root, cancellationToken)).Specs.Any(spec => spec.Type != SpecLinks.TaskType); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}