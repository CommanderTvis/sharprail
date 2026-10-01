using ProtoBuf.Grpc;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class WorkspaceRpc(IWorkspaceHost host) : IWorkspaceRpc
{
    public async ValueTask<WorkspaceReply> GetWorkspaceAsync(WorkspaceRequest request, CallContext context = default)
    {
        var result = await host.GetWorkspaceAsync(context.CancellationToken);
        return new() { Name = result.Name, ProjectName = result.ProjectName, RootPath = result.RootPath };
    }

    public async ValueTask<FilesReply> ListRootFilesAsync(WorkspaceRequest request, CallContext context = default)
    {
        var result = await host.ListRootFilesAsync(context.CancellationToken);
        return new() { Entries = result.Select(entry => new FileReply { Name = entry.Name, IsDirectory = entry.IsDirectory }).ToList() };
    }
}