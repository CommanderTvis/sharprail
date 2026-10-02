using System.Runtime.CompilerServices;

using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

/// <summary>
/// Embedded calls run Core on the thread pool. Core awaits without leaving the caller's context, so a call made from the
/// UI thread would otherwise start processes and parse their output on the dispatcher.
/// </summary>
internal static class OffDispatcher
{
    public static ValueTask<T> Run<T>(Func<ValueTask<T>> call) => new(Task.Run(async () => await call().ConfigureAwait(false)));
    public static ValueTask Run(Func<ValueTask> call) => new(Task.Run(async () => await call().ConfigureAwait(false)));
}

public sealed partial class LocalProjectAdapter(IProjectServices host) : IProjectServices
{
    public IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync(CancellationToken cancellationToken = default) => host.WatchFilesAsync(cancellationToken);
    public ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.SaveFileAsync(request, cancellationToken));
    public ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.OpenProjectAsync(path, cancellationToken));
    public ValueTask<IReadOnlyList<ProjectFile>> ListFilesAsync(string relativePath, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListFilesAsync(relativePath, cancellationToken));
    public ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ReadFileAsync(relativePath, cancellationToken));
    public ValueTask<SearchHits> SearchAsync(string query, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.SearchAsync(query, cancellationToken));
    public ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all") => OffDispatcher.Run(() => host.GetGitAsync(comparisonBranch, cancellationToken, scope));
    public ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListCommitsAsync(comparisonBranch, cancellationToken));
    public ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetCommitAsync(sha, cancellationToken));
    public ValueTask<string> CreateProjectAsync(string parentPath, string name, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.CreateProjectAsync(parentPath, name, cancellationToken));
    public ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.CloneProjectAsync(url, parentPath, name, depth, cancellationToken));
    public ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetDiffAsync(path, scope, comparisonBranch, cancellationToken));
    public ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetDiffSidesAsync(path, scope, comparisonBranch, cancellationToken));
    public ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ApplyGitActionAsync(action, cancellationToken));
    public ValueTask ApplyFileActionAsync(FileAction action, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ApplyFileActionAsync(action, cancellationToken));
    public ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListBranchesAsync(fetchDefault, cancellationToken));
    public ValueTask<DiffStats?> GetDiffStatsAsync(string workspacePath, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetDiffStatsAsync(workspacePath, cancellationToken));
    public ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListEditorsAsync(cancellationToken));
    public ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.OpenInEditorAsync(editorId, worktreePath, cancellationToken));
    public ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ReadContentBytesAsync(path, revision, cancellationToken));
    public ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparisonBranch, RevertTarget target, ChangeExpectation expect, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.RevertChangeAsync(path, scope, comparisonBranch, target, expect, cancellationToken));
    public ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.UndoChangeAsync(receiptId, expectModifiedHash, cancellationToken));
    public ValueTask<OpenReview?> GetOpenReviewAsync(bool fresh, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetOpenReviewAsync(fresh, cancellationToken));
    public ValueTask<PrDraft> PreviewPrAsync(CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.PreviewPrAsync(cancellationToken));
    public ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.OpenPrAsync(request, cancellationToken));
    public ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListSpecsAsync(cancellationToken));
    public ValueTask<WorkspaceCatalog> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListWorkspacesAsync(projectRoot, cancellationToken));
    public ValueTask<WorkspaceRecord?> ApplyWorkspaceActionAsync(WorkspaceAction action, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ApplyWorkspaceActionAsync(action, cancellationToken));
}

public sealed partial class RemoteProjectAdapter : IProjectServices, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly IProjectRpc service;
    private readonly string token;

    /// <summary>Adapters of one client share <paramref name="connection"/>; without one this adapter is its own client.</summary>
    public RemoteProjectAdapter(Uri address, string token, HostConnection? connection = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.");
        this.token = token;
        channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = FileLimits.ReadMessageBytes,
            InitialReconnectBackoff = StateAdapterDefaults.InitialReconnect,
            MaxReconnectBackoff = StateAdapterDefaults.MaxReconnect
        });
        service = channel.Intercept(new HostCallInterceptor(connection ?? new())).CreateGrpcService<IProjectRpc>();
    }

    // The host keeps no per-client session: each call names the workspace this adapter opened last.
    private volatile string root = "";

    private CallContext Context(CancellationToken ct, bool stream = false, TimeSpan? deadline = null)
    {
        var headers = new Metadata { { "authorization", $"Bearer {token}" } };
        if (root.Length > 0) headers.Add(ProjectHeaders.Root, System.Text.Encoding.UTF8.GetBytes(root));
        return new(new CallOptions(headers: headers, deadline: stream ? null : DateTime.UtcNow + (deadline ?? TimeSpan.FromSeconds(60)), cancellationToken: ct));
    }

    public async ValueTask<WorkspaceInfo> OpenProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        var reply = await service.OpenProjectAsync(new() { Path = path }, Context(cancellationToken));
        root = reply.RootPath;
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

    public async IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await foreach (var changes in service.WatchFilesAsync(new(), Context(cancellationToken, stream: true)).WithCancellation(cancellationToken))
            yield return new(changes.Paths, changes.GitChanged, changes.Rescan);
    }

    public async ValueTask<FileDocument> ReadFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await service.ReadFileAsync(new() { Path = relativePath }, Context(cancellationToken));
            return new(reply.Path, reply.Text, reply.ImageData) { Info = Info(reply.Info) };
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.NotFound) { throw new FileNotFoundException(error.Status.Detail, relativePath); }
    }

    public async ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all")
        => Map(await service.GetGitAsync(new() { Branch = comparisonBranch, Scope = scope }, Context(cancellationToken)));

    public async ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default)
        => (await service.ListCommitsAsync(new() { Branch = comparisonBranch }, Context(cancellationToken))).Commits.Select(Map).ToArray();

    public async ValueTask<IReadOnlyList<SpecDocument>> ListSpecsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await service.ListSpecsAsync(new(), Context(cancellationToken));
        return reply.Specs.Select(Map).ToArray();
    }
    public async ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default)
        => (await service.GetCommitAsync(new() { Branch = sha }, Context(cancellationToken))).Commit is { } commit ? Map(commit) : null;

    public async ValueTask<string> CreateProjectAsync(string parentPath, string name, CancellationToken cancellationToken = default)
        => (await service.CreateProjectAsync(new() { Path = parentPath, Branch = name }, Context(cancellationToken))).Text;

    public async ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken cancellationToken = default)
        => (await service.CloneProjectAsync(new() { Url = url, Path = parentPath, Branch = name, Depth = depth ?? 0 }, Context(cancellationToken))).Text;

    public async ValueTask<SearchHits> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var reply = await service.SearchAsync(new() { Path = query }, Context(cancellationToken));
        return new(reply.Hits.Select(hit => new SearchHit(hit.Path, hit.Line, hit.Text)).ToArray(), reply.Truncated);
    }

    public async ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default)
        => (await service.GetDiffAsync(new() { Path = path, Scope = scope, Branch = comparisonBranch }, Context(cancellationToken))).Text;

    public async ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default)
    {
        var reply = await service.GetDiffSidesAsync(new() { Path = path, Scope = scope, Branch = comparisonBranch }, Context(cancellationToken));
        return new(reply.Original, reply.Modified)
        {
            OriginalHash = reply.OriginalHash,
            ModifiedHash = reply.ModifiedHash,
            OriginalCommit = reply.OriginalCommit,
            OriginalInfo = Info(reply.OriginalInfo),
            ModifiedInfo = Info(reply.ModifiedInfo),
            OriginalRevision = reply.OriginalRevision,
            ModifiedRevision = reply.ModifiedRevision
        };
    }

    public async ValueTask<ContentBytes> ReadContentBytesAsync(string path, string? revision, CancellationToken cancellationToken = default)
    {
        var reply = await service.ReadContentBytesAsync(new() { Path = path, WorkingTree = revision is null, Revision = revision ?? "" }, Context(cancellationToken));
        return new(reply.Data, Info(reply.Info)!);
    }

    public async ValueTask<ChangeReceipt> RevertChangeAsync(string path, string scope, string comparisonBranch, RevertTarget target, ChangeExpectation expect, CancellationToken cancellationToken = default)
    {
        var request = new RevertChangeRequest { Path = path, Scope = scope, Branch = comparisonBranch, OriginalHash = expect.OriginalHash, ModifiedHash = expect.ModifiedHash };
        if (!target.IsFile)
        {
            request.IsRange = true;
            request.OriginalStart = target.Original!.Start; request.OriginalCount = target.Original.Count;
            request.ModifiedStart = target.Modified!.Start; request.ModifiedCount = target.Modified.Count;
        }
        try { return Map(await service.RevertChangeAsync(request, Context(cancellationToken))); }
        catch (RpcException error) when (Coded(error) is { } failure) { throw failure; }
    }

    public async ValueTask<ChangeReceipt> UndoChangeAsync(string receiptId, string? expectModifiedHash, CancellationToken cancellationToken = default)
    {
        try { return Map(await service.UndoChangeAsync(new() { ReceiptId = receiptId, ModifiedHash = expectModifiedHash }, Context(cancellationToken))); }
        catch (RpcException error) when (Coded(error) is { } failure) { throw failure; }
    }

    private static ContentMetadata? Info(ContentMetadataDto? dto) => dto is null ? null : new(dto.Sha256, dto.ByteLength, dto.IsText, dto.MediaType);

    private static ChangeException? Coded(RpcException error) =>
        error.Trailers.GetValue(ProjectHeaders.ChangeCode) is { } code && Enum.TryParse<ChangeFailure>(code, out var failure)
            ? new ChangeException(failure, error.Status.Detail) : null;

    private static ChangeReceipt Map(ChangeReceiptReply reply) => new(reply.Id, reply.Path, reply.Kind, reply.At,
        new(reply.BeforeHash, reply.BeforeLength, reply.BeforeMode), new(reply.AfterHash, reply.AfterLength, reply.AfterMode), reply.Trashed);

    public async ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default)
        => Map(await service.ApplyGitActionAsync(new() { Action = action.Kind, Path = action.Path, Branch = action.Branch, BaseBranch = action.BaseBranch }, Context(cancellationToken)));

    public async ValueTask SaveFileAsync(FileSaveRequest request, CancellationToken cancellationToken = default)
        => await service.SaveFileAsync(new() { WorkspaceRoot = request.WorkspaceRoot, Path = request.Path, OriginalText = request.OriginalText, Text = request.Text }, Context(cancellationToken));

    public async ValueTask ApplyFileActionAsync(FileAction action, CancellationToken cancellationToken = default)
        => await service.ApplyFileActionAsync(new() { Kind = action.Kind, Path = action.Path, To = action.To }, Context(cancellationToken));

    public async ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default)
    {
        var reply = await service.ListBranchesAsync(new() { FetchDefault = fetchDefault }, Context(cancellationToken));
        return new(reply.Local, reply.Remote.Select(branch => new RemoteBranch(branch.Remote, branch.Name)).ToArray(), reply.DefaultBase)
        { SuggestedPath = reply.SuggestedPath, SuggestedBranch = reply.SuggestedBranch, Current = reply.Current };
    }

    public async ValueTask<DiffStats?> GetDiffStatsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await service.GetDiffStatsAsync(new() { WorkspacePath = workspacePath }, Context(cancellationToken));
            return new(reply.Added, reply.Removed);
        }
        // A host that predates the totals has no badge to offer.
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { return null; }
    }

    public async ValueTask<OpenReview?> GetOpenReviewAsync(bool fresh, CancellationToken cancellationToken = default)
    {
        var reply = await service.GetOpenReviewAsync(new() { Fresh = fresh }, Context(cancellationToken));
        return reply.Found ? new(reply.Number, reply.Url, reply.Provider, reply.UnpushedCommits, reply.BehindCommits) : null;
    }

    public async ValueTask<PrDraft> PreviewPrAsync(CancellationToken cancellationToken = default)
    {
        var reply = await service.PreviewPrAsync(new(), Context(cancellationToken));
        return new(reply.Title, reply.Body);
    }

    // Pushing and gh together may run for minutes, so this call carries its own long deadline.
    public async ValueTask<PrResult> OpenPrAsync(PrRequest request, CancellationToken cancellationToken = default)
    {
        var reply = await service.OpenPrAsync(new OpenPrRequest { Title = request.Title, TitleEdited = request.TitleEdited, Body = request.Body, Draft = request.Draft },
            Context(cancellationToken, deadline: TimeSpan.FromMinutes(5)));
        return new(reply.Action, reply.Url, reply.Number, reply.DirtyFiles, reply.GhProblem);
    }

    public async ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default)
        => (await service.ListEditorsAsync(new(), Context(cancellationToken))).Editors.Select(editor => new EditorInfo(editor.Id, editor.Label)).ToArray();

    public async ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default)
        => await service.OpenInEditorAsync(new() { EditorId = editorId, WorktreePath = worktreePath }, Context(cancellationToken));

    public async ValueTask<WorkspaceCatalog> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var reply = await service.ListWorkspacesAsync(new() { ProjectRoot = projectRoot }, Context(cancellationToken));
        return new(reply.Workspaces.Select(WorkspaceMessages.Map).ToArray(), reply.Existing.Select(tree => new ExistingWorktree(tree.Path, tree.Branch)).ToArray());
    }

    // Creating may fetch the base first, which a slow remote stretches well past an ordinary call.
    public async ValueTask<WorkspaceRecord?> ApplyWorkspaceActionAsync(WorkspaceAction action, CancellationToken cancellationToken = default)
    {
        var reply = await service.ApplyWorkspaceActionAsync(new()
        {
            Kind = action.Kind,
            ProjectRoot = action.ProjectRoot,
            Id = action.Id,
            Path = action.Path,
            Name = action.Name,
            BaseBranch = action.BaseBranch
        }, Context(cancellationToken, deadline: TimeSpan.FromMinutes(5)));
        return reply.Workspace is { } workspace ? WorkspaceMessages.Map(workspace) : null;
    }

    private static GitSnapshot Map(GitReply reply) => new(reply.IsRepository, reply.Branch,
        reply.Changes.Select(change => new GitChange(change.Path, change.IndexStatus, change.WorktreeStatus, change.OriginalPath, change.Added, change.Removed)).ToArray(),
        reply.Worktrees.Select(tree => new WorktreeInfo(tree.Path, tree.Branch, tree.IsMain, tree.IsLocked)).ToArray(), reply.Branches)
    {
        Commits = reply.Commits.Select(Map).ToArray()
    };

    private static GitCommit Map(CommitReply commit) => new(commit.Sha, commit.ShortSha, commit.Subject, commit.Author, commit.CommittedAt);

    public void Dispose() => channel.Dispose();
}