using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed class WorkspaceHost : IWorkspaceHost
{
    private readonly string root;

    public WorkspaceHost(string rootPath)
    {
        root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Directory does not exist: {root}");
    }

    public ValueTask<WorkspaceInfo> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new WorkspaceInfo("Benchmark workspace", new DirectoryInfo(root).Name, root));
    }

    public ValueTask<IReadOnlyList<FileEntry>> ListRootFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new FileEntry(Path.GetFileName(path), Directory.Exists(path)));
        }
        entries.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1) : StringComparer.Ordinal.Compare(a.Name, b.Name));
        return ValueTask.FromResult<IReadOnlyList<FileEntry>>(entries);
    }
}
