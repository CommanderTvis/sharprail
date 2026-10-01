namespace SharpRail.Host.Abstractions;

public record WorkspaceInfo(string Name, string ProjectName, string RootPath)
{
    public string ProjectRoot { get; init; } = RootPath;
}
public record FileEntry(string Name, bool IsDirectory);

public interface IWorkspaceHost
{
    ValueTask<WorkspaceInfo> GetWorkspaceAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<FileEntry>> ListRootFilesAsync(CancellationToken cancellationToken = default);
}