using System.Text.Json.Nodes;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core.Language;

public sealed partial class LanguageService
{
    /// <summary>Most files a directory rename may carry; a larger directory is refused whole.</summary>
    private const int MaxRenamePairs = 1000;

    /// <summary>The running sessions of a workspace, by server id; only the named server when <paramref name="only"/> is not empty.</summary>
    private List<(string Id, LanguageSession Session)> Running(string workspace, string only = "")
    {
        var running = new List<(string, LanguageSession)>();
        foreach (var (key, entry) in entries.ToArray())
        {
            if (!key.StartsWith(workspace + "\0", StringComparison.Ordinal)) continue;
            var id = key[(workspace.Length + 1)..];
            if (only.Length > 0 && id != only) continue;
            if (entry.Session.IsValueCreated && entry.Session.Value.IsCompletedSuccessfully && !entry.Session.Value.Result.Exited) running.Add((id, entry.Session.Value.Result));
        }
        return running;
    }

    /// <summary>A capabilities dump or raw request that names a server instead of a file; it never starts one.</summary>
    private async Task<LanguageResult> ServerScopedAsync(string workspace, LanguageRequest request, CancellationToken token)
    {
        if (request.Server.Length > 0 && servers.Find(request.Server) is null) throw new ArgumentException("Unknown language server.");
        if (request.Operation == "rawRequest" && request.Server.Length == 0)
            throw new ArgumentException("Pass server (the id of a running server, see lsp_status operation=servers) or path of a file it owns. A raw request goes to one server.");
        var running = Running(workspace, request.Server);
        if (running.Count == 0)
            return new("unavailable", request.Server.Length > 0 ? $"{request.Server} is not running in this workspace. Pass path of a file it owns to start it on demand, or start it with lsp_status operation=retry." : "No language server is running in this workspace. Pass path of a file to start its server on demand.") { Freshness = "unavailable" };
        if (request.Operation == "rawRequest") return await running[0].Session.ExecuteAsync(request, token);
        var document = new JsonObject();
        foreach (var (id, session) in running)
        {
            var one = await session.ExecuteAsync(request, token);
            document[id] = JsonNode.Parse(one.Json);
        }
        return new("ready", $"Capabilities of {running.Count} running server{(running.Count == 1 ? "" : "s")}: {string.Join(", ", running.Select(item => item.Id))}.") { Json = document.ToJsonString(), Freshness = "current" };
    }

    private static string? RelativeOf(string workspace, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        var path = Path.GetRelativePath(workspace, parsed.LocalPath);
        return path == ".." || path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : path;
    }

    /// <summary>
    /// An agent's file or directory rename: every running server that asked for the move is consulted (workspace/willRenameFiles), the edits it returns
    /// are validated whole, and only then is anything written. Apply writes the edits, moves the path and rolls the edits back if the move fails.
    /// </summary>
    private async Task<LanguageResult> RenameFileAsync(string operation, LanguageRequest request, CancellationToken token)
    {
        var workspace = request.Workspace;
        string oldFull, newFull;
        try
        {
            oldFull = ProjectServices.Contain(workspace, request.Path, write: true);
            newFull = ProjectServices.Contain(workspace, request.NewPath, write: true);
        }
        catch (UnauthorizedAccessException error) { return new("unsupported", $"Both paths must stay inside the workspace: {error.Message}") { Freshness = "current" }; }
        var oldRel = Path.GetRelativePath(workspace, oldFull);
        var newRel = Path.GetRelativePath(workspace, newFull);
        var directory = Directory.Exists(oldFull);
        if (oldFull == newFull) return new("notApplicable", "The new path is the old path.") { Freshness = "current" };
        if (!directory && !File.Exists(oldFull)) return new("notFound", $"{oldRel} does not exist.") { Freshness = "current" };
        // On a case-insensitive disk a case-only rename finds its own destination; that is the same entry, not a collision.
        if (!string.Equals(oldFull, newFull, StringComparison.OrdinalIgnoreCase) && (File.Exists(newFull) || Directory.Exists(newFull)))
            return new("conflict", $"{newRel} already exists. Choose another path or move it first.") { Freshness = "current" };
        if (directory && newFull.StartsWith(oldFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return new("notApplicable", "A directory cannot be moved into itself.") { Freshness = "current" };

        var pairs = new List<RenamePair> { new(new Uri(oldFull).AbsoluteUri, new Uri(newFull).AbsoluteUri, oldFull, directory) };
        if (directory)
        {
            var files = Directory.EnumerateFiles(oldFull, "*", SearchOption.AllDirectories).Take(MaxRenamePairs + 1).ToArray();
            if (files.Length > MaxRenamePairs) return new("unsupported", $"{oldRel} holds more than {MaxRenamePairs} files; move it in smaller parts.") { Freshness = "current" };
            pairs.AddRange(files.Select(file => new RenamePair(new Uri(file).AbsoluteUri, new Uri(Path.Combine(newFull, Path.GetRelativePath(oldFull, file))).AbsoluteUri, file, false)));
        }

        // The owners of the moved files must be running to take part. A graph still loading could return a partial import list, so wait for it.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var started = new HashSet<string>();
        foreach (var pair in pairs.Where(pair => !pair.Directory).Take(MaxRenamePairs).Prepend(pairs[0]))
        {
            var rel = Path.GetRelativePath(workspace, pair.OldFull);
            if (Detect(workspace, rel) is not { } owner || !started.Add(owner.Id)) continue;
            var start = await EnsureStartedAsync(workspace, rel, request.Origin, token);
            if (start.State is "installing" or "initializing" || !await WaitForProjectsAsync(workspace, owner.Id, OnDemandWait - waited.Elapsed, token))
                return new("initializing", $"{owner.Id} is still starting or loading its project graph, so its import updates could be incomplete; nothing was changed. Call again.") { Freshness = "pending" };
        }
        var sessions = Running(workspace);

        foreach (var (id, session) in sessions)
        {
            var unsaved = await session.UnsavedUnderAsync(oldFull, token);
            if (unsaved.Length > 0) return new("conflict", $"{unsaved[0]} has unsaved changes in an editor. Save or close it first.") { Freshness = "current" };
        }
        foreach (var draft in drafts.Where(pair => pair.Key.StartsWith(workspace + "\0", StringComparison.Ordinal)).SelectMany(pair => pair.Value))
            if (draft.Dirty && Path.GetFullPath(Path.Combine(workspace, draft.Path)) is var draftFull && (draftFull == oldFull || directory && draftFull.StartsWith(oldFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                return new("conflict", $"{draft.Path} has unsaved changes in an editor. Save or close it first.") { Freshness = "current" };

        // Ask every server first; any failure other than "no such method" ends the call before a byte is written.
        var edits = new List<JsonNode>();
        var took = new List<string>();
        foreach (var (id, session) in sessions)
        {
            try
            {
                var answer = await session.WillRenameAsync(pairs, token);
                if (!answer.Took) continue;
                took.Add(id);
                if (answer.Edit is { } edit) edits.Add(edit);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException && !token.IsCancellationRequested)
            {
                return new("error", $"{id} failed workspace/willRenameFiles ({error.Message}); nothing was changed.") { Freshness = "unavailable", Server = id };
            }
        }

        var snapshots = new List<Dictionary<string, WorkspaceEdits.Open>>();
        foreach (var (_, session) in sessions) snapshots.Add(await session.OpenSnapshotAsync(token));
        WorkspaceEdits.Open? OpenOf(string uri) => snapshots.Select(snapshot => snapshot.GetValueOrDefault(uri)).OfType<WorkspaceEdits.Open>().OrderByDescending(open => open.Dirty).FirstOrDefault();
        EditPlan plan;
        try { plan = WorkspaceEdits.Build(workspace, $"Rename {oldRel} to {newRel}", edits, uri => RelativeOf(workspace, uri), OpenOf); }
        catch (EditRefused refused) { return new(refused.State, refused.Message) { Freshness = "current" }; }

        var move = new LanguageChange(oldRel, "renamed", 0, newRel);
        var changes = new[] { move }.Concat(plan.Changes()).ToArray();
        var noun = directory ? $"directory ({pairs.Count - 1} files)" : "file";
        var note = took.Count == 0
            ? " No running language server declared workspace/willRenameFiles for these paths, so only the path moves and imports may need updating by hand."
            : $" Servers that took part: {string.Join(", ", took)}.";
        var preview = new LanguageResult("ready", $"Rename {noun} {oldRel} to {newRel}.{note}") { Changes = changes, Freshness = "current", Server = string.Join(", ", took) };
        if (operation != "apply") return preview;

        bool Inside(string rel) => rel == oldRel || directory && rel.StartsWith(oldRel + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var edited = plan.Changes().Where(change => change.Change == "edited").Select(change => change.Path).ToArray();
        var before = await DiagnosticsOfAsync(workspace, [.. edited.Where(path => !Inside(path)).Take(MaxDeltaFiles)], token);
        try { WorkspaceEdits.Commit(plan); }
        catch (EditRefused refused) { return new(refused.State, refused.Message) { Freshness = "current" }; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
            if (directory) Directory.Move(oldFull, newFull); else File.Move(oldFull, newFull);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var failed = WorkspaceEdits.Revert(plan);
            return new("failed", $"Moving {oldRel} failed ({error.Message}); " + (failed.Length == 0 ? "the edits were rolled back and nothing changed." : "rolling back some edits failed: " + string.Join("; ", failed))) { Freshness = "current" };
        }
        foreach (var (_, session) in sessions)
        {
            try { await session.AfterRenameAsync(pairs, plan.Files.Where(file => file.Changed && file.Text is not null).Select(file => file.Full), token); }
            catch (Exception error) when (error is IOException or OperationCanceledException && !token.IsCancellationRequested) { }
        }
        var touched = edited.Select(path => Inside(path) ? Path.Combine(newRel, Path.GetRelativePath(oldRel, path)) : path).Where(path => File.Exists(Path.Combine(workspace, path))).Take(MaxDeltaFiles).ToArray();
        var after = await DiagnosticsOfAsync(workspace, touched, token);
        return preview with { Applied = true, Delta = Delta(before, after) };
    }
}