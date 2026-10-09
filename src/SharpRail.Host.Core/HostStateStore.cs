using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// The host's shared state: persisted to <c>state.json</c> in its directory (or kept in memory
/// without one) and published as complete snapshots to every watcher.
/// </summary>
public sealed partial class HostStateStore : IHostStateService
{
    public const string FileName = "state.json";

    private sealed class Stored
    {
        public HostSettings Settings { get; set; } = new();
        public List<LayoutPreset> Presets { get; set; } = [];
        public List<string> Projects { get; set; } = [];
        public List<string> RecentProjects { get; set; } = [];
        public Dictionary<string, string> WorkspaceLabels { get; set; } = [];
        public Dictionary<string, string> WorkspaceBases { get; set; } = [];
        public Dictionary<string, string> WorkspaceDiffBases { get; set; } = [];
        public List<WorkspaceRecord> Workspaces { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock gate = new();
    private readonly string? path;
    private readonly List<Channel<HostState>> watchers = [];
    private HostState state;

    public string? LastError { get; private set; }
    /// <summary>Raised with the path of a workspace the host removed, so its other resources can end with it.</summary>
    public event Action<string>? WorkspaceRemoved;

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
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) ?? new();
                initial = new()
                {
                    Settings = stored.Settings ?? new(),
                    Presets = stored.Presets ?? [],
                    Projects = stored.Projects ?? [],
                    RecentProjects = stored.RecentProjects ?? [],
                    WorkspaceLabels = stored.WorkspaceLabels ?? [],
                    WorkspaceBases = stored.WorkspaceBases ?? [],
                    WorkspaceDiffBases = stored.WorkspaceDiffBases ?? [],
                    Workspaces = stored.Workspaces ?? []
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
            return ValueTask.FromResult(Publish(next, persist: true));
        }
    }

    private static IReadOnlyDictionary<string, string> Without(IReadOnlyDictionary<string, string> entries, string key) =>
        entries.ContainsKey(key) ? entries.Where(entry => entry.Key != key).ToDictionary() : entries;

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
                WorkspaceLabels = snapshot.WorkspaceLabels.ToDictionary(),
                WorkspaceBases = snapshot.WorkspaceBases.ToDictionary(),
                WorkspaceDiffBases = snapshot.WorkspaceDiffBases.ToDictionary(),
                Workspaces = snapshot.Workspaces.ToList()
            };
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Json));
            File.Move(temporary, path, overwrite: true);
            LastError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
    }

    private static bool ValidPath(string value) => value.Length > 0 && !value.Contains('\0') && Path.IsPathFullyQualified(value);

    private static bool ValidText(string value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');

    private static HostState Normalize(HostState value) => value with
    {
        Revision = 0,
        Settings = value.Settings with
        {
            ThemeMode = value.Settings.ThemeMode is "system" ? "system" : "fixed",
            Theme = Clean(value.Settings.Theme),
            SystemLight = Clean(value.Settings.SystemLight),
            SystemDark = Clean(value.Settings.SystemDark),
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
        Workspaces = value.Workspaces.Where(ValidWorkspace).DistinctBy(workspace => workspace.Id).DistinctBy(workspace => workspace.Path).ToArray()
    };

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