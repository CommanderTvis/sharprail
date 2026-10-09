using Grpc.Core;

using ProtoBuf.Grpc;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class ProjectRpc(ProjectSessions sessions, IHostApplicationLifetime lifetime) : IProjectRpc
{
    public async IAsyncEnumerable<FileChangeReply> WatchFilesAsync(ProjectRequest request, CallContext context = default)
    {
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        await using var changes = Host(context).WatchFilesAsync(watch.Token).GetAsyncEnumerator();
        while (true)
        {
            var next = false;
            try { next = await changes.MoveNextAsync(); }
            catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
            if (!next) yield break;
            yield return new() { Paths = changes.Current.Paths.ToList(), Rescan = changes.Current.Rescan };
        }
    }

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
        var result = await Host(context).ReadFileAsync(request.Path, context.CancellationToken);
        return new DocumentReply { Path = result.Path, Text = result.Text, ImageData = result.ImageData, Info = Info(result.Info) };
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

    public ValueTask<DocumentReply> GetDiffAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => new DocumentReply { Path = request.Path, Text = await Host(context).GetDiffAsync(request.Path, request.Scope, request.Branch, context.CancellationToken) });

    public ValueTask<DiffSidesReply> GetDiffSidesAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var sides = await Host(context).GetDiffSidesAsync(request.Path, request.Scope, request.Branch, context.CancellationToken);
        return new DiffSidesReply
        {
            Original = sides.Original,
            Modified = sides.Modified,
            OriginalHash = sides.OriginalHash,
            ModifiedHash = sides.ModifiedHash,
            OriginalCommit = sides.OriginalCommit,
            OriginalInfo = Info(sides.OriginalInfo),
            ModifiedInfo = Info(sides.ModifiedInfo),
            OriginalRevision = sides.OriginalRevision,
            ModifiedRevision = sides.ModifiedRevision
        };
    });

    private static ContentMetadataDto? Info(ContentMetadata? info) =>
        info is null ? null : new() { Sha256 = info.Sha256, ByteLength = info.ByteLength, IsText = info.IsText, MediaType = info.MediaType };

    public ValueTask<ContentReply> ReadContentBytesAsync(ContentRequest request, CallContext context = default) => Execute(async () =>
    {
        var content = await Host(context).ReadContentBytesAsync(request.Path, request.WorkingTree ? null : request.Revision, context.CancellationToken);
        return new ContentReply { Data = content.Data, Info = Info(content.Info)! };
    });

    public ValueTask<ChangeReceiptReply> RevertChangeAsync(RevertChangeRequest request, CallContext context = default) => Execute(async () =>
    {
        var target = request.IsRange
            ? new RevertTarget(new(request.OriginalStart, request.OriginalCount), new(request.ModifiedStart, request.ModifiedCount))
            : new RevertTarget();
        return Map(await Host(context).RevertChangeAsync(request.Path, request.Scope, request.Branch, target,
            new(request.OriginalHash, request.ModifiedHash), context.CancellationToken));
    });

    public ValueTask<ChangeReceiptReply> UndoChangeAsync(UndoChangeRequest request, CallContext context = default)
        => Execute(async () => Map(await Host(context).UndoChangeAsync(request.ReceiptId, request.ModifiedHash, context.CancellationToken)));

    public ValueTask<GitReply> ApplyGitActionAsync(ProjectRequest request, CallContext context = default)
        => Execute(async () => Map(await Host(context).ApplyGitActionAsync(new(request.Action, request.Path, request.Branch, request.BaseBranch), context.CancellationToken)));

    public ValueTask<BranchesReply> ListBranchesAsync(BranchesRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).ListBranchesAsync(request.FetchDefault, context.CancellationToken);
        return new BranchesReply
        {
            Local = result.Local.ToList(),
            Remote = result.Remote.Select(branch => new RemoteBranchReply { Remote = branch.Remote, Name = branch.Name }).ToList(),
            DefaultBase = result.DefaultBase,
            SuggestedPath = result.SuggestedPath,
            SuggestedBranch = result.SuggestedBranch
        };
    });

    public ValueTask<DiffStatsReply> GetDiffStatsAsync(DiffStatsRequest request, CallContext context = default) => Execute(async () =>
    {
        var stats = await Host(context).GetDiffStatsAsync(request.WorkspacePath, context.CancellationToken) ?? new(0, 0);
        return new DiffStatsReply { Added = stats.Added, Removed = stats.Removed };
    });

    public ValueTask<OpenReviewReply> GetOpenReviewAsync(OpenReviewRequest request, CallContext context = default) => Execute(async () =>
        await Host(context).GetOpenReviewAsync(request.Fresh, context.CancellationToken) is { } review
            ? new OpenReviewReply { Found = true, Number = review.Number, Url = review.Url, Provider = review.Provider, UnpushedCommits = review.UnpushedCommits, BehindCommits = review.BehindCommits }
            : new OpenReviewReply());

    public ValueTask<PrDraftReply> PreviewPrAsync(ProjectRequest request, CallContext context = default) => Execute(async () =>
    {
        var draft = await Host(context).PreviewPrAsync(context.CancellationToken);
        return new PrDraftReply { Title = draft.Title, Body = draft.Body };
    });

    public ValueTask<PrReply> OpenPrAsync(OpenPrRequest request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).OpenPrAsync(new(request.Title, request.TitleEdited, request.Body, request.Draft), context.CancellationToken);
        return new PrReply { Action = result.Action, Url = result.Url, Number = result.Number, DirtyFiles = result.DirtyFiles, GhProblem = result.GhProblem };
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

    private static ChangeReceiptReply Map(ChangeReceipt receipt) => new()
    {
        Id = receipt.Id,
        Path = receipt.Path,
        Kind = receipt.Kind,
        At = receipt.At,
        Trashed = receipt.Trashed,
        BeforeHash = receipt.Before.Hash,
        BeforeLength = receipt.Before.ByteLength,
        BeforeMode = receipt.Before.Mode,
        AfterHash = receipt.After.Hash,
        AfterLength = receipt.After.ByteLength,
        AfterMode = receipt.After.Mode
    };

    private static CommitReply Map(GitCommit commit) => new()
    { Sha = commit.Sha, ShortSha = commit.ShortSha, Subject = commit.Subject, Author = commit.Author, CommittedAt = commit.CommittedAt };

    private IProjectServices Host(CallContext context) =>
        sessions.For(context.ServerCallContext?.RequestHeaders.GetValueBytes(ProjectHeaders.Root) is { } root ? System.Text.Encoding.UTF8.GetString(root) : null);

    private static async ValueTask<T> Execute<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ChangeException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message), new Metadata { { ProjectHeaders.ChangeCode, error.Code.ToString() } });
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
    }
}