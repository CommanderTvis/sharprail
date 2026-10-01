using Grpc.Core;
using Grpc.Net.Client;

using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed class LocalHostAdapter(IWorkspaceHost host) : IWorkspaceHost
{
    public ValueTask<WorkspaceInfo> GetWorkspaceAsync(CancellationToken cancellationToken = default)
        => host.GetWorkspaceAsync(cancellationToken);

    public ValueTask<IReadOnlyList<FileEntry>> ListRootFilesAsync(CancellationToken cancellationToken = default)
        => host.ListRootFilesAsync(cancellationToken);
}

public sealed class RemoteHostAdapter : IWorkspaceHost, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly IWorkspaceRpc service;
    private readonly string token;

    public RemoteHostAdapter(Uri address, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        this.token = token;
        channel = GrpcChannel.ForAddress(address);
        service = channel.CreateGrpcService<IWorkspaceRpc>();
    }

    private CallContext Context(CancellationToken cancellationToken) => new(new CallOptions(
        headers: new Metadata { { "authorization", $"Bearer {token}" } },
        deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: cancellationToken));

    public async ValueTask<WorkspaceInfo> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var reply = await service.GetWorkspaceAsync(new(), Context(cancellationToken));
        return new(reply.Name, reply.ProjectName, reply.RootPath);
    }

    public async ValueTask<IReadOnlyList<FileEntry>> ListRootFilesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await service.ListRootFilesAsync(new(), Context(cancellationToken));
        return reply.Entries.Select(entry => new FileEntry(entry.Name, entry.IsDirectory)).ToArray();
    }

    public void Dispose() => channel.Dispose();
}