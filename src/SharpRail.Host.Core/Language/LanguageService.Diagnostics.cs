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
            ? (await ProjectServices.SearchablePathsAsync(request.Workspace, token)).Where(path => LanguageServers.Detect(path) is not null)
            : request.Paths!;
        // Validate the entire selection before sending any document to a server.
        var selection = paths.Select(path => scope == "files" ? Path.GetRelativePath(request.Workspace, ProjectServices.Contain(request.Workspace, path)) : path)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var files = new List<LanguageFileResult>();
        var diagnostics = new List<LanguageItem>();
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(2);
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
                files.Add(new(path, "skipped", "unavailable", $"{server.Id} is not running. Start it with lsp_status operation=retry and server={server.Id}, or in the LSP pane, then retry."));
                continue;
            }
            if (DateTime.UtcNow >= until)
            {
                files.Add(new(path, "pending", "pending", "Scan time limit reached. Retry this file using paths."));
                continue;
            }
            try
            {
                entry.Used = DateTime.UtcNow;
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
        return new(files.Any(file => file.State == "error") ? "error" : "ready",
            $"Checked {checkedCount} of {files.Count} selected files using running language servers.")
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