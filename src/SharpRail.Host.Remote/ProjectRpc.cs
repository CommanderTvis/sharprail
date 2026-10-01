using Grpc.Core;

using ProtoBuf.Grpc;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class ProjectRpc(ProjectSessions sessions) : IProjectRpc
{
    public ValueTask<WorkspaceReply> OpenProjectAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await sessions.Detached().OpenProjectAsync(request.Path, context.CancellationToken);
        return new WorkspaceReply { Name = result.Name, ProjectName = result.ProjectName, RootPath = result.RootPath, ProjectRoot = result.ProjectRoot };
    });

    public ValueTask<ProjectFilesReply> ListFilesAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).ListFilesAsync(request.Path, context.CancellationToken);
        return new ProjectFilesReply { Files = result.Select(file => new ProjectFileReply { Path = file.Path, Name = file.Name, IsDirectory = file.IsDirectory }).ToList() };
    });

    public ValueTask<DocumentReply> ReadFileAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        FileDocument result;
        try { result = await Host(context).ReadFileAsync(request.Path, context.CancellationToken); }
        // A missing file is the one read failure a client acts on: an open tab marks its file deleted on disk.
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, error.Message));
        }
        return new DocumentReply { Path = result.Path, Text = result.Text, ImageData = result.ImageData };
    });

    public ValueTask<GitReply> GetGitAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => Map(await Host(context).GetGitAsync(request.Branch, context.CancellationToken, request.Scope.Length == 0 ? "all" : request.Scope)));

    public ValueTask<CommitsReply> ListCommitsAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
        new CommitsReply { Commits = (await Host(context).ListCommitsAsync(request.Branch, context.CancellationToken)).Select(Map).ToList() });

    public ValueTask<SpecsReply> ListSpecsAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).ListSpecsAsync(context.CancellationToken);
        return new SpecsReply { Specs = result.Select(spec => new SpecReply { Id = spec.Id, Title = spec.Title, Path = spec.Path, Parent = spec.Parent, Type = spec.Type }).ToList() };
    });

    public ValueTask<SearchReply> SearchAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).SearchAsync(request.Path, context.CancellationToken);
        return new SearchReply { Hits = result.Hits.Select(hit => new SearchHitReply { Path = hit.Path, Line = hit.Line, Text = hit.Text }).ToList(), Truncated = result.Truncated };
    });

    public ValueTask<DocumentReply> GetDiffAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => new DocumentReply { Path = request.Path, Text = await Host(context).GetDiffAsync(request.Path, request.Scope, request.Branch, context.CancellationToken) });

    public ValueTask<DiffSidesReply> GetDiffSidesAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var sides = await Host(context).GetDiffSidesAsync(request.Path, request.Scope, request.Branch, context.CancellationToken);
        return new DiffSidesReply { Original = sides.Original, Modified = sides.Modified };
    });

    public ValueTask<GitReply> ApplyGitActionAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => Map(await Host(context).ApplyGitActionAsync(new(request.Action, request.Path, request.Branch, request.BaseBranch), context.CancellationToken)));

    public ValueTask<SaveFileReply> ApplyFileActionAsync(FileActionRequest request, CallContext context = default) => Execute(async () =>
    {
        await Host(context).ApplyFileActionAsync(new(request.Kind, request.Path, request.To), context.CancellationToken);
        return new SaveFileReply();
    });

    public ValueTask<BranchesReply> ListBranchesAsync(BranchesRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).ListBranchesAsync(request.FetchDefault, context.CancellationToken);
        return new BranchesReply
        {
            Local = result.Local.ToList(),
            Remote = result.Remote.Select(branch => new RemoteBranchReply { Remote = branch.Remote, Name = branch.Name }).ToList(),
            DefaultBase = result.DefaultBase,
            SuggestedPath = result.SuggestedPath,
            SuggestedBranch = result.SuggestedBranch,
            Current = result.Current
        };
    });

    public ValueTask<EditorsReply> ListEditorsAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
        new EditorsReply { Editors = (await Host(context).ListEditorsAsync(context.CancellationToken)).Select(editor => new EditorReply { Id = editor.Id, Label = editor.Label }).ToList() });

    public ValueTask<SaveFileReply> OpenInEditorAsync(OpenInEditorRequest request, CallContext context = default) => Execute(async () =>
    {
        await Host(context).OpenInEditorAsync(request.EditorId, request.WorktreePath, context.CancellationToken);
        return new SaveFileReply();
    });

    public ValueTask<SaveFileReply> SaveFileAsync(SaveFileRequest request, CallContext context = default) => Execute(async () =>
    {
        await Host(context).SaveFileAsync(new(request.WorkspaceRoot, request.Path, request.OriginalText, request.Text), context.CancellationToken);
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

    private IProjectServices Host(CallContext context) =>
        sessions.For(context.ServerCallContext?.RequestHeaders.GetValueBytes(ProjectHeaders.Root) is { } root ? System.Text.Encoding.UTF8.GetString(root) : null);

    private static async ValueTask<T> Execute<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
    }
}