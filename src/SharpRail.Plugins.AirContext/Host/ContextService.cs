using System.Collections.Concurrent;

using SharpRail.Plugins.AirContext;

namespace SharpRail.Plugins.AirContext.Host;

internal interface IContextRuntime : IAsyncDisposable
{
    Task IndexAsync(CancellationToken token);
    Task<string> SearchAsync(string query, string path, int limit, CancellationToken token);
    Task<string> StatusAsync(CancellationToken token);
    Task<string> PrepareLoginAsync(CancellationToken token);
    Task LogoutAsync(CancellationToken token);
}

internal sealed class ContextService(Func<string, CancellationToken, ValueTask<bool>> enabled, Func<string, Action<string>, CancellationToken, Task<IContextRuntime>> create) : IAsyncDisposable
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
    private readonly ConcurrentDictionary<string, ContextLog> logs = new(StringComparer.Ordinal);
    private int disposed;
    // The JetBrains login is the CLI's, shared by every workspace: -1 unknown, 0 it asked for a login, 1 it accepted the stored one.
    private int signed = -1;
    private readonly CancellationTokenSource lifetime = new();

    internal async Task<ContextResult> ExecuteAsync(ContextRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace))
            throw new ArgumentException("Context requires an existing absolute workspace.");
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        if (Volatile.Read(ref disposed) != 0) return new("stopped", "The host is shutting down.");
        var on = await enabled(workspace, token);
        if (request.Operation == "logs") return new("ready", "Context output for this workspace.", on, logs.GetOrAdd(workspace, _ => new()).Read());
        if (request.Operation == "stop") { await CloseWorkspaceAsync(workspace); return new("stopped", "SharpRail-owned Context processes stopped.", on); }
        // The login belongs to the CLI, not to a workspace: probing, signing in and out send no source and need no enablement.
        var account = request.Operation is "check" or "login" or "logout";
        if (!account && !on) return new("disabled", "Enable JetBrains Context for this project in the Air Context pane; it covers every workspace of the project. Indexing sends source chunks to JetBrains.", SignedIn: Known());
        if (request.Operation == "status")
            return entries.TryGetValue(workspace, out var known) ? new(known.State == "requiresLogin" && Known() == true ? "idle" : known.State, known.State == "requiresLogin" && Known() == true ? "Launch a SharpRail agent or choose Index now." : known.Detail, true, SignedIn: Known()) : new("idle", "Launch a SharpRail agent or choose Index now.", true, SignedIn: Known());
        if (request.Operation is not ("search" or "index" or "refresh" or "login" or "check" or "logout")) throw new ArgumentException("Unknown Context operation.");
        if (request.Limit is < 1 or > 100 || request.Query.Length > 8000) throw new ArgumentException("Context query limit is 100 results and 8,000 characters.");
        if (request.Operation == "search" && string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("A semantic search query is required.");
        var relative = request.Path.Length == 0 ? "." : Path.GetRelativePath(workspace, Containment.Contain(workspace, request.Path)).Replace('\\', '/');
        var entry = entries.GetOrAdd(workspace, _ => new(lifetime.Token));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Token);
        var operationToken = operation.Token;
        await entry.Gate.WaitAsync(operationToken);
        try
        {
            on = await enabled(workspace, operationToken);
            if (!account && !on) return new("disabled", "Context was disabled.");
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
            if (request.Operation == "check")
            {
                if (entry.Index is { IsCompleted: false }) return new(entry.State, entry.Detail, on, SignedIn: true);
                try { await runtime.StatusAsync(operationToken); }
                catch (IOException error) when (error is not ContextLoginRequiredException)
                {
                    // The probe failing for another reason says nothing about the login: keep what is known.
                    return new(entry.State, ContextOutput.Sanitize(error.Message), on, SignedIn: Known());
                }
                Interlocked.Exchange(ref signed, 1);
                if (entry.State == "requiresLogin") { entry.State = "idle"; entry.Detail = "Launch a SharpRail agent or choose Index now."; }
                return new(entry.State, entry.Detail, on, SignedIn: true);
            }
            if (request.Operation == "logout")
            {
                await runtime.LogoutAsync(operationToken);
                Interlocked.Exchange(ref signed, 0);
                entry.State = "requiresLogin"; entry.Detail = "Signed out of JetBrains.";
                return new(entry.State, entry.Detail, on, SignedIn: false);
            }
            if (request.Operation == "login") return new("login", "Sign in using the dedicated SharpRail terminal. JetBrains credentials stay with the CLI's supported secret store.", true, Command: await runtime.PrepareLoginAsync(operationToken));
            if (request.Operation == "refresh") return new(entry.State, "Vendor-reported index status; this does not start indexing.", true, await runtime.StatusAsync(operationToken));
            if (entry.Index is { IsCompleted: false }) return new("indexing", "Workspace indexing is in progress. Retry the search after it finishes.", true);
            var data = await runtime.SearchAsync(request.Query, relative, request.Limit, operationToken);
            Interlocked.Exchange(ref signed, 1);
            entry.State = "ready"; entry.Detail = "Search completed. Provider output describes index coverage and matching locations.";
            return new("ready", entry.Detail, true, data);
        }
        catch (ContextConsentRequiredException) { return Failure(entry, "requiresAcceptance", "Review the current Context terms in the Air Context pane before running the CLI."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (entry.Token.IsCancellationRequested) { return new("stopped", "SharpRail-owned Context processes stopped.", on); }
        catch (OperationCanceledException) { return Failure(entry, "error", "Context operation timed out. Retry or view this workspace's logs."); }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            var state = error is ContextLoginRequiredException ? "requiresLogin" : "error";
            if (error is ContextLoginRequiredException) Interlocked.Exchange(ref signed, 0);
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

    private bool? Known() => Volatile.Read(ref signed) switch { 1 => true, 0 => false, _ => null };

    private ContextResult Failure(Entry entry, string state, string detail)
    { entry.State = state; entry.Detail = detail; return new(state, detail, true, SignedIn: Known()); }

    private async Task IndexAsync(string workspace, Entry entry, Task<IContextRuntime> runtime)
    {
        try
        {
            await (await runtime).IndexAsync(entry.Token);
            Interlocked.Exchange(ref signed, 1);
            entry.State = "ready"; entry.Detail = "Index command completed. Search or vendor status reports availability and coverage.";
        }
        catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested) { entry.State = "stopped"; entry.Detail = "Indexing stopped."; }
        catch (ContextConsentRequiredException) { Failure(entry, "requiresAcceptance", "Review the current Context terms in the Air Context pane."); }
        catch (Exception error)
        {
            var detail = ContextOutput.Sanitize(error.Message);
            if (error is ContextLoginRequiredException) Interlocked.Exchange(ref signed, 0);
            Failure(entry, error is ContextLoginRequiredException ? "requiresLogin" : "error", detail);
            logs.GetOrAdd(workspace, _ => new()).Add("index", detail);
        }
    }

    internal async Task AgentStartedAsync(string workspace)
    {
        try { if (await enabled(workspace, default)) await ExecuteAsync(new(workspace, "index"), default); }
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