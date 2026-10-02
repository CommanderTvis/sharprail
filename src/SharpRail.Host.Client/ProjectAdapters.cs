using System.Runtime.CompilerServices;

using Grpc.Core;
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

public sealed class LocalProjectAdapter(IProjectServices host) : IProjectServices
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
    public ValueTask<IReadOnlyList<WorktreeInfo>> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListWorkspacesAsync(projectRoot, cancellationToken));
    public ValueTask<string> CloneProjectAsync(string url, string parentPath, string name, int? depth = null, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.CloneProjectAsync(url, parentPath, name, depth, cancellationToken));
    public ValueTask<string> GetDiffAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetDiffAsync(path, scope, comparisonBranch, cancellationToken));
    public ValueTask<DiffSides> GetDiffSidesAsync(string path, string scope, string comparisonBranch = "", CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.GetDiffSidesAsync(path, scope, comparisonBranch, cancellationToken));
    public ValueTask<GitSnapshot> ApplyGitActionAsync(GitAction action, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ApplyGitActionAsync(action, cancellationToken));
    public ValueTask ApplyFileActionAsync(FileAction action, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ApplyFileActionAsync(action, cancellationToken));
    public ValueTask<BranchCatalog> ListBranchesAsync(bool fetchDefault, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListBranchesAsync(fetchDefault, cancellationToken));
    public ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.ListEditorsAsync(cancellationToken));
    public ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default) => OffDispatcher.Run(() => host.OpenInEditorAsync(editorId, worktreePath, cancellationToken));
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
        channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = FileLimits.ReadMessageBytes,
            InitialReconnectBackoff = StateAdapterDefaults.InitialReconnect,
            MaxReconnectBackoff = StateAdapterDefaults.MaxReconnect
        });
        service = channel.CreateGrpcService<IProjectRpc>();
    }

    // The host keeps no per-client session: each call names the workspace this adapter opened last.
    private volatile string root = "";

    private CallContext Context(CancellationToken ct, bool stream = false)
    {
        var headers = new Metadata { { "authorization", $"Bearer {token}" } };
        if (root.Length > 0) headers.Add(ProjectHeaders.Root, System.Text.Encoding.UTF8.GetBytes(root));
        return new(new CallOptions(headers: headers, deadline: stream ? null : DateTime.UtcNow.AddSeconds(60), cancellationToken: ct));
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
            return new(reply.Path, reply.Text, reply.ImageData);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.NotFound) { throw new FileNotFoundException(error.Status.Detail, relativePath); }
    }

    public async ValueTask<GitSnapshot> GetGitAsync(string comparisonBranch = "", CancellationToken cancellationToken = default, string scope = "all")
        => Map(await service.GetGitAsync(new() { Branch = comparisonBranch, Scope = scope }, Context(cancellationToken)));

    public async ValueTask<IReadOnlyList<GitCommit>> ListCommitsAsync(string comparisonBranch, CancellationToken cancellationToken = default)
        => (await service.ListCommitsAsync(new() { Branch = comparisonBranch }, Context(cancellationToken))).Commits.Select(Map).ToArray();

    public async ValueTask<GitCommit?> GetCommitAsync(string sha, CancellationToken cancellationToken = default)
        => (await service.GetCommitAsync(new() { Branch = sha }, Context(cancellationToken))).Commit is { } commit ? Map(commit) : null;

    public async ValueTask<IReadOnlyList<WorktreeInfo>> ListWorkspacesAsync(string projectRoot, CancellationToken cancellationToken = default)
        => [.. (await service.ListWorkspacesAsync(new() { Path = projectRoot }, Context(cancellationToken))).Worktrees
            .Select(tree => new WorktreeInfo(tree.Path, tree.Branch, tree.IsMain, tree.IsLocked))];

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
        return new(reply.Original, reply.Modified);
    }

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

    public async ValueTask<IReadOnlyList<EditorInfo>> ListEditorsAsync(CancellationToken cancellationToken = default)
        => (await service.ListEditorsAsync(new(), Context(cancellationToken))).Editors.Select(editor => new EditorInfo(editor.Id, editor.Label)).ToArray();

    public async ValueTask OpenInEditorAsync(string editorId, string worktreePath, CancellationToken cancellationToken = default)
        => await service.OpenInEditorAsync(new() { EditorId = editorId, WorktreePath = worktreePath }, Context(cancellationToken));

    private static GitSnapshot Map(GitReply reply) => new(reply.IsRepository, reply.Branch,
        reply.Changes.Select(change => new GitChange(change.Path, change.IndexStatus, change.WorktreeStatus, change.OriginalPath, change.Added, change.Removed)).ToArray(),
        reply.Worktrees.Select(tree => new WorktreeInfo(tree.Path, tree.Branch, tree.IsMain, tree.IsLocked)).ToArray(), reply.Branches)
    {
        Commits = reply.Commits.Select(Map).ToArray()
    };

    private static GitCommit Map(CommitReply commit) => new(commit.Sha, commit.ShortSha, commit.Subject, commit.Author, commit.CommittedAt);

    public void Dispose() => channel.Dispose();
}