using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

namespace SharpRail.Host.Core;

/// <summary>
/// Project-level language configuration from <c>.sharprail/language.toml</c> in the project root (the Default
/// checkout, so every workspace of the project agrees), merged under host state. Files are read lazily on first
/// use, off the UI thread, and re-read when they change; nothing is written except by an explicit save.
/// </summary>
public sealed partial class HostStateStore
{
    private sealed class FileWatch(ProjectLanguageFile.Snapshot snapshot)
    {
        public ProjectLanguageFile.Snapshot Snapshot = snapshot;
        public List<FileSystemWatcher> Watchers = [];
        public Timer? Debounce;
        public Func<string, string, FileSystemWatcher?>? Create;
    }

    private readonly Dictionary<string, FileWatch> languageFiles = [];
    private readonly Lock languageFileGate = new();

    private string? ProjectOfWorkspace(string workspace)
    {
        lock (gate) return state.Workspaces.FirstOrDefault(item => item.Path == workspace)?.ProjectRoot;
    }

    /// <summary>The project's current file snapshot; starts watching the project's file on first use.</summary>
    internal ProjectLanguageFile.Snapshot ProjectLanguage(string project)
    {
        var path = ProjectLanguageFile.PathOf(project);
        FileWatch watch;
        lock (languageFileGate)
        {
            if (!languageFiles.TryGetValue(project, out watch!))
            {
                languageFiles[project] = watch = new(ProjectLanguageFile.Read(project));
                StartLanguageFileWatchers(project, watch);
                PublishLanguageFile(project, watch.Snapshot);
            }
        }
        // Stamp check keeps correctness even where file watching is unavailable.
        var info = new FileInfo(path);
        var stamp = info.Exists ? info.Length + "-" + info.LastWriteTimeUtc.Ticks : "";
        if (stamp != watch.Snapshot.Stamp && watch.Snapshot.Stamp != "unreadable" || info.Exists != watch.Snapshot.Exists) return ReloadLanguageFile(project);
        return watch.Snapshot;
    }

    private ProjectLanguageFile.Snapshot ReloadLanguageFile(string project)
    {
        var next = ProjectLanguageFile.Read(project);
        lock (languageFileGate)
        {
            if (!languageFiles.TryGetValue(project, out var watch)) return next;
            var changed = watch.Snapshot.ToJson() != next.ToJson();
            watch.Snapshot = next;
            if (changed) PublishLanguageFile(project, next);
        }
        return next;
    }

    private void PublishLanguageFile(string project, ProjectLanguageFile.Snapshot snapshot)
    {
        lock (gate)
        {
            var files = state.ProjectLanguageFiles.ToDictionary();
            files[project] = snapshot.ToJson();
            Publish(state with { ProjectLanguageFiles = files }, persist: false);
        }
    }

    private void StartLanguageFileWatchers(string project, FileWatch watch)
    {
        void Changed(object? sender, FileSystemEventArgs e)
        {
            if (e.Name is not null && !e.Name.StartsWith(".sharprail", StringComparison.Ordinal) && !e.Name.StartsWith("language.toml", StringComparison.Ordinal)) return;
            lock (languageFileGate)
            {
                watch.Debounce?.Dispose();
                watch.Debounce = new Timer(_ =>
                {
                    try { ReloadLanguageFile(project); EnsureInnerWatcher(project, watch); }
                    catch (Exception error) { System.Diagnostics.Trace.WriteLine("Language file refresh: " + error.Message); }
                }, null, 150, Timeout.Infinite);
            }
        }
        FileSystemWatcher? Create(string directory, string filter)
        {
            try
            {
                if (!Directory.Exists(directory)) return null;
                var created = new FileSystemWatcher(directory, filter) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                created.Created += Changed; created.Changed += Changed; created.Deleted += Changed;
                created.Renamed += (_, e) => Changed(null, e);
                created.EnableRaisingEvents = true;
                return created;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
        watch.Create = Create;
        if (Create(project, ".sharprail") is { } outer) watch.Watchers.Add(outer);
        EnsureInnerWatcher(project, watch);
    }

    private void EnsureInnerWatcher(string project, FileWatch watch)
    {
        lock (languageFileGate)
        {
            if (watch.Watchers.Count > 1 || watch.Create is not { } create) return;
            if (create(Path.Combine(project, ".sharprail"), "language.toml*") is { } inner) watch.Watchers.Add(inner);
        }
    }

    private void StopLanguageFileWatchers()
    {
        lock (languageFileGate)
            foreach (var watch in languageFiles.Values)
            {
                watch.Debounce?.Dispose();
                foreach (var watcher in watch.Watchers) watcher.Dispose();
                watch.Watchers.Clear();
            }
    }

    internal ValueTask<LanguageResult> ProjectLanguageFileAsync(LanguageRequest request, CancellationToken token)
    {
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        return new(Task.Run(() => request.Operation == "saveProjectLanguageFile"
            ? SaveProjectLanguageFile(workspace, request.Server, request.Configuration ?? throw new ArgumentException("Supply the configuration to share."))
            : ProjectLanguageFileStatus(workspace, request.Server), token));
    }

    /// <summary>What the Settings page shows about one server in one workspace: the file, and where its values come from.</summary>
    internal LanguageResult ProjectLanguageFileStatus(string workspace, string server)
    {
        var project = ProjectOfWorkspace(workspace) ?? throw new ArgumentException("Choose a registered project workspace.");
        var snapshot = ProjectLanguage(project);
        var status = JsonNode.Parse(snapshot.ToJson())!.AsObject();
        string origin = "default";
        if (server.Length > 0 && snapshot.Servers.ContainsKey(server))
        {
            var local = LocalWorkspaceLanguage(workspace, server);
            origin = ProjectLanguageFile.Portable.Any(key => local.ContainsKey(key)) ? "local" : "file";
        }
        else if (server.Length > 0 && LocalWorkspaceLanguage(workspace, server).Count > 0) origin = "local";
        status["origin"] = origin;
        return new("ready") { Configuration = status.ToJsonString() };
    }

    private JsonObject LocalWorkspaceLanguage(string workspace, string server)
    {
        lock (gate)
            return JsonNode.Parse(state.WorkspaceLanguages.GetValueOrDefault(workspace) ?? "{}")?[server] as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Explicit Settings save: writes the portable part of one server's options to the project's file and drops
    /// those keys from the host-state entries of the project's workspaces so the file is authoritative. Host-specific
    /// options (installation path, project JDK) stay in host state.
    /// </summary>
    internal LanguageResult SaveProjectLanguageFile(string workspace, string server, string configuration)
    {
        if (LanguageServers.Catalog.All(item => item.Id != server)) throw new ArgumentException("Unknown language server.");
        var project = ProjectOfWorkspace(workspace) ?? throw new ArgumentException("Choose a registered project workspace.");
        var options = WorkspaceLanguageConfiguration.Parse(configuration, server);
        ProjectLanguageFile.Save(project, server, ProjectLanguageFile.PortableOf(options));
        ReloadLanguageFile(project);
        var local = JsonNode.Parse(WorkspaceLanguageConfiguration.Serialize(options))!.AsObject();
        lock (gate)
        {
            var configurations = state.WorkspaceLanguages.ToDictionary();
            foreach (var item in state.Workspaces.Where(item => item.ProjectRoot == project))
            {
                var servers = JsonNode.Parse(configurations.GetValueOrDefault(item.Path) ?? "{}")!.AsObject();
                var kept = new JsonObject();
                var existing = servers[server] as JsonObject;
                foreach (var key in ProjectLanguageFile.Local)
                {
                    var value = item.Path == workspace ? local[key] : existing?[key];
                    if (value is not null && (item.Path != workspace || !JsonNode.DeepEquals(value, JsonNode.Parse(WorkspaceLanguageConfiguration.Serialize(new()))![key])))
                        kept[key] = value.DeepClone();
                }
                if (existing is null && item.Path != workspace) continue;
                if (kept.Count > 0) servers[server] = kept; else servers.Remove(server);
                configurations[item.Path] = servers.ToJsonString();
            }
            Publish(state with { WorkspaceLanguages = configurations }, persist: true);
        }
        return new("ready") { Configuration = WorkspaceLanguage(workspace, server) };
    }
}