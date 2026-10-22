using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.AirContext;
using SharpRail.Plugins.AirContext.Host;
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
        // Air Context's enabled workspaces, as the file kept them before it became a plugin; read once to migrate, never written.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? ContextWorkspaces { get; set; }
        public List<string> SharprailExcludeDeclined { get; set; } = [];
        public Dictionary<string, string> WorkspaceLanguages { get; set; } = [];
        public Dictionary<string, string> ProjectLanguageAutostart { get; set; } = [];
        public Dictionary<string, string> WorkspaceLabels { get; set; } = [];
        public Dictionary<string, string> ProjectLabels { get; set; } = [];
        public Dictionary<string, string> UnreadWorkspaces { get; set; } = [];
        public Dictionary<string, string> WorkspaceBases { get; set; } = [];
        public Dictionary<string, string> WorkspaceDiffBases { get; set; } = [];
        public List<WorkspaceRecord> Workspaces { get; set; } = [];
        public Dictionary<string, JsonElement> PluginSettings { get; set; } = [];
        public List<string> PluginPaths { get; set; } = [];
        public List<TerminalAgent> TerminalAgents { get; set; } = [];
        public List<TerminalTitle> TerminalTitles { get; set; } = [];
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
    private readonly SemaphoreSlim changesGate = new(1, 1);

    /// <summary>The host's terminals, ended when their project closes; input they accept is activity in their workspace.</summary>
    public PtyTerminalService? Terminals
    {
        get => terminals;
        set
        {
            if (ReferenceEquals(terminals, value)) return;
            if (terminals is not null)
            {
                terminals.InputAccepted -= TerminalInput;
                terminals.PrepareRevival = null;
            }
            terminals = value;
            if (value is not null)
            {
                value.InputAccepted += TerminalInput;
                value.PrepareRevival = async (workspace, token) =>
                {
                    try { await ProjectLanguageAutostartAsync(new(workspace, Operation: "prepareAgent"), token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { System.Diagnostics.Trace.WriteLine("Language server startup: " + error.Message); }
                };
            }
        }
    }
    private PtyTerminalService? terminals;

    private void TerminalInput(string workspace) => RecordWorkspaceActivity(workspace);

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
    /// <param name="verticalCenterTabs">The embedded desktop profile's fallback for a previously unset tab mode.</param>
    /// <param name="verticalTabsInProjects">The embedded desktop profile's fallback for a previously unset Projects tab home.</param>
    public HostStateStore(string? directory, Func<HostState>? seed = null, bool? verticalCenterTabs = null, bool? verticalTabsInProjects = null)
    {
        path = directory is null ? null : Path.Combine(directory, FileName);
        HostState initial;
        var fileIconsConfigured = false;
        List<string>? legacyContextWorkspaces = null;
        try
        {
            if (path is not null && File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var stored = JsonSerializer.Deserialize<Stored>(text, Json) ?? new();
                legacyContextWorkspaces = stored.ContextWorkspaces;
                if (JsonNode.Parse(text)?[nameof(Stored.Settings)] is JsonObject settings)
                {
                    fileIconsConfigured = settings.ContainsKey(nameof(HostSettings.FileTypeIconsEnabled));
                    foreach (var (name, value) in settings)
                        if (!KnownSettings.Contains(name)) unknownSettings[name] = value?.DeepClone();
                }
                initial = new()
                {
                    Settings = stored.Settings ?? new(),
                    Presets = stored.Presets ?? [],
                    // Directories stored before the one spelling (ThinkRail #775) are healed here and saved with the next write.
                    Projects = [.. (stored.Projects ?? []).Select(ProjectPaths.Heal)],
                    RecentProjects = [.. (stored.RecentProjects ?? []).Select(ProjectPaths.Heal)],
                    ProjectRecords = stored.ProjectRecords ?? [],
                    WorkspaceLanguages = stored.WorkspaceLanguages ?? [],
                    ProjectLanguageAutostart = stored.ProjectLanguageAutostart ?? [],
                    SharprailExcludeDeclined = stored.SharprailExcludeDeclined ?? [],
                    WorkspaceLabels = stored.WorkspaceLabels ?? [],
                    ProjectLabels = stored.ProjectLabels ?? [],
                    UnreadWorkspaces = stored.UnreadWorkspaces ?? [],
                    WorkspaceBases = stored.WorkspaceBases ?? [],
                    WorkspaceDiffBases = stored.WorkspaceDiffBases ?? [],
                    Workspaces = [.. (stored.Workspaces ?? []).Select(record => record is null ? record! : record with { ProjectRoot = ProjectPaths.Heal(record.ProjectRoot), Path = ProjectPaths.Heal(record.Path) })],
                    PluginSettings = stored.PluginSettings ?? [],
                    PluginPaths = stored.PluginPaths ?? [],
                    // Sessions end with the app, so a record whose workspace folder is gone can never resume; plugins
                    // that follow every recorded workspace would otherwise read and watch folders that no longer exist.
                    TerminalAgents = (stored.TerminalAgents ?? []).Where(agent => agent?.Terminal?.WorkspaceId is { } workspace && Directory.Exists(workspace)).ToList(),
                    TerminalTitles = (stored.TerminalTitles ?? []).Where(title => title?.Terminal?.WorkspaceId is { } workspace && Directory.Exists(workspace)).ToList()
                };
            }
            else initial = seed?.Invoke() ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            initial = new(); LastError = error.Message;
        }
        var migrated = initial;
        if (initial.PluginSettings.TryGetValue("file-icons", out var legacyIcons))
        {
            var enabled = legacyIcons.ValueKind != JsonValueKind.Object || !legacyIcons.TryGetProperty("enabled", out var legacyEnabled) || legacyEnabled.ValueKind != JsonValueKind.False;
            migrated = initial with
            {
                Settings = fileIconsConfigured ? initial.Settings : initial.Settings with { FileTypeIconsEnabled = enabled },
                PluginSettings = initial.PluginSettings.Where(entry => entry.Key != "file-icons").ToDictionary()
            };
        }
        // Air Context became a plugin (protocol 8). Hiding it is now the plugin being disabled; the workspaces the user enabled
        // and the CLI's storage move into the plugin's own state. The old keys leave the file in the same save, so this runs once.
        var hadHiddenKey = unknownSettings.Remove("AirContextHidden", out var hiddenNode);
        var airContextHidden = hiddenNode is JsonValue hiddenValue && hiddenValue.TryGetValue<bool>(out var hidden) && hidden;
        var airContextLegacy = legacyContextWorkspaces is not null || hadHiddenKey;
        // The plugin ships disabled. A profile that used Air Context (an enabled workspace, a receipt or a download) and did not hide it
        // gets it enabled explicitly; a hidden one is disabled explicitly; an explicit value already in the file wins over both.
        var usedAirContext = (legacyContextWorkspaces ?? []).Any(ValidPath) ||
            directory is not null && Directory.Exists(Path.Combine(directory, AirContextMigration.LegacyDirectory)) &&
            Directory.EnumerateFiles(Path.Combine(directory, AirContextMigration.LegacyDirectory), "*", SearchOption.AllDirectories).Any();
        var airExplicit = migrated.PluginSettings.TryGetValue(AirContextContract.Id, out var airSettings) && airSettings.ValueKind == JsonValueKind.Object && airSettings.TryGetProperty("enabled", out _);
        if (!airExplicit && (airContextHidden || usedAirContext))
            migrated = migrated with { PluginSettings = new Dictionary<string, JsonElement>(migrated.PluginSettings) { [AirContextContract.Id] = JsonSerializer.SerializeToElement(new { enabled = !airContextHidden }) } };
        if (directory is not null && (legacyContextWorkspaces is not null || Directory.Exists(Path.Combine(directory, AirContextMigration.LegacyDirectory))))
        {
            try { AirContextMigration.Migrate(directory, [.. (legacyContextWorkspaces ?? []).Where(ValidPath).Distinct().Order(StringComparer.Ordinal)]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
        }
        state = BackfillActivity(Normalize(migrated), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        state = state with
        {
            Settings = state.Settings with
            {
                VerticalCenterTabs = state.Settings.VerticalCenterTabs ?? verticalCenterTabs ?? false,
                VerticalTabsInProjects = state.Settings.VerticalTabsInProjects ?? verticalTabsInProjects ?? false
            }
        };
        OpenLanguageMetadata();
        MigrateJavaKotlinProvider();
        // Identities minted for a file that predates them are written at once, or they would change on the next launch.
        if (path is not null && LastError is null && (!File.Exists(path) || !state.ProjectRecords.SequenceEqual(initial.ProjectRecords) ||
            state.Settings != initial.Settings || airContextLegacy || state.PluginSettings.Count != initial.PluginSettings.Count || !state.Workspaces.SequenceEqual(initial.Workspaces))) Save(state);
        if (directory is not null)
            try { InstallationId = Installation.EnsureIn(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError ??= error.Message; }
    }

    public HostState Current
    {
        get
        {
            HostState current;
            lock (gate) current = state;
            return Visible(current);
        }
    }

    // Recents is a projection checked against the filesystem on every read: a folder that no longer exists, or became a
    // file, drops out, while other filesystem errors keep it. The stored list is untouched, so a restored folder returns.
    private static HostState Visible(HostState current)
    {
        var recents = current.RecentProjects.Where(Exists).ToArray();
        return recents.Length == current.RecentProjects.Count ? current : current with { RecentProjects = recents };

        static bool Exists(string path)
        {
            try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        }
    }

    public ValueTask<HostState> GetStateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);

    public async ValueTask<HostState> ChangeAsync(IReadOnlyList<HostStateChange> changes, CancellationToken cancellationToken = default)
    {
        await changesGate.WaitAsync(cancellationToken);
        try
        {
            HostState previous;
            HostState next;
            lock (gate)
            {
                previous = state;
                next = state;
                foreach (var change in changes) next = Apply(next, change);
            }
            if (Terminals is { } terminals)
                foreach (var project in previous.Projects.Except(next.Projects))
                {
                    await terminals.CloseProjectAsync(project, previous.WorkspacesOf(project).Select(workspace => workspace.Path).ToArray());
                    var roots = (previous.WorkspacesOf(project).Select(workspace => workspace.Path).ToArray()).Append(project).ToHashSet(StringComparer.Ordinal);
                    var agents = previous.TerminalAgents.Where(agent => roots.Contains(agent.Terminal.WorkspaceId) ||
                        ProjectServices.KnownCheckout(agent.Terminal.WorkspaceId)?.ProjectRoot == Path.GetFullPath(project))
                        .Select(agent => agent.Terminal).ToHashSet();
                    foreach (var terminal in agents) await terminals.CloseAsync(PtyTerminalService.SessionFor(terminal));
                    RemoveTerminalAgents(agents.Contains);
                    RemoveTerminalTitles(terminal => agents.Contains(terminal) || roots.Contains(terminal.WorkspaceId));
                }
            lock (gate)
            {
                // Terminal closure can remove agent records while the shells drain.
                next = state;
                foreach (var change in changes) next = Apply(next, change);
                if (!ReferenceEquals(next, state)) next = next with { ProjectRecords = Records(next, next.ProjectRecords) };
                // A client that predates theme modes sends only the theme and means it to take effect.
                if (next.Settings.ThemeMode != "fixed" && changes.Any(change => change is { Kind: "setting", Key: "theme" }) &&
                    !changes.Any(change => change is { Kind: "setting", Key: "theme-mode" }))
                    next = next with { Settings = next.Settings with { ThemeMode = "fixed" } };
                next = Visible(Publish(next, persist: true));
            }
            if (previous.Settings.LspEnabled && !next.Settings.LspEnabled && languages is { } languageService)
                await languageService.SuspendAsync();
            if (previous.Settings.JavaKotlinProvider != next.Settings.JavaKotlinProvider && languages is { } jvmLanguages)
                await jvmLanguages.SwitchProviderAsync(CancellationToken.None);
            // A project opened for the first time starts the language servers its shared configuration marks autoStart in its Default workspace.
            foreach (var project in next.Projects.Except(previous.Projects)) StartWorkspaceLanguages(project);
            return next;
        }
        finally { changesGate.Release(); }
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

    /// <summary>Publishes one unexpected language-server death so every client can notify; the value rides later snapshots and is never persisted.</summary>
    internal void PublishLanguageDeath(string workspace, string server, string time, string reason)
    {
        lock (gate)
        {
            var deaths = state.LanguageDeaths.ToDictionary();
            deaths[workspace + "\0" + server] = time + "|" + reason;
            while (deaths.Count > 64) deaths.Remove(deaths.OrderBy(pair => pair.Value, StringComparer.Ordinal).First().Key);
            Publish(state with { LanguageDeaths = deaths }, persist: false);
        }
    }

    /// <summary>Sets or clears one terminal's agent record.</summary>
    public void SetTerminalAgent(TerminalRef terminal, TerminalAgentRecord? record)
    {
        lock (gate)
        {
            var agents = state.TerminalAgents.Where(agent => agent.Terminal != terminal).ToList();
            if (record is not null) agents.Add(new(terminal, record));
            if (!agents.SequenceEqual(state.TerminalAgents)) Publish(state with { TerminalAgents = agents }, persist: true);
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

    /// <summary>Sets or clears the title of one terminal's tab.</summary>
    public void SetTerminalTitle(TerminalRef terminal, string? title)
    {
        lock (gate)
        {
            var titles = state.TerminalTitles.Where(known => known.Terminal != terminal).ToList();
            if (title is not null) titles.Add(new(terminal, title));
            if (titles.SequenceEqual(state.TerminalTitles)) return;
            Publish(state with { TerminalTitles = titles }, persist: true);
        }
    }

    /// <summary>Drops the titles of terminals that closed.</summary>
    public void RemoveTerminalTitles(Func<TerminalRef, bool> closed)
    {
        lock (gate)
        {
            if (state.TerminalTitles.Any(title => closed(title.Terminal)))
                Publish(state with { TerminalTitles = state.TerminalTitles.Where(title => !closed(title.Terminal)).ToArray() }, persist: true);
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
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Snapshots are complete; a slow watcher only needs the latest.
                var latest = default(HostState);
                while (channel.Reader.TryRead(out var item)) latest = item;
                if (latest is not null) yield return Visible(latest);
            }
        }
        finally { lock (gate) watchers.Remove(channel); }
    }

    private HostState Publish(HostState next, bool persist)
    {
        if (ReferenceEquals(next, state)) return state;
        var previous = state;
        if (persist) PersistLanguageMetadata(previous.WorkspaceLanguages, next.WorkspaceLanguages);
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
                WorkspaceLanguages = snapshot.WorkspaceLanguages.ToDictionary(),
                ProjectLanguageAutostart = snapshot.ProjectLanguageAutostart.ToDictionary(),
                SharprailExcludeDeclined = snapshot.SharprailExcludeDeclined.ToList(),
                WorkspaceLabels = snapshot.WorkspaceLabels.ToDictionary(),
                ProjectLabels = snapshot.ProjectLabels.ToDictionary(),
                UnreadWorkspaces = snapshot.UnreadWorkspaces.ToDictionary(),
                WorkspaceBases = snapshot.WorkspaceBases.ToDictionary(),
                WorkspaceDiffBases = snapshot.WorkspaceDiffBases.ToDictionary(),
                Workspaces = snapshot.Workspaces.ToList(),
                PluginSettings = snapshot.PluginSettings.ToDictionary(),
                PluginPaths = snapshot.PluginPaths.ToList(),
                TerminalAgents = snapshot.TerminalAgents.ToList(),
                TerminalTitles = snapshot.TerminalTitles.ToList()
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
            SharprailExcludeDeclined = value.SharprailExcludeDeclined.Where(item => item is not null && ValidPath(item)).Distinct().Order(StringComparer.Ordinal).ToArray(),
            Settings = value.Settings with
            {
                ThemeMode = value.Settings.ThemeMode is "system" ? "system" : "fixed",
                Theme = Clean(value.Settings.Theme),
                SystemLight = light,
                SystemDark = dark,
                FileLineWidth = Width(value.Settings.FileLineWidth),
                MarkdownLineWidth = Width(value.Settings.MarkdownLineWidth),
                TerminalReplayKb = Math.Clamp(value.Settings.TerminalReplayKb, 0, HostSettings.MaxTerminalReplayKb),
                CustomHighlighting = ValidHighlighting(value.Settings.CustomHighlighting) ? value.Settings.CustomHighlighting : "",
                WindowDecorations = value.Settings.WindowDecorations is "client" or "system" ? value.Settings.WindowDecorations : "",
                SettleIdleDays = value.Settings.SettleIdleDays is { } days ? Math.Clamp(days, 1, HostSettings.MaxSettleIdleDays) : null
            },
            Presets = value.Presets.Where(preset => preset is not null && ValidText(preset.Name) && preset.Layout is { Length: > 0 })
                .DistinctBy(preset => preset.Name).ToArray(),
            Projects = value.Projects.Where(item => item is not null && ValidPath(item)).Distinct().ToArray(),
            RecentProjects = value.RecentProjects.Where(item => item is not null && ValidPath(item)).Distinct().Take(HostStateChange.RecentLimit).ToArray(),
            WorkspaceLabels = value.WorkspaceLabels.Where(entry => ValidPath(entry.Key) && entry.Value is not null && ValidText(entry.Value)).ToDictionary(),
            UnreadWorkspaces = value.UnreadWorkspaces.Where(entry => ValidPath(entry.Key) && UnreadRank(entry.Value) > 0).ToDictionary(),
            ProjectLabels = value.ProjectLabels.Where(entry => ValidPath(entry.Key) && entry.Value is not null && NormalizeProjectName(entry.Value) == entry.Value).ToDictionary(),
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
            TerminalTitles = value.TerminalTitles.Where(title => title?.Terminal is { WorkspaceId: { } workspace, TabKey: { Length: > 0 } } && ValidPath(workspace) &&
                !string.IsNullOrWhiteSpace(title.Title)).DistinctBy(title => title.Terminal).ToArray(),
            Platform = OperatingSystem.IsMacOS() ? HostPlatform.MacOS : OperatingSystem.IsWindows() ? HostPlatform.Windows : HostPlatform.Linux,
            Workspaces = value.Workspaces.Where(ValidWorkspace).DistinctBy(workspace => workspace.Id).DistinctBy(workspace => workspace.Path)
                .Select(NormalizeSettling).ToArray()
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

    // An override the host does not know, or one on the Default workspace, is dropped rather than shelving the row.
    private static WorkspaceRecord NormalizeSettling(WorkspaceRecord workspace) =>
        workspace.SettledOverride is SettledOverrides.Settled or SettledOverrides.Active && workspace.Kind != WorkspaceKinds.Default
            ? workspace
            : workspace.SettledOverride is null && workspace.SettledAt is null ? workspace : workspace with { SettledOverride = null, SettledAt = null };

    private static string Clean(string? value) => value is null || value.Contains('\0') ? "" : value.Trim();

    private static int Width(int value) => value is >= 40 and <= 240 ? value : 0;

    private static bool ValidHighlighting(string value)
    {
        if (value.Length == 0) return true;
        if (value.Length > 2 * 1024 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 64) return false;
            var scopes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("Scope", out var scope) || scope.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(scope.GetString()) || !scopes.Add(scope.GetString()!) ||
                    !entry.TryGetProperty("Grammar", out var grammar) || grammar.ValueKind != JsonValueKind.String || grammar.GetString()!.Length > 512 * 1024 ||
                    !entry.TryGetProperty("Patterns", out var patterns) || patterns.ValueKind != JsonValueKind.Array || patterns.GetArrayLength() is < 1 or > 32) return false;
                foreach (var pattern in patterns.EnumerateArray())
                    if (pattern.ValueKind != JsonValueKind.String || pattern.GetString() is not { Length: > 0 and <= 200 } text || text.Contains('/') || text.Contains('\\')) return false;
                using var source = JsonDocument.Parse(grammar.GetString()!);
                if (source.RootElement.ValueKind != JsonValueKind.Object || !source.RootElement.TryGetProperty("scopeName", out var declared) ||
                    declared.ValueKind != JsonValueKind.String || declared.GetString() != scope.GetString()) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private HostState Apply(HostState current, HostStateChange change)
    {
        var key = change.Key ?? ""; var value = change.Value ?? "";
        switch (change.Kind)
        {
            case "terminal-title":
                var terminal = JsonSerializer.Deserialize<TerminalRef>(key) ?? throw new ArgumentException("A terminal is required.");
                if (string.IsNullOrWhiteSpace(terminal.WorkspaceId) || string.IsNullOrWhiteSpace(terminal.TabKey) ||
                    string.IsNullOrWhiteSpace(value) || value.Length > TerminalTab.MaxTitleLength)
                    throw new ArgumentException("A terminal and a nonempty title of at most 1000 characters are required.");
                var titles = current.TerminalTitles.Where(title => title.Terminal != terminal).Append(new TerminalTitle(terminal, value.Trim())).ToArray();
                return titles.SequenceEqual(current.TerminalTitles) ? current : current with { TerminalTitles = titles };
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
                    "file-type-icons" when bool.TryParse(value, out var icons) => settings with { FileTypeIconsEnabled = icons },
                    "lsp-enabled" when bool.TryParse(value, out var lspEnabled) => settings with { LspEnabled = lspEnabled },
                    "java-kotlin-provider" when IsProviderChoice(value) => settings with { JavaKotlinProvider = value },
                    "notifications" when bool.TryParse(value, out var enabled) => settings with { NotificationsEnabled = enabled },
                    _ when key.StartsWith("language-server:", StringComparison.Ordinal) => settings with { LanguageServers = SetLanguageServer(settings.LanguageServers, key[16..], value) },
                    _ when key.StartsWith("language-tool:", StringComparison.Ordinal) => settings with { LanguageTools = SetLanguageTool(settings.LanguageTools, key[14..], value) },
                    "custom-highlighting" when ValidHighlighting(value) => settings with { CustomHighlighting = value },
                    "vertical-tabs" when bool.TryParse(value, out var vertical) => settings with { VerticalCenterTabs = vertical },
                    "settle-idle-days" when value is "" or "never" => settings with { SettleIdleDays = null },
                    "settle-idle-days" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= HostSettings.MaxSettleIdleDays
                        => settings with { SettleIdleDays = days },
                    "window-decorations" when value is "auto" or "" => settings with { WindowDecorations = "" },
                    "window-decorations" when value is "client" or "system" => settings with { WindowDecorations = value },
                    "tabs-in-projects" when bool.TryParse(value, out var inProjects) => settings with { VerticalTabsInProjects = inProjects },
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
            case "workspace-unread":
                if (!ValidPath(key)) throw new ArgumentException("Invalid workspace path.");
                if (UnreadRank(value) == 0) throw new ArgumentException("Unknown unread reason " + value + ".");
                if (UnreadRank(current.UnreadWorkspaces.GetValueOrDefault(key)) >= UnreadRank(value)) return current;
                return current with { UnreadWorkspaces = new Dictionary<string, string>(current.UnreadWorkspaces) { [key] = value } };
            case "workspace-read":
                if (!current.UnreadWorkspaces.TryGetValue(key, out var unread)) return current;
                // Mark as read by hand never dismisses an agent that still waits for input; seeing the workspace does.
                if (unread == "blocked" && value != "seen") return current;
                return current with { UnreadWorkspaces = Without(current.UnreadWorkspaces, key) };
            case "project-label":
                if (!ValidPath(key)) throw new ArgumentException("Invalid project path.");
                var projectName = NormalizeProjectName(value) ?? throw new ArgumentException("A project name has 1 to 100 characters.");
                var projectLabels = current.ProjectLabels.ToDictionary();
                if (projectName == Path.GetFileName(key.TrimEnd(Path.DirectorySeparatorChar))) { if (!projectLabels.Remove(key)) return current; }
                else if (projectLabels.GetValueOrDefault(key) == projectName) return current;
                else projectLabels[key] = projectName;
                return current with { ProjectLabels = projectLabels };
            case "workspace-diff-base":
                if (!ValidPath(key)) throw new ArgumentException("Invalid workspace path.");
                var targets = current.WorkspaceDiffBases.ToDictionary();
                // The ref only has to be well formed: one that does not resolve yet is stored and reported when read.
                if (value.Length == 0 || value == current.WorkspaceBases.GetValueOrDefault(key))
                { if (!targets.Remove(key)) return current; }
                else if (targets.GetValueOrDefault(key) == GitRefs.Require(value)) return current;
                else targets[key] = value;
                return current with { WorkspaceDiffBases = targets };
            case "sharprail-exclude-declined":
                if (!ValidPath(key)) throw new ArgumentException("Invalid project path.");
                return current.SharprailExcludeDeclined.Contains(key) ? current : current with { SharprailExcludeDeclined = [.. current.SharprailExcludeDeclined, key] };
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

    /// <summary>Collapses whitespace runs and trims; null unless 1 to 100 characters without control characters remain.</summary>
    internal static string? NormalizeProjectName(string? value)
    {
        if (value is null || value.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character))) return null;
        var name = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length is >= 1 and <= 100 ? name : null;
    }

    /// <summary>How strongly a reason asks for the user: blocked over failed over done over a hand-set mark; 0 for none.</summary>
    internal static int UnreadRank(string? reason) => reason switch { "blocked" => 4, "failed" => 3, "done" => 2, "manual" => 1, _ => 0 };
}