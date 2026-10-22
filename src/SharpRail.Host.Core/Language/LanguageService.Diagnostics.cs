using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

public sealed partial class LanguageService
{
    private async Task<LanguageResult> DiagnoseFilesAsync(LanguageRequest request, CancellationToken token)
    {
        var scope = request.Scope.Length == 0 ? "files" : request.Scope;
        if (scope is not ("files" or "workspace") || scope == "files" && request.Paths is not { Count: > 0 } ||
            scope == "workspace" && request.Paths is { Count: > 0 })
            throw new ArgumentException("Use scope files with paths, or scope workspace without paths.");
        var paths = scope == "workspace"
            ? (await ProjectServices.SearchablePathsAsync(request.Workspace, token)).Where(path => servers.Detect(path) is not null)
            : request.Paths!;
        // Validate the entire selection before sending any document to a server.
        var selection = paths.Select(path => scope == "files" ? Path.GetRelativePath(request.Workspace, ProjectServices.Contain(request.Workspace, path)) : path)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var files = new List<LanguageFileResult>();
        var diagnostics = new List<LanguageItem>();
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        // Every needed server starts now, in parallel, so no file is skipped merely because its server was stopped.
        var starts = new Dictionary<string, LanguageResult>();
        var owners = new Dictionary<string, string>();
        foreach (var path in selection)
        {
            try { ProjectServices.Contain(request.Workspace, path); }
            catch (UnauthorizedAccessException) { continue; }
            if (Detect(request.Workspace, path) is { } owner) owners.TryAdd(owner.Id, path);
        }
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var started = await Task.WhenAll(owners.Select(async pair => (pair.Key, Result: await EnsureStartedAsync(request.Workspace, pair.Value, request.Origin, token))));
        foreach (var (id, result) in started) starts[id] = result;
        // A file checked while its project graph loads could report types that are merely not loaded yet; wait for the graph, bounded.
        var loading = new HashSet<string>();
        foreach (var id in starts.Keys)
            if (!await WaitForProjectsAsync(request.Workspace, id, OnDemandWait - waited.Elapsed, token)) loading.Add(id);
        foreach (var path in selection)
        {
            token.ThrowIfCancellationRequested();
            try { ProjectServices.Contain(request.Workspace, path); }
            catch (UnauthorizedAccessException error)
            {
                files.Add(new(path, "skipped", "unavailable", error.Message));
                continue;
            }
            var server = Detect(request.Workspace, path);
            if (server is null)
            {
                files.Add(new(path, "skipped", "unavailable", "No language server supports this file."));
                continue;
            }
            if (!entries.TryGetValue(request.Workspace + "\0" + server.Id, out var entry) ||
                !entry.Session.IsValueCreated || !entry.Session.Value.IsCompletedSuccessfully)
            {
                var start = starts.GetValueOrDefault(server.Id);
                if (start?.State is "installing" or "initializing") files.Add(new(path, "pending", "pending", start.Detail));
                else files.Add(new(path, "skipped", "unavailable", start is null || start.Detail.Length == 0 ? $"{server.Id} could not be started. Check lsp_status operation=servers." : $"{server.Id}: {start.Detail}"));
                continue;
            }
            if (DateTime.UtcNow >= until)
            {
                files.Add(new(path, "pending", "pending", "Scan time limit reached. Retry this file using paths."));
                continue;
            }
            try
            {
                var session = await entry.Session.Value;
                var result = await session.ExecuteAsync(new(request.Workspace, path, "fileDiagnostics"), token);
                // Push-only servers report asynchronously after didOpen/didChange. Bound the wait
                // and retain explicit pending/unversioned coverage when they cannot confirm a version.
                var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                while (result.Freshness == "pending" && DateTime.UtcNow < waitUntil && DateTime.UtcNow < until)
                {
                    await Task.Delay(50, token);
                    result = await session.ExecuteAsync(new(request.Workspace, path, "fileDiagnostics"), token);
                }
                files.Add(new(path, result.Freshness == "current" ? "checked" : "pending", result.Freshness, result.Detail));
                diagnostics.AddRange(result.Diagnostics);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { files.Add(new(path, "error", "unavailable", error.Message)); }
        }
        var checkedCount = files.Count(file => file.State == "checked");
        return new(files.Any(file => file.State == "error") ? "error" : starts.Values.Any(start => start.State is "installing" or "initializing") || loading.Count > 0 ? "initializing" : "ready",
            $"Checked {checkedCount} of {files.Count} selected files; servers that were not running were started on demand.")
        {
            Files = files,
            Diagnostics = diagnostics.Distinct().ToArray(),
            Freshness = files.Count == 0 ? "unavailable" : checkedCount == files.Count ? "current" : "partial",
            Coverage = scope == "workspace"
                ? "Project source files (Git tracked and non-ignored untracked files, or visible files outside Git); see per-file coverage. This is LSP analysis, not a compiler/build check."
                : "Explicit file selection; see per-file coverage. Active editor drafts take precedence over disk."
        };
    }
}