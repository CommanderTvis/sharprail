using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core;

/// <summary>
/// The host's shared state: persisted to <c>state.json</c> in its directory (or kept in memory
/// without one) and published as complete snapshots to every watcher.
/// </summary>
public sealed partial class HostStateStore : IHostStateService
{
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();

    public const string FileName = "state.json";

    private sealed class Stored
    {
        public HostSettings Settings { get; set; } = new();
        public List<LayoutPreset> Presets { get; set; } = [];
        public List<string> Projects { get; set; } = [];
        public List<string> RecentProjects { get; set; } = [];
        public List<ProjectRecord> ProjectRecords { get; set; } = [];
        public Dictionary<string, string> WorkspaceLabels { get; set; } = [];
        public Dictionary<string, string> WorkspaceBases { get; set; } = [];
        public Dictionary<string, string> WorkspaceDiffBases { get; set; } = [];
        public List<WorkspaceRecord> Workspaces { get; set; } = [];
        public Dictionary<string, JsonElement> PluginSettings { get; set; } = [];
        public List<string> PluginPaths { get; set; } = [];
        public List<TerminalAgent> TerminalAgents { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock gate = new();
    private readonly string? path;
    internal string? DirectoryPath => path is null ? null : Path.GetDirectoryName(path);
    private readonly List<Channel<HostState>> watchers = [];
    // Settings a newer host wrote that this one does not know; written back untouched.
    private readonly Dictionary<string, JsonNode?> unknownSettings = [];
    private static readonly HashSet<string> KnownSettings = typeof(HostSettings).GetProperties().Select(property => property.Name).ToHashSet();
    private HostState state;

    public string? LastError { get; private set; }
    /// <summary>Raised with the path of a workspace the host removed, so its other resources can end with it.</summary>
    public event Action<string>? WorkspaceRemoved;

    /// <summary>The state directory's identity; null for a memory-only store or an unwritable directory.</summary>
    public string? InstallationId { get; }

    public ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HostHandshake(HostProtocol.Current, HostProtocol.BuildVersion));
    /// <summary>
    /// Normalizes a merged plugin settings namespace before it is persisted, or throws to reject the whole batch.
    /// The plugin runtime registers it; without one, namespaces merge unvalidated.
    /// </summary>
    public Func<string, JsonElement, JsonElement>? PluginNamespaceValidator { get; set; }

    /// <summary>Every transitive dependent of a plugin, so turning it off turns them off in the same batch.</summary>
    public Func<string, IReadOnlyList<string>>? PluginDependents { get; set; }

    /// <summary>Opens the store; when the directory has no state file yet, <paramref name="seed"/> supplies migrated state.</summary>
    public HostStateStore(string? directory, Func<HostState>? seed = null)
    {
        path = directory is null ? null : Path.Combine(directory, FileName);
        HostState initial;
        try
        {
            if (path is not null && File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var stored = JsonSerializer.Deserialize<Stored>(text, Json) ?? new();
                if (JsonNode.Parse(text)?[nameof(Stored.Settings)] is JsonObject settings)
                    foreach (var (name, value) in settings)
                        if (!KnownSettings.Contains(name)) unknownSettings[name] = value?.DeepClone();
                initial = new()
                {
                    Settings = stored.Settings ?? new(),
                    Presets = stored.Presets ?? [],
                    Projects = stored.Projects ?? [],
                    RecentProjects = stored.RecentProjects ?? [],
                    ProjectRecords = stored.ProjectRecords ?? [],
                    WorkspaceLabels = stored.WorkspaceLabels ?? [],
                    WorkspaceBases = stored.WorkspaceBases ?? [],
                    WorkspaceDiffBases = stored.WorkspaceDiffBases ?? [],
                    Workspaces = stored.Workspaces ?? [],
                    PluginSettings = stored.PluginSettings ?? [],
                    PluginPaths = stored.PluginPaths ?? [],
                    // Sessions end with the app, so a record whose workspace folder is gone can never resume; plugins
                    // that follow every recorded workspace would otherwise read and watch folders that no longer exist.
                    TerminalAgents = (stored.TerminalAgents ?? []).Where(agent => agent?.Terminal?.WorkspaceId is { } workspace && Directory.Exists(workspace)).ToList()
                };
            }
            else initial = seed?.Invoke() ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            initial = new(); LastError = error.Message;
        }
        state = Normalize(initial);
        // Identities minted for a file that predates them are written at once, or they would change on the next launch.
        if (path is not null && LastError is null && (!File.Exists(path) || !state.ProjectRecords.SequenceEqual(initial.ProjectRecords))) Save(state);
        if (directory is not null)
            try { InstallationId = Installation.EnsureIn(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError ??= error.Message; }
    }

    public HostState Current { get { lock (gate) return state; } }

    public ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);

    public ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var next = state;
            foreach (var change in changes) next = Apply(next, change);
            if (!ReferenceEquals(next, state)) next = next with { ProjectRecords = Records(next, next.ProjectRecords) };
            // A client that predates theme modes sends only the theme and means it to take effect.
            if (next.Settings.ThemeMode != "fixed" && changes.Any(change => change is { Kind: "setting", Key: "theme" }) &&
                !changes.Any(change => change is { Kind: "setting", Key: "theme-mode" }))
                next = next with { Settings = next.Settings with { ThemeMode = "fixed" } };
            return ValueTask.FromResult(Publish(next, persist: true));
        }
    }

    private static IReadOnlyDictionary<string, string> Without(IReadOnlyDictionary<string, string> entries, string key) =>
        entries.ContainsKey(key) ? entries.Where(entry => entry.Key != key).ToDictionary() : entries;

    /// <summary>Publishes the plugin roster the runtime reconciled; it rides every later snapshot and is never persisted.</summary>
    public void PublishPlugins(IReadOnlyList<PluginRosterEntry> roster)
    {
        lock (gate)
        {
            if (JsonSerializer.Serialize(roster, PluginJson.Options) == JsonSerializer.Serialize(state.Plugins, PluginJson.Options)) return;
            Publish(state with { Plugins = roster.ToArray() }, persist: false);
        }
    }

    /// <summary>Sets or clears one terminal's agent record.</summary>
    public void SetTerminalAgent(TerminalRef terminal, TerminalAgentRecord? record)
    {
        lock (gate)
        {
            var agents = state.TerminalAgents.Where(agent => agent.Terminal != terminal).ToList();
            if (record is not null) agents.Add(new(terminal, record));
            if (agents.SequenceEqual(state.TerminalAgents)) return;
            Publish(state with { TerminalAgents = agents }, persist: true);
        }
    }

    /// <summary>Drops the agent records of terminals that closed and returns them.</summary>
    public IReadOnlyList<TerminalRef> RemoveTerminalAgents(Func<TerminalRef, bool> closed)
    {
        lock (gate)
        {
            var removed = state.TerminalAgents.Where(agent => closed(agent.Terminal)).Select(agent => agent.Terminal).ToArray();
            if (removed.Length > 0) Publish(state with { TerminalAgents = state.TerminalAgents.Where(agent => !closed(agent.Terminal)).ToArray() }, persist: true);
            return removed;
        }
    }

    /// <summary>Replaces one plugin settings namespace as is, for a namespace found invalid once its plugin's contract loads.</summary>
    public void ReplacePluginSettings(string id, JsonElement value)
    {
        lock (gate)
        {
            var namespaces = state.PluginSettings.ToDictionary();
            namespaces[id] = JsonSerializer.SerializeToElement(value);
            Publish(state with { PluginSettings = namespaces }, persist: true);
        }
    }

    public async IAsyncEnumerable<HostState> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<HostState>(new() { SingleReader = true });
        lock (gate) { watchers.Add(channel); channel.Writer.TryWrite(state); }
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                // Snapshots are complete; a slow watcher only needs the latest.
                var latest = default(HostState);
                while (channel.Reader.TryRead(out var item)) latest = item;
                if (latest is not null) yield return latest;
            }
        }
        finally { lock (gate) watchers.Remove(channel); }
    }

    private HostState Publish(HostState next, bool persist)
    {
        if (ReferenceEquals(next, state)) return state;
        var previous = state;
        state = next with { Revision = state.Revision + 1 };
        if (persist) Save(state);
        foreach (var watcher in watchers) watcher.Writer.TryWrite(state);
        PublishLifecycle(previous, state);
        return state;
    }

    private void Save(HostState snapshot)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stored = new Stored
            {
                Settings = snapshot.Settings,
                Presets = snapshot.Presets.ToList(),
                Projects = snapshot.Projects.ToList(),
                RecentProjects = snapshot.RecentProjects.ToList(),
                ProjectRecords = snapshot.ProjectRecords.ToList(),
                WorkspaceLabels = snapshot.WorkspaceLabels.ToDictionary(),
                WorkspaceBases = snapshot.WorkspaceBases.ToDictionary(),
                WorkspaceDiffBases = snapshot.WorkspaceDiffBases.ToDictionary(),
                Workspaces = snapshot.Workspaces.ToList(),
                PluginSettings = snapshot.PluginSettings.ToDictionary(),
                PluginPaths = snapshot.PluginPaths.ToList(),
                TerminalAgents = snapshot.TerminalAgents.ToList()
            };
            var temporary = path + ".tmp";
            var document = JsonSerializer.SerializeToNode(stored, Json)!;
            foreach (var (name, value) in unknownSettings) document[nameof(Stored.Settings)]![name] = value?.DeepClone();
            File.WriteAllText(temporary, document.ToJsonString(Json));
            File.Move(temporary, path, overwrite: true);
            LastError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
    }

    private static bool ValidPath(string value) => value.Length > 0 && !value.Contains('\0') && Path.IsPathFullyQualified(value);

    private static bool ValidText(string value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');

    private static HostState Normalize(HostState value)
    {
        var light = Clean(value.Settings.SystemLight); var dark = Clean(value.Settings.SystemDark);
        // Half a system pair is malformed: the client falls back to its own default pair.
        if (light.Length == 0 || dark.Length == 0) light = dark = "";
        var normalized = value with
        {
            Revision = 0,
            Settings = value.Settings with
            {
                ThemeMode = value.Settings.ThemeMode is "system" ? "system" : "fixed",
                Theme = Clean(value.Settings.Theme),
                SystemLight = light,
                SystemDark = dark,
                FileLineWidth = Width(value.Settings.FileLineWidth),
                MarkdownLineWidth = Width(value.Settings.MarkdownLineWidth),
                TerminalReplayKb = Math.Clamp(value.Settings.TerminalReplayKb, 0, HostSettings.MaxTerminalReplayKb)
            },
            Presets = value.Presets.Where(preset => preset is not null && ValidText(preset.Name) && preset.Layout is { Length: > 0 })
                .DistinctBy(preset => preset.Name).ToArray(),
            Projects = value.Projects.Where(item => item is not null && ValidPath(item)).Distinct().ToArray(),
            RecentProjects = value.RecentProjects.Where(item => item is not null && ValidPath(item)).Distinct().Take(HostStateChange.RecentLimit).ToArray(),
            WorkspaceLabels = value.WorkspaceLabels.Where(entry => ValidPath(entry.Key) && entry.Value is not null && ValidText(entry.Value)).ToDictionary(),
            WorkspaceBases = value.WorkspaceBases.Where(entry => ValidPath(entry.Key) && entry.Value is not null && GitRefs.IsSafe(entry.Value)).ToDictionary(),
            WorkspaceDiffBases = value.WorkspaceDiffBases.Where(entry => ValidPath(entry.Key) && entry.Value is not null && GitRefs.IsSafe(entry.Value) &&
                value.WorkspaceBases.GetValueOrDefault(entry.Key) != entry.Value).ToDictionary(),
            PluginSettings = value.PluginSettings.Where(entry => PluginIdentity.IsPluginId(entry.Key) && entry.Value.ValueKind == JsonValueKind.Object &&
                (!entry.Value.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is JsonValueKind.True or JsonValueKind.False))
            .ToDictionary(entry => entry.Key, entry => JsonSerializer.SerializeToElement(entry.Value)),
            PluginPaths = value.PluginPaths.Where(item => item is not null && ValidPath(item)).Distinct().ToArray(),
            Plugins = [],
            TerminalAgents = value.TerminalAgents.Where(agent => agent?.Terminal is { WorkspaceId: { } workspace, TabKey: { Length: > 0 } } && ValidPath(workspace) &&
                agent.Record is { Kind: not null, Command: not null }).DistinctBy(agent => agent.Terminal).ToArray(),
            Platform = OperatingSystem.IsMacOS() ? HostPlatform.MacOS : OperatingSystem.IsWindows() ? HostPlatform.Windows : HostPlatform.Linux,
            Workspaces = value.Workspaces.Where(ValidWorkspace).DistinctBy(workspace => workspace.Id).DistinctBy(workspace => workspace.Path).ToArray()
        };
        var records = (value.ProjectRecords ?? []).Where(record => record is not null && ValidPath(record.Path ?? "") &&
            ValidText(record.Id ?? "") && ValidText(record.Slug ?? "")).DistinctBy(record => record.Path).DistinctBy(record => record.Id).DistinctBy(record => record.Slug);
        return normalized with { ProjectRecords = Records(normalized, records.ToArray()) };
    }

    /// <summary>Keeps one record per open or recent project, minting an identity for a path that has none.</summary>
    private static IReadOnlyList<ProjectRecord> Records(HostState value, IReadOnlyList<ProjectRecord> known)
    {
        var paths = value.Projects.Concat(value.RecentProjects).Distinct().ToArray();
        if (paths.Length == known.Count && paths.All(path => known.Any(record => record.Path == path))) return known;
        var records = known.Where(record => paths.Contains(record.Path)).ToList();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var path in paths.Where(path => records.All(record => record.Path != path)))
            records.Add(new(Guid.NewGuid().ToString(), path, Slug(path, records), now));
        return records;
    }

    private static string Slug(string path, IReadOnlyList<ProjectRecord> taken)
    {
        var name = NonSlug().Replace(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)).ToLowerInvariant(), "-").Trim('-');
        if (name.Length == 0) name = "project";
        if (taken.All(record => record.Slug != name)) return name;
        var suffix = 2;
        while (taken.Any(record => record.Slug == $"{name}-{suffix}")) suffix++;
        return $"{name}-{suffix}";
    }

    private static string Clean(string? value) => value is null || value.Contains('\0') ? "" : value.Trim();

    private static int Width(int value) => value is >= 40 and <= 240 ? value : 0;

    private HostState Apply(HostState current, HostStateChange change)
    {
        var key = change.Key ?? ""; var value = change.Value ?? "";
        switch (change.Kind)
        {
            case "setting":
                var settings = current.Settings;
                settings = key switch
                {
                    "theme" when ValidText(value) => settings with { Theme = value.Trim() },
                    "theme-mode" when value is "fixed" or "system" => settings with { ThemeMode = value },
                    "system-light" when ValidText(value) => settings with { SystemLight = value.Trim() },
                    "system-dark" when ValidText(value) => settings with { SystemDark = value.Trim() },
                    "file-width" when Number(value) is { } width => settings with { FileLineWidth = width },
                    "file-bounded" when bool.TryParse(value, out var bounded) => settings with { FileLineWidthBounded = bounded },
                    "markdown-width" when Number(value) is { } width => settings with { MarkdownLineWidth = width },
                    "markdown-bounded" when bool.TryParse(value, out var bounded) => settings with { MarkdownLineWidthBounded = bounded },
                    "terminal-replay" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) && kb <= HostSettings.MaxTerminalReplayKb
                        => settings with { TerminalReplayKb = kb },
                    _ => throw new ArgumentException($"Invalid setting {key}.")
                };
                return settings == current.Settings ? current : current with { Settings = settings };
            case "preset-save":
                if (!ValidText(key) || value.Length == 0) throw new ArgumentException("Enter a preset name.");
                var saved = current.Presets.ToList();
                var index = saved.FindIndex(preset => preset.Name == key.Trim());
                if (index >= 0) saved[index] = new(key.Trim(), value); else saved.Add(new(key.Trim(), value));
                return current with { Presets = saved };
            case "preset-rename":
                if (!ValidText(value)) throw new ArgumentException("Enter a preset name.");
                if (current.Presets.Any(preset => preset.Name == value.Trim())) throw new InvalidOperationException($"A preset named {value.Trim()} already exists.");
                return current.Presets.Any(preset => preset.Name == key)
                    ? current with { Presets = current.Presets.Select(preset => preset.Name == key ? preset with { Name = value.Trim() } : preset).ToArray() }
                    : current;
            case "preset-delete":
                return current.Presets.Any(preset => preset.Name == key)
                    ? current with { Presets = current.Presets.Where(preset => preset.Name != key).ToArray() } : current;
            case "workspace-label":
                if (!ValidPath(key)) throw new ArgumentException("Invalid workspace path.");
                var labels = current.WorkspaceLabels.ToDictionary();
                var label = value.Trim();
                if (label.Length == 0 || label == Path.GetFileName(key.TrimEnd(Path.DirectorySeparatorChar)))
                { if (!labels.Remove(key)) return current; }
                else if (label.Contains('\0')) throw new ArgumentException("Invalid workspace name.");
                else if (labels.GetValueOrDefault(key) == label) return current;
                else labels[key] = label;
                return current with { WorkspaceLabels = labels };
            case "workspace-diff-base":
                if (!ValidPath(key)) throw new ArgumentException("Invalid workspace path.");
                var targets = current.WorkspaceDiffBases.ToDictionary();
                // The ref only has to be well formed: one that does not resolve yet is stored and reported when read.
                if (value.Length == 0 || value == current.WorkspaceBases.GetValueOrDefault(key))
                { if (!targets.Remove(key)) return current; }
                else if (targets.GetValueOrDefault(key) == GitRefs.Require(value)) return current;
                else targets[key] = value;
                return current with { WorkspaceDiffBases = targets };
            case "project-open":
                if (!ValidPath(key)) throw new ArgumentException("Invalid project path.");
                if (current.Projects.Contains(key) && !current.RecentProjects.Contains(key)) return current;
                // A linked worktree of an open project is that project's workspace, never a second project.
                var owner = current.Workspaces.FirstOrDefault(workspace => workspace.Path == key && workspace.ProjectRoot != key)?.ProjectRoot ?? ProjectPaths.LinkedWorktreeOwner(key);
                if (owner is not null && owner != key && current.Projects.Contains(owner))
                    throw new HostException(HostErrorCode.AlreadyOpen, $"This folder is already open in SharpRail as a workspace: {key}");
                var opened = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return current with
                {
                    Projects = current.Projects.Contains(key) ? current.Projects : [key, .. current.Projects],
                    RecentProjects = current.RecentProjects.Where(item => item != key).ToArray(),
                    ProjectRecords = current.ProjectRecords.Any(record => record.Path == key)
                        ? current.ProjectRecords.Select(record => record.Path == key ? record with { LastOpened = opened } : record).ToArray()
                        : [.. current.ProjectRecords, new(Guid.NewGuid().ToString(), key, Slug(key, current.ProjectRecords), opened)]
                };
            case "project-close":
                if (!current.Projects.Contains(key)) return current;
                return current with
                {
                    Projects = current.Projects.Where(item => item != key).ToArray(),
                    RecentProjects = new[] { key }.Concat(current.RecentProjects.Where(item => item != key)).Take(HostStateChange.RecentLimit).ToArray()
                };
            case "project-forget":
                return current.Projects.Contains(key) ? current with { Projects = current.Projects.Where(item => item != key).ToArray() } : current;
            case "plugin-settings":
                return ApplyPluginSettings(current, key, value);
            case "plugin-paths":
                string[] paths;
                try { paths = JsonSerializer.Deserialize<string[]>(value) ?? throw new JsonException(); }
                catch (JsonException) { throw new ArgumentException("Plugin paths must be a JSON array of directories."); }
                if (!paths.All(item => item is not null && ValidPath(item))) throw new ArgumentException("Plugin paths must be absolute directories.");
                paths = paths.Distinct().ToArray();
                return paths.SequenceEqual(current.PluginPaths) ? current : current with { PluginPaths = paths };
            default: throw new ArgumentException($"Unknown state change {change.Kind}.");
        }
    }

    // A namespace merges member by member and an empty value resets it; turning a plugin off turns off every
    // transitive dependent too, touching nothing else.
    private HostState ApplyPluginSettings(HostState current, string id, string value)
    {
        if (!PluginIdentity.IsPluginId(id)) throw new ArgumentException($"Invalid plugin id {id}.");
        JsonObject? patch = null;
        if (value.Length > 0)
        {
            try { patch = JsonNode.Parse(value) as JsonObject; }
            catch (JsonException) { }
            if (patch is null) throw new ArgumentException($"Plugin {id} settings must be a JSON object.");
            if (patch["enabled"] is { } flag && flag.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException($"Plugin {id} settings: enabled must be true or false.");
        }
        var namespaces = current.PluginSettings.ToDictionary();
        var merged = patch is null ? new JsonObject() : Merge(namespaces.GetValueOrDefault(id), patch);
        var element = JsonSerializer.SerializeToElement(merged);
        if (patch is not null && PluginNamespaceValidator is { } validate) element = validate(id, element);
        namespaces[id] = element;
        if (patch?["enabled"]?.GetValue<bool>() == false && PluginDependents is { } dependents)
            foreach (var dependent in dependents(id))
                namespaces[dependent] = JsonSerializer.SerializeToElement(Merge(namespaces.GetValueOrDefault(dependent), new JsonObject { ["enabled"] = false }));
        var changed = namespaces.Count != current.PluginSettings.Count || namespaces.Any(entry =>
            !current.PluginSettings.TryGetValue(entry.Key, out var previous) || !JsonElement.DeepEquals(previous, entry.Value));
        return changed ? current with { PluginSettings = namespaces } : current;
    }

    private static JsonObject Merge(JsonElement existing, JsonObject patch)
    {
        var merged = existing.ValueKind == JsonValueKind.Object ? JsonSerializer.SerializeToNode(existing)!.AsObject() : new JsonObject();
        foreach (var (name, member) in patch)
        {
            if (member is null) merged.Remove(name);
            else merged[name] = member.DeepClone();
        }
        return merged;
    }

    private static int? Number(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 40 and <= 240 ? number : null;
}