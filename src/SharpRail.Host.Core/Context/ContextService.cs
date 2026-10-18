using System.Collections.Concurrent;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

namespace SharpRail.Host.Core.Context;

internal interface IContextRuntime : IAsyncDisposable
{
    Task IndexAsync(CancellationToken token);
    Task<string> SearchAsync(string query, string path, int limit, CancellationToken token);
    Task<string> StatusAsync(CancellationToken token);
    Task<string> PrepareLoginAsync(CancellationToken token);
}

internal sealed class ContextService(Func<string, bool> enabled, Func<string, Action<string>, CancellationToken, Task<IContextRuntime>> create) : IAsyncDisposable
{
    private sealed class Entry
    {
        internal readonly CancellationTokenSource Stop;
        internal readonly CancellationToken Token;
        internal Entry(CancellationToken hostToken) { Stop = CancellationTokenSource.CreateLinkedTokenSource(hostToken); Token = Stop.Token; }
        internal readonly SemaphoreSlim Gate = new(1);
        internal Task<IContextRuntime>? Runtime;
        internal Task? Index;
        internal volatile string State = "idle";
        internal volatile string Detail = "Launch a SharpRail agent or choose Index now.";
    }
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LanguageLog> logs = new(StringComparer.Ordinal);
    private int disposed;
    private readonly CancellationTokenSource lifetime = new();

    internal async Task<ContextResult> ExecuteAsync(ContextRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace))
            throw new ArgumentException("Context requires an existing absolute workspace.");
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        if (Volatile.Read(ref disposed) != 0) return new("stopped", "The host is shutting down.");
        if (request.Operation == "logs") return new("ready", "Context output for this workspace.", enabled(workspace), logs.GetOrAdd(workspace, _ => new()).Read("context").Detail);
        if (request.Operation == "stop") { await CloseWorkspaceAsync(workspace); return new("stopped", "SharpRail-owned Context processes stopped.", enabled(workspace)); }
        if (!enabled(workspace)) return new("disabled", "Enable JetBrains Context for this workspace in Code Intelligence. Indexing sends source chunks to JetBrains.");
        if (request.Operation == "status")
            return entries.TryGetValue(workspace, out var known) ? new(known.State, known.Detail, true) : new("idle", "Launch a SharpRail agent or choose Index now.", true);
        if (request.Operation is not ("search" or "index" or "refresh" or "login")) throw new ArgumentException("Unknown Context operation.");
        if (request.Limit is < 1 or > 100 || request.Query.Length > 8000) throw new ArgumentException("Context query limit is 100 results and 8,000 characters.");
        if (request.Operation == "search" && string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("A semantic search query is required.");
        var relative = request.Path.Length == 0 ? "." : Path.GetRelativePath(workspace, ProjectServices.Contain(workspace, request.Path)).Replace('\\', '/');
        var entry = entries.GetOrAdd(workspace, _ => new(lifetime.Token));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Token);
        var operationToken = operation.Token;
        await entry.Gate.WaitAsync(operationToken);
        try
        {
            if (!enabled(workspace)) return new("disabled", "Context was disabled.");
            if (entry.Stop.IsCancellationRequested) return new("stopped", "Context was stopped.", true);
            if (request.Operation == "index")
            {
                if (entry.Index is not { IsCompleted: false })
                {
                    entry.State = "indexing"; entry.Detail = "Indexing this workspace; source chunks are sent to JetBrains.";
                    var prepared = RuntimeAsync(workspace, entry);
                    entry.Index = Task.Run(() => IndexAsync(workspace, entry, prepared), entry.Token);
                }
                return new(entry.State, entry.Detail, true);
            }
            var runtime = await RuntimeAsync(workspace, entry).WaitAsync(operationToken);
            if (request.Operation == "login") return new("login", "Sign in using the dedicated SharpRail terminal. JetBrains credentials stay with the CLI's supported secret store.", true, Command: await runtime.PrepareLoginAsync(operationToken));
            if (request.Operation == "refresh") return new(entry.State, "Vendor-reported index status; this does not start indexing.", true, await runtime.StatusAsync(operationToken));
            if (entry.Index is { IsCompleted: false }) return new("indexing", "Workspace indexing is in progress. Retry the search after it finishes.", true);
            var data = await runtime.SearchAsync(request.Query, relative, request.Limit, operationToken);
            entry.State = "ready"; entry.Detail = "Search completed. Provider output describes index coverage and matching locations.";
            return new("ready", entry.Detail, true, data);
        }
        catch (ContextConsentRequiredException) { return Failure(entry, "requiresAcceptance", "Review the current Context terms in Code Intelligence before running the CLI."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (entry.Token.IsCancellationRequested) { return new("stopped", "SharpRail-owned Context processes stopped.", enabled(workspace)); }
        catch (OperationCanceledException) { return Failure(entry, "error", "Context operation timed out. Retry or view this workspace's logs."); }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            var state = error is ContextLoginRequiredException ? "requiresLogin" : "error";
            return Failure(entry, state, ContextOutput.Sanitize(error.Message));
        }
        finally { entry.Gate.Release(); }
    }

    private Task<IContextRuntime> RuntimeAsync(string workspace, Entry entry)
    {
        if (entry.Runtime is { IsFaulted: true } or { IsCanceled: true }) entry.Runtime = null;
        return entry.Runtime ??= create(workspace,
        text => logs.GetOrAdd(workspace, _ => new()).Add("context", ContextOutput.Sanitize(text)), entry.Token);
    }

    private static ContextResult Failure(Entry entry, string state, string detail)
    { entry.State = state; entry.Detail = detail; return new(state, detail, true); }

    private async Task IndexAsync(string workspace, Entry entry, Task<IContextRuntime> runtime)
    {
        try
        {
            await (await runtime).IndexAsync(entry.Token);
            entry.State = "ready"; entry.Detail = "Index command completed. Search or vendor status reports availability and coverage.";
        }
        catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested) { entry.State = "stopped"; entry.Detail = "Indexing stopped."; }
        catch (ContextConsentRequiredException) { Failure(entry, "requiresAcceptance", "Review the current Context terms in Code Intelligence."); }
        catch (Exception error)
        {
            var detail = ContextOutput.Sanitize(error.Message);
            Failure(entry, error is ContextLoginRequiredException ? "requiresLogin" : "error", detail);
            logs.GetOrAdd(workspace, _ => new()).Add("index", detail);
        }
    }

    internal async Task AgentStartedAsync(string workspace)
    {
        try { if (enabled(workspace)) await ExecuteAsync(new(workspace, "index"), default); }
        catch (Exception error) { logs.GetOrAdd(workspace, _ => new()).Add("launch", ContextOutput.Sanitize(error.Message)); }
    }

    internal async ValueTask CloseWorkspaceAsync(string workspace)
    {
        if (!entries.TryRemove(workspace, out var entry)) return;
        entry.Stop.Cancel();
        await entry.Gate.WaitAsync();
        try
        {
            if (entry.Index is { } index)
            {
                try { await index; } catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested) { }
            }
            if (entry.Runtime is { } pending)
            {
                try { await (await pending).DisposeAsync(); }
                catch (Exception error) { logs.GetOrAdd(workspace, _ => new()).Add("stop", ContextOutput.Sanitize(error.Message)); }
            }
        }
        finally { entry.Gate.Release(); entry.Stop.Dispose(); }
    }

    internal async ValueTask ForgetWorkspaceAsync(string workspace)
    {
        await CloseWorkspaceAsync(workspace);
        logs.TryRemove(workspace, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        foreach (var workspace in entries.Keys) await CloseWorkspaceAsync(workspace);
        lifetime.Dispose();
    }
}