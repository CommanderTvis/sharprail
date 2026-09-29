using Grpc.Core;
using ProtoBuf.Grpc;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class ProjectRpc(IProjectServices host) : IProjectRpc
{
    public ValueTask<WorkspaceReply> OpenProjectAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await host.OpenProjectAsync(request.Path, context.CancellationToken);
        return new WorkspaceReply { Name = result.Name, ProjectName = result.ProjectName, RootPath = result.RootPath, ProjectRoot = result.ProjectRoot };
    });

    public ValueTask<ProjectFilesReply> ListFilesAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await host.ListFilesAsync(request.Path, context.CancellationToken);
        return new ProjectFilesReply { Files = result.Select(file => new ProjectFileReply { Path = file.Path, Name = file.Name, IsDirectory = file.IsDirectory }).ToList() };
    });

    public ValueTask<DocumentReply> ReadFileAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await host.ReadFileAsync(request.Path, context.CancellationToken);
        return new DocumentReply { Path = result.Path, Text = result.Text, ImageData = result.ImageData };
    });

    public ValueTask<GitReply> GetGitAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => Map(await host.GetGitAsync(request.Branch, context.CancellationToken, request.Scope.Length == 0 ? "all" : request.Scope)));

    public ValueTask<CommitsReply> ListCommitsAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
        new CommitsReply { Commits = (await host.ListCommitsAsync(request.Branch, context.CancellationToken)).Select(Map).ToList() });

    public ValueTask<SpecsReply> ListSpecsAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await host.ListSpecsAsync(context.CancellationToken);
        return new SpecsReply { Specs = result.Select(spec => new SpecReply { Id = spec.Id, Title = spec.Title, Path = spec.Path, Parent = spec.Parent, Type = spec.Type }).ToList() };
    });

    public ValueTask<DocumentReply> GetDiffAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => new DocumentReply { Path = request.Path, Text = await host.GetDiffAsync(request.Path, request.Scope, request.Branch, context.CancellationToken) });

    public ValueTask<DiffSidesReply> GetDiffSidesAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var sides = await host.GetDiffSidesAsync(request.Path, request.Scope, request.Branch, context.CancellationToken);
        return new DiffSidesReply { Original = sides.Original, Modified = sides.Modified };
    });

    public ValueTask<GitReply> ApplyGitActionAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => Map(await host.ApplyGitActionAsync(new(request.Action, request.Path, request.Branch, request.BaseBranch), context.CancellationToken)));

    public ValueTask<SaveFileReply> SaveFileAsync(SaveFileRequest request, CallContext context = default) => Execute(async () =>
    {
        await host.SaveFileAsync(new(request.WorkspaceRoot, request.Path, request.OriginalText, request.Text), context.CancellationToken);
        return new SaveFileReply();
    });

    private static GitReply Map(GitSnapshot result) => new()
    {
        IsRepository = result.IsRepository,
        Branch = result.Branch,
        Branches = result.Branches.ToList(),
        Commits = result.Commits.Select(Map).ToList(),
        Changes = result.Changes.Select(change => new ChangeReply { Path = change.Path, IndexStatus = change.IndexStatus, WorktreeStatus = change.WorktreeStatus, OriginalPath = change.OriginalPath, Added = change.Added, Removed = change.Removed }).ToList(),
        Worktrees = result.Worktrees.Select(tree => new WorktreeReply { Path = tree.Path, Branch = tree.Branch, IsMain = tree.IsMain, IsLocked = tree.IsLocked }).ToList()
    };

    private static CommitReply Map(GitCommit commit) => new()
    { Sha = commit.Sha, ShortSha = commit.ShortSha, Subject = commit.Subject, Author = commit.Author, CommittedAt = commit.CommittedAt };

    private static async ValueTask<T> Execute<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
    }
}
