using Grpc.Core;
using Grpc.Net.Client;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed class LocalProjectAdapter(IProjectServices host) : IProjectServices
{
    public ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default) => host.OpenProjectAsync(path, cancellationToken);
    public ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default) => host.ListFilesAsync(relativePath, cancellationToken);
    public ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default) => host.ReadFileAsync(relativePath, cancellationToken);
    public ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default) => host.ListSpecsAsync(cancellationToken);
    public ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all") => host.GetGitAsync(comparisonBranch, cancellationToken, scope);
    public ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default) => host.ListCommitsAsync(comparisonBranch, cancellationToken);
    public ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default) => host.GetDiffAsync(path, scope, comparisonBranch, cancellationToken);
    public ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default) => host.ApplyGitActionAsync(action, cancellationToken);
}

public sealed class RemoteProjectAdapter : IProjectServices, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly IProjectRpc service;
    private readonly string token;

    public RemoteProjectAdapter(Uri address, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.");
        this.token = token;
        channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { MaxReceiveMessageSize = 16 * 1024 * 1024 });
        service = channel.CreateGrpcService<IProjectRpc>();
    }

    private CallContext Context(CancellationToken ct) => new(new CallOptions(
        headers: new Metadata { { "authorization", $"Bearer {token}" } },
        deadline: DateTime.UtcNow.AddSeconds(60), cancellationToken: ct));

    public async ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        var reply = await service.OpenProjectAsync(new() { Path = path }, Context(cancellationToken));
        return new(reply.Name, reply.ProjectName, reply.RootPath)
        {
            ProjectRoot = reply.ProjectRoot.Length > 0 ? reply.ProjectRoot : reply.RootPath
        };
    }

    public async ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var reply = await service.ListFilesAsync(new() { Path = relativePath }, Context(cancellationToken));
        return reply.Files.Select(file => new ProjectFile(file.Path, file.Name, file.IsDirectory)).ToArray();
    }

    public async ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var reply = await service.ReadFileAsync(new() { Path = relativePath }, Context(cancellationToken));
        return new(reply.Path, reply.Text, reply.ImageData);
    }

    public async ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all")
        => Map(await service.GetGitAsync(new() { Branch = comparisonBranch, Scope = scope }, Context(cancellationToken)));

    public async ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default)
        => (await service.ListCommitsAsync(new() { Branch = comparisonBranch }, Context(cancellationToken))).Commits.Select(Map).ToArray();

    public async ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await service.ListSpecsAsync(new(), Context(cancellationToken));
        return reply.Specs.Select(spec => new SpecDocument(spec.Id, spec.Title, spec.Path, spec.Parent, spec.Type)).ToArray();
    }

    public async ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default)
        => (await service.GetDiffAsync(new() { Path = path, Scope = scope, Branch = comparisonBranch }, Context(cancellationToken))).Text;

    public async ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default)
        => Map(await service.ApplyGitActionAsync(new() { Action = action.Kind, Path = action.Path, Branch = action.Branch, BaseBranch = action.BaseBranch }, Context(cancellationToken)));

    private static GitSnapshot Map(GitReply reply) => new(reply.IsRepository, reply.Branch,
        reply.Changes.Select(change => new GitChange(change.Path, change.IndexStatus, change.WorktreeStatus, change.OriginalPath, change.Added, change.Removed)).ToArray(),
        reply.Worktrees.Select(tree => new WorktreeInfo(tree.Path, tree.Branch, tree.IsMain, tree.IsLocked)).ToArray(), reply.Branches)
    {
        Commits = reply.Commits.Select(Map).ToArray()
    };

    private static GitCommit Map(CommitReply commit) => new(commit.Sha, commit.ShortSha, commit.Subject, commit.Author, commit.CommittedAt);

    public void Dispose() => channel.Dispose();
}
