using System.Diagnostics;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

public sealed partial class LanguageService
{
    /// <summary>How long on-demand starts wait for a server; settable so checks need not wait a minute.</summary>
    internal TimeSpan OnDemandWait { get; set; } = LanguageLimits.OnDemandStartWait;

    /// <summary>An agent tool call: starts the servers that own the requested files and waits for them, bounded by <see cref="OnDemandWait"/>.</summary>
    internal async Task<LanguageResult> ExecuteForAgentAsync(LanguageRequest request, CancellationToken token)
    {
        request = request with { Origin = LanguageLimits.AgentOrigin };
        LanguageResult result;
        try { result = await ExecuteForAgentCoreAsync(request, token); }
        catch (Exception) when (Volatile.Read(ref disposed) == 0)
        {
            RecordAgentUse(request, null);
            throw;
        }
        RecordAgentUse(request, result);
        return result with { Hint = result.Hint.Length > 0 ? result.Hint : Hint(request, result) };
    }

    private async Task<LanguageResult> ExecuteForAgentCoreAsync(LanguageRequest request, CancellationToken token)
    {
        if (request.Operation is not ("hover" or "definition" or "typeDefinition" or "implementation" or "references" or "symbols" or "symbolBody" or "workspaceSymbols"))
            return await ExecuteAsync(request, token);
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace)) return await ExecuteAsync(request, token);
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        if (request.Path.Length == 0 && (request.Operation == "workspaceSymbols" || LanguageUsage.MapsToWorkspaceSearch(request)))
        {
            await Task.WhenAll(Discover(workspace).Select(path => EnsureStartedAsync(workspace, path, request.Origin, token)));
            return await ExecuteAsync(request, token);
        }
        var result = await ExecuteAsync(request, token);
        if (result.State is not ("installing" or "initializing") || result.Server.Length == 0) return result;
        var waited = Stopwatch.StartNew();
        if (!await WaitForServerAsync(workspace, result.Server, token)) return result with { Detail = Initializing(result.Server, waited.Elapsed, result.Detail) };
        return await ExecuteAsync(request, token);
    }

    /// <summary>Starts the server that owns <paramref name="path"/> when it is not running and waits for it; the result says honestly when it is still initializing.</summary>
    internal async Task<LanguageResult> EnsureStartedAsync(string workspace, string path, string origin, CancellationToken token)
    {
        try
        {
            var touch = await ExecuteAsync(new(workspace, path, "status", Origin: origin), token);
            if (touch.State is "stopped" && touch.Server.Length > 0)
                return touch with { Detail = $"{touch.Server} was stopped on request. Start it with lsp_status {{\"operation\":\"retry\",\"server\":\"{touch.Server}\",\"workspace\":\"{workspace}\"}}." };
            if (touch.State is not ("installing" or "initializing") || touch.Server.Length == 0) return touch;
            var waited = Stopwatch.StartNew();
            if (!await WaitForServerAsync(workspace, touch.Server, token)) return touch with { Detail = Initializing(touch.Server, waited.Elapsed, touch.Detail) };
            return await ExecuteAsync(new(workspace, path, "status", Origin: origin), token);
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException) { return new("error", error.Message) { Freshness = "unavailable" }; }
    }

    /// <summary>Waits until the server's startup finished, successfully or not; false when <see cref="OnDemandWait"/> passed first.</summary>
    private async Task<bool> WaitForServerAsync(string workspace, string server, CancellationToken token)
    {
        if (!entries.TryGetValue(workspace + "\0" + server, out var entry) || !entry.Session.IsValueCreated) return true;
        var startup = entry.Session.Value;
        return await Task.WhenAny(startup, Task.Delay(OnDemandWait, token)) == startup;
    }

    private string Initializing(string server, TimeSpan elapsed, string detail)
        => $"{server} server initializing, {(int)elapsed.TotalSeconds} s elapsed (waits up to {(int)OnDemandWait.TotalSeconds} s per call); retry or wait. {detail}".TrimEnd();

    /// <summary>What a call that hit its deadline returns: the servers still starting, so the agent can wait instead of guessing.</summary>
    internal LanguageResult DeadlineExceeded(string workspace, TimeSpan elapsed, TimeSpan deadline)
    {
        var starting = new List<LanguageServerState>();
        foreach (var (key, entry) in entries)
            if (key.StartsWith(workspace + "\0", StringComparison.Ordinal) && !(entry.Session.IsValueCreated && entry.Session.Value.IsCompleted))
                starting.Add(new(key[(workspace.Length + 1)..], "", entry.State, entry.Detail, ""));
        return new("initializing", $"Server initializing or busy, {(int)elapsed.TotalSeconds} s elapsed; this call reached its {(int)deadline.TotalSeconds} s deadline and was cancelled. Retry or wait, and poll lsp_status operation=servers.")
        { Freshness = "pending", Servers = starting };
    }

    /// <summary>Counts one agent tool call against each server it used; a failed or cancelled call counts as a failure.</summary>
    private void RecordAgentUse(LanguageRequest request, LanguageResult? result)
    {
        if (!Path.IsPathFullyQualified(request.Workspace)) return;
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        var success = result is not null && result.State is not ("error" or "unavailable" or "disabled" or "unsupported" or "stopped" or "requiresAcceptance" or "expired");
        try
        {
            foreach (var server in ServersUsed(workspace, request, result).Distinct()) usage.Record(workspace, server, success);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private IEnumerable<string> ServersUsed(string workspace, LanguageRequest request, LanguageResult? result)
    {
        if (request.Server.Length > 0) return [request.Server];
        IEnumerable<string> paths = request.Paths is { Count: > 0 } ? request.Paths : request.Path.Length > 0 ? [request.Path] : result?.Files.Select(file => file.Path) ?? [];
        var ids = paths.Select(path => Detect(workspace, path)?.Id).OfType<string>().ToList();
        if (ids.Count == 0 && request.Operation is "workspaceSymbols" or "hover" or "definition" or "typeDefinition" or "implementation" or "references" or "symbols" or "symbolBody" && request.Path.Length == 0)
            ids.AddRange(DetectedServers(workspace));
        if (ids.Count == 0 && result is { Server.Length: > 0 }) ids.Add(result.Server);
        return ids;
    }

    /// <summary>The one line that tells an agent how to get a server that exists but is not serving: stopped, disabled or unsupported.</summary>
    private string Hint(LanguageRequest request, LanguageResult result)
    {
        var server = result.Server.Length > 0 ? result.Server : request.Path.Length > 0 && Path.IsPathFullyQualified(request.Workspace) ? Detect(request.Workspace, request.Path)?.Id ?? "" : "";
        var start = $"lsp_status {{\"operation\":\"retry\",\"server\":\"{server}\",\"workspace\":\"{request.Workspace}\"}}";
        if (result.State == "stopped" && server.Length > 0) return $"{server} is stopped; start it with {start}.";
        if (result.State == "disabled") return server.Length > 0 ? $"{server} is disabled in Settings > LSP; ask the user to enable it, then call {start}." : "Language services are disabled in Settings > LSP; ask the user to enable them.";
        if (result.State == "unsupported") return "No language server handles this file type; use text search for it.";
        var skipped = result.Files.FirstOrDefault(file => file.State == "skipped" && (file.Detail.Contains("stopped on request", StringComparison.Ordinal) || file.Detail.Contains("Disabled", StringComparison.Ordinal)));
        return skipped is null ? "" : $"{skipped.Path} was skipped: {skipped.Detail}";
    }
}