using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// The host's shared state: persisted to <c>state.json</c> in its directory (or kept in memory
/// without one) and published as complete snapshots to every watcher.
/// </summary>
public sealed class HostStateStore : IHostStateService
{
    public const string FileName = "state.json";

    private sealed class Stored
    {
        public HostSettings Settings { get; set; } = new();
        public List<LayoutPreset> Presets { get; set; } = [];
        public List<string> Projects { get; set; } = [];
        public List<string> RecentProjects { get; set; } = [];
        public Dictionary<string, string> WorkspaceLabels { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock gate = new();
    private readonly string? path;
    private readonly List<Channel<HostState>> watchers = [];
    // Settings a newer host wrote that this one does not know; written back untouched.
    private readonly Dictionary<string, JsonNode?> unknownSettings = [];
    private static readonly HashSet<string> KnownSettings = typeof(HostSettings).GetProperties().Select(property => property.Name).ToHashSet();
    private HostState state;

    public string? LastError { get; private set; }

    /// <summary>The state directory's identity; null for a memory-only store or an unwritable directory.</summary>
    public string? InstallationId { get; }

    public ValueTask<HostHandshake> GetHandshakeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new HostHandshake(HostProtocol.Current, HostProtocol.BuildVersion));

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
                    WorkspaceLabels = stored.WorkspaceLabels ?? []
                };
            }
            else initial = seed?.Invoke() ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            initial = new(); LastError = error.Message;
        }
        state = Normalize(initial);
        if (path is not null && !File.Exists(path) && LastError is null) Save(state);
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
            // A client that predates theme modes sends only the theme and means it to take effect.
            if (next.Settings.ThemeMode != "fixed" && changes.Any(change => change is { Kind: "setting", Key: "theme" }) &&
                !changes.Any(change => change is { Kind: "setting", Key: "theme-mode" }))
                next = next with { Settings = next.Settings with { ThemeMode = "fixed" } };
            return ValueTask.FromResult(Publish(next, persist: true));
        }
    }

    /// <summary>Records a project's workspaces after one was created or removed; a removed workspace loses its label.</summary>
    public void PublishWorkspaces(string projectRoot, IReadOnlyList<string> workspaces, string? removed = null)
    {
        lock (gate)
        {
            var lists = state.Workspaces.ToDictionary();
            lists[projectRoot] = workspaces.ToArray();
            var labels = state.WorkspaceLabels;
            if (removed is not null && labels.ContainsKey(removed)) labels = labels.Where(entry => entry.Key != removed).ToDictionary();
            Publish(state with { Workspaces = lists, WorkspaceLabels = labels }, persist: !ReferenceEquals(labels, state.WorkspaceLabels));
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
        state = next with { Revision = state.Revision + 1 };
        if (persist) Save(state);
        foreach (var watcher in watchers) watcher.Writer.TryWrite(state);
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
                WorkspaceLabels = snapshot.WorkspaceLabels.ToDictionary()
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
        return value with
        {
            Revision = 0,
            Settings = value.Settings with
            {
                ThemeMode = value.Settings.ThemeMode is "system" ? "system" : "fixed",
                Theme = Clean(value.Settings.Theme),
                SystemLight = light,
                SystemDark = dark,
                FileLineWidth = Width(value.Settings.FileLineWidth),
                MarkdownLineWidth = Width(value.Settings.MarkdownLineWidth)
            },
            Presets = value.Presets.Where(preset => preset is not null && ValidText(preset.Name) && preset.Layout is { Length: > 0 })
                .DistinctBy(preset => preset.Name).ToArray(),
            Projects = value.Projects.Where(item => item is not null && ValidPath(item)).Distinct().ToArray(),
            RecentProjects = value.RecentProjects.Where(item => item is not null && ValidPath(item)).Distinct().Take(HostStateChange.RecentLimit).ToArray(),
            WorkspaceLabels = value.WorkspaceLabels.Where(entry => ValidPath(entry.Key) && entry.Value is not null && ValidText(entry.Value)).ToDictionary(),
            Workspaces = new Dictionary<string, IReadOnlyList<string>>()
        };
    }

    private static string Clean(string? value) => value is null || value.Contains('\0') ? "" : value.Trim();

    private static int Width(int value) => value is >= 40 and <= 240 ? value : 0;

    private static HostState Apply(HostState current, HostStateChange change)
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
            case "project-open":
                if (!ValidPath(key)) throw new ArgumentException("Invalid project path.");
                if (current.Projects.Contains(key) && !current.RecentProjects.Contains(key)) return current;
                return current with
                {
                    Projects = current.Projects.Contains(key) ? current.Projects : [key, .. current.Projects],
                    RecentProjects = current.RecentProjects.Where(item => item != key).ToArray()
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
            default: throw new ArgumentException($"Unknown state change {change.Kind}.");
        }
    }

    private static int? Number(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 40 and <= 240 ? number : null;
}