using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

public sealed partial class LanguageService
{
    private const int MaxDeltaFiles = 20;
    private const int MaxRememberedListings = 64;

    private long listingCounter;

    // A listing's id (before the dot of an action id) maps to the file it was listed for, so applying needs only the id.
    private readonly ConcurrentDictionary<string, (string Path, long Number)> listedPaths = new();

    /// <summary>
    /// An agent's code action, rename or format call. <paramref name="operation"/> is list (actions only), preview (the files and edits, nothing written)
    /// or apply (written to disk, with the diagnostics of the touched files before and after).
    /// </summary>
    internal async Task<LanguageResult> RefactorForAgentAsync(string tool, string operation, LanguageRequest request, CancellationToken token)
    {
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        request = request with { Workspace = workspace, Origin = LanguageLimits.AgentOrigin };
        if (tool == "rename" && request.NewPath.Length > 0) return await RenameFileAsync(operation, request, token);
        var number = Interlocked.Increment(ref listingCounter);
        if (tool == "codeActions" && operation == "list")
        {
            var id = "ca" + number;
            var listed = await ExecuteForAgentAsync(request with { Operation = "agentCodeActions", Id = id }, token);
            if (listed.AgentActions.Count > 0)
            {
                listedPaths[workspace + "\0" + id] = (request.Path, number);
                foreach (var stale in listedPaths.OrderBy(pair => pair.Value.Number).Take(Math.Max(0, listedPaths.Count - MaxRememberedListings)).Select(pair => pair.Key).ToArray()) listedPaths.TryRemove(stale, out _);
            }
            return listed;
        }
        LanguageResult prepared;
        var path = request.Path;
        if (tool == "codeActions")
        {
            var dot = request.Id.LastIndexOf('.');
            if (dot < 0 || !listedPaths.TryGetValue(workspace + "\0" + request.Id[..dot], out var known))
                return new("notFound", "Unknown or expired action id. List the actions again with lsp_code_actions.");
            path = known.Path;
            prepared = await ExecuteForAgentAsync(request with { Operation = "agentPrepare", Path = path }, token);
        }
        else prepared = await ExecuteForAgentAsync(request with { Operation = tool == "rename" ? "agentRename" : "agentFormat", Id = (tool == "rename" ? "rn" : "fm") + number }, token);
        if (operation != "apply" || prepared.State != "ready" || prepared.Plan.Length == 0) return prepared;

        // Commit routes through any existing file the edit touches, which belongs to the same server.
        path = prepared.Changes.FirstOrDefault(change => change.Change is "edited" or "renamed" or "deleted")?.Path ?? path;
        var edited = prepared.Changes.Where(change => change.Change == "edited").Select(change => change.Path).Take(MaxDeltaFiles).ToArray();
        var before = await DiagnosticsOfAsync(workspace, edited, token);
        var committed = await ExecuteAsync(request with { Operation = "agentCommit", Path = path, Id = prepared.Plan }, token);
        if (!committed.Applied) return committed;
        var touched = committed.Changes.Where(change => change.Change != "deleted").Select(change => change.Change == "renamed" ? change.NewPath : change.Path).Where(file => File.Exists(Path.Combine(workspace, file))).Take(MaxDeltaFiles).ToArray();
        var after = await DiagnosticsOfAsync(workspace, touched, token);
        return committed with { Delta = Delta(before, after) };
    }

    private async Task<LanguageResult?> DiagnosticsOfAsync(string workspace, string[] paths, CancellationToken token)
    {
        if (paths.Length == 0) return null;
        try { return await DiagnoseFilesAsync(new(workspace, Operation: "diagnostics", Scope: "files", Paths: paths, Origin: LanguageLimits.AgentOrigin), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string Key(LanguageItem item)
    {
        string? code = null;
        if (item.Raw.Length > 0) try { code = JsonNode.Parse(item.Raw)?["code"]?.ToString(); } catch (System.Text.Json.JsonException) { }
        return $"{item.Path}|{item.Kind}|{code}|{item.Text}";
    }

    private static LanguageDiagnosticDelta Delta(LanguageResult? before, LanguageResult? after)
    {
        if (after is null) return new([], [], 0, "unavailable");
        var remaining = (before?.Diagnostics ?? []).ToList();
        var added = new List<LanguageItem>();
        var unchanged = 0;
        foreach (var item in after.Diagnostics)
        {
            var match = remaining.FindIndex(old => Key(old) == Key(item));
            if (match < 0) added.Add(item);
            else { remaining.RemoveAt(match); unchanged++; }
        }
        return new(added, remaining, unchanged, before is { Freshness: "current" } || before is null ? after.Freshness : "partial");
    }
}