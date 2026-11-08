using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

public sealed partial class LanguageService
{
    /// <summary>Most files a glob selection checks in one call, like the reference tools; the rest is reported, never dropped silently.</summary>
    internal const int GlobFileCap = 20;

    /// <summary>How long a file check waits for diagnostics a push-only server has not sent yet, per server; a call's own timeout replaces it. Settable for checks.</summary>
    internal TimeSpan DiagnosticWait { get; set; } = TimeSpan.FromSeconds(12);

    private static bool IsGlob(string path) => path.AsSpan().IndexOfAny('*', '?', '{') >= 0;

    private static int SeverityRank(string name) => name.ToLowerInvariant() switch
    {
        "error" => 1,
        "warning" => 2,
        "information" or "info" => 3,
        "hint" => 4,
        _ => throw new ArgumentException("minSeverity is error, warning, information or hint.")
    };

    private async Task<LanguageResult> DiagnoseFilesAsync(LanguageRequest request, CancellationToken token)
    {
        var scope = request.Scope.Length == 0 ? "files" : request.Scope;
        if (scope is not ("files" or "workspace") || scope == "files" && request.Paths is not { Count: > 0 } ||
            scope == "workspace" && request.Paths is { Count: > 0 })
            throw new ArgumentException("Use scope files with paths, or scope workspace without paths.");
        var minimum = request.MinSeverity.Length == 0 ? 4 : SeverityRank(request.MinSeverity);
        if (request.Timeout is not 0 and (< 5 or > 300)) throw new ArgumentException("timeout is between 5 and 300 seconds.");
        var wait = request.Timeout > 0 ? TimeSpan.FromSeconds(request.Timeout) : DiagnosticWait;
        var notes = new List<string>();
        IEnumerable<string> paths;
        if (scope == "workspace") paths = (await ProjectServices.SearchablePathsAsync(request.Workspace, token)).Where(path => servers.Detect(path) is not null);
        else
        {
            // A path with a wildcard that is not itself a file selects the project's source files that match it, up to GlobFileCap in all.
            var literal = new List<string>();
            var matched = new SortedSet<string>(StringComparer.Ordinal);
            var searchable = (string[]?)null;
            foreach (var path in request.Paths!)
            {
                if (!IsGlob(path) || File.Exists(Path.Combine(request.Workspace, path))) { literal.Add(path); continue; }
                searchable ??= [.. (await ProjectServices.SearchablePathsAsync(request.Workspace, token)).Select(item => item.Replace('\\', '/'))];
                var pattern = LanguageSession.Glob(path.Replace('\\', '/').TrimStart('/'));
                var hits = searchable.Where(item => pattern.IsMatch(item) && servers.Detect(item) is not null).ToArray();
                if (hits.Length == 0) notes.Add($"The glob {path} matched no source file a language server owns.");
                foreach (var hit in hits) matched.Add(hit);
            }
            if (matched.Count > GlobFileCap) notes.Add($"The globs matched {matched.Count} files; the first {GlobFileCap} were checked. Narrow the glob or list paths to see the rest.");
            paths = literal.Concat(matched.Take(GlobFileCap));
        }
        // Validate the entire selection before sending any document to a server.
        var selection = paths.Select(path => scope == "files" ? Path.GetRelativePath(request.Workspace, ProjectServices.Contain(request.Workspace, path)) : path)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var files = new List<LanguageFileResult>();
        var diagnostics = new List<LanguageItem>();
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(2) + (request.Timeout > 0 ? wait : TimeSpan.Zero);
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
        // A server that declares workspace diagnostics reports every file in one request; files it lists need no per-file request.
        var reports = new Dictionary<string, Dictionary<string, LanguageItem[]>>();
        if (scope == "workspace")
            foreach (var id in starts.Keys.Where(id => !loading.Contains(id)))
            {
                if (!entries.TryGetValue(request.Workspace + "\0" + id, out var running) || !running.Session.IsValueCreated || !running.Session.Value.IsCompletedSuccessfully) continue;
                try { if (await running.Session.Value.Result.WorkspaceReportAsync(token) is { } report) reports[id] = report; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or OperationCanceledException) { notes.Add($"{id}: workspace/diagnostic failed ({error.Message}); checked file by file."); }
            }
        var waitLimits = new Dictionary<string, DateTime>();
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
                if (reports.TryGetValue(server.Id, out var report) && report.TryGetValue(path, out var reported))
                {
                    files.Add(new(path, "checked", "current", "") { Method = "workspace/diagnostic" });
                    diagnostics.AddRange(reported);
                    continue;
                }
                var session = await entry.Session.Value;
                var result = await session.ExecuteAsync(new(request.Workspace, path, "fileDiagnostics"), token);
                // Push-only servers report asynchronously after didOpen/didChange. Wait for them, bounded per server,
                // and retain explicit pending/unversioned coverage when they cannot confirm a version.
                if (result.Freshness == "pending" && !waitLimits.ContainsKey(server.Id)) waitLimits[server.Id] = DateTime.UtcNow + wait;
                while (result.Freshness == "pending" && waitLimits.TryGetValue(server.Id, out var waitUntil) && DateTime.UtcNow < waitUntil && DateTime.UtcNow < until)
                {
                    await Task.Delay(50, token);
                    result = await session.ExecuteAsync(new(request.Workspace, path, "fileDiagnostics"), token);
                }
                files.Add(new(path, result.Freshness == "current" ? "checked" : "pending", result.Freshness, result.Detail) { Method = result.Method });
                diagnostics.AddRange(result.Diagnostics);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { files.Add(new(path, "error", "unavailable", error.Message)); }
        }
        var hidden = diagnostics.RemoveAll(item => int.TryParse(item.Kind, out var rank) && rank > minimum);
        if (hidden > 0) notes.Add($"{hidden} diagnostic{(hidden == 1 ? "" : "s")} below minSeverity {request.MinSeverity} hidden.");
        var checkedCount = files.Count(file => file.State == "checked");
        return new(files.Any(file => file.State == "error") ? "error" : starts.Values.Any(start => start.State is "installing" or "initializing") || loading.Count > 0 ? "initializing" : "ready",
            $"Checked {checkedCount} of {files.Count} selected files; servers that were not running were started on demand." + (notes.Count > 0 ? " " + string.Join(" ", notes) : ""))
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