using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.AirContext;
using Tereditor.Plugins.AirContext.Host;
using Tereditor.Plugins.Api;

namespace Tereditor.Host.Core;

/// <summary>
/// The host's shared state: persisted to <c>state.json</c> in its directory (or kept in memory
/// without one) and published as complete snapshots to every watcher.
/// </summary>
public sealed partial class HostStateStore : IHostStateService
{
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();

    public const string FileName = "state.json";

    internal sealed class Stored
    {
        public HostSettings Settings { get; set; } = new();
        public List<LayoutPreset> Presets { get; set; } = [];
        public List<KeymapPreset> KeymapPresets { get; set; } = [];
        public List<string> Projects { get; set; } = [];
        public List<string> RecentProjects { get; set; } = [];
        public List<ProjectRecord> ProjectRecords { get; set; } = [];
        // Air Context's enabled workspaces, as the file kept them before it became a plugin; read once to migrate, never written.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? ContextWorkspaces { get; set; }
        public List<string> TereditorExcludeDeclined { get; set; } = [];
        public Dictionary<string, string> WorkspaceLanguages { get; set; } = [];
        public Dictionary<string, string> ProjectLanguageAutostart { get; set; } = [];
        public Dictionary<string, string> ProjectLanguageHidden { get; set; } = [];
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
        // The terminal whose workspace_create made each workspace; only that terminal may emigrate into it.
        public Dictionary<string, TerminalRef> WorkspaceCreators { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = HostJson.Resolving(new() { WriteIndented = true });
    private readonly Lock gate = new();
    private readonly string? path;
    internal string? DirectoryPath => path is null ? null : Path.GetDirectoryName(path);
    private readonly List<Channel<HostState>> watchers = [];
    // Settings a newer host wrote that this one does not know; written back untouched.
    private readonly Dictionary<string, JsonNode?> unknownSettings = [];
    private static readonly HashSet<string> KnownSettings = Json.Info<HostSettings>().Properties.Select(property => property.Name).ToHashSet();
    private HostState state;
    private readonly SemaphoreSlim changesGate = new(1, 1);
    private readonly Dictionary<string, TerminalRef> workspaceCreators = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> emigrationAnswers = new(StringComparer.Ordinal);

    /// <summary>Which agent sessions this host and every other tereditor process on the machine have open.</summary>
    public Tereditor.Host.Core.Plugins.AgentSessionOwnership AgentSessions { get; init; } = new();

    /// <summary>The host's terminals, ended when their project closes.</summary>
    public PtyTerminalService? Terminals
    {
        get => terminals;
        set
        {
            if (ReferenceEquals(terminals, value)) return;
            if (terminals is not null)
            {
                terminals.TitleChanged -= ProgramTitle;
                terminals.Owned.Changed -= PublishOwnedProcesses;
                terminals.PrepareRevival = null;
            }
            terminals = value;
            WatchFolders(value is not null);
            if (value is not null)
            {
                value.TitleChanged += ProgramTitle;
                value.Owned.Changed += PublishOwnedProcesses;
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

    private void PublishOwnedProcesses()
    {
        var owned = terminals?.Owned.List() ?? [];
        lock (gate)
            if (!state.OwnedProcesses.SequenceEqual(owned)) Publish(state with { OwnedProcesses = owned }, persist: false);
    }

    private void ProgramTitle(TerminalRef terminal, string? title) => SetTerminalTitle(terminal, title, TerminalTitleSource.Program);

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

    /// <summary>
    /// Every transitive dependent of a plugin given the namespaces stored now, so turning it off turns them off in the same
    /// batch. A dependent that was on carries the dependency that took it down, which the batch writes as its suspension mark.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, JsonElement>, IReadOnlyList<(string Id, string? Cause)>>? PluginDependents { get; set; }

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
                var stored = JsonSerializer.Deserialize(text, Json.Info<Stored>()) ?? new();
                legacyContextWorkspaces = stored.ContextWorkspaces;
                foreach (var (workspace, creator) in stored.WorkspaceCreators ?? [])
                    if (creator?.WorkspaceId is { Length: > 0 } && creator.TabKey is { Length: > 0 } && Directory.Exists(workspace)) workspaceCreators[workspace] = creator;
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
                    KeymapPresets = stored.KeymapPresets ?? [],
                    // Directories stored before the one spelling (ThinkRail #775) are healed here and saved with the next write.
                    Projects = [.. (stored.Projects ?? []).Select(ProjectPaths.Heal)],
                    RecentProjects = [.. (stored.RecentProjects ?? []).Select(ProjectPaths.Heal)],
                    ProjectRecords = stored.ProjectRecords ?? [],
                    WorkspaceLanguages = stored.WorkspaceLanguages ?? [],
                    ProjectLanguageAutostart = stored.ProjectLanguageAutostart ?? [],
                    ProjectLanguageHidden = stored.ProjectLanguageHidden ?? [],
                    TereditorExcludeDeclined = stored.TereditorExcludeDeclined ?? [],
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
                    TerminalTitles = (stored.TerminalTitles ?? []).Where(title => title?.Terminal?.WorkspaceId is { } workspace && Directory.Exists(workspace))
                        // Program titles stored before the scanner dropped status glyphs keep their "✳ " until the program speaks again.
                        .Select(title => title.Source == TerminalTitleSource.Program ? title with { Title = TerminalTitleScanner.Clean(TerminalTitleScanner.StripStatus(title.Title ?? "")) ?? title.Title! } : title).ToList()
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
        // Air Governance was renamed Air Gateway (plugin id air-governance -> air-gateway). The old namespace carries over once, unless the new one
        // already exists; it leaves the file in the same save.
        var gatewayRenamed = migrated.PluginSettings.TryGetValue("air-governance", out var legacyGateway);
        if (gatewayRenamed)
        {
            var renamed = migrated.PluginSettings.Where(entry => entry.Key != "air-governance").ToDictionary();
            renamed.TryAdd("air-gateway", legacyGateway);
            migrated = migrated with { PluginSettings = renamed };
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
            migrated = migrated with { PluginSettings = new Dictionary<string, JsonElement>(migrated.PluginSettings) { [AirContextContract.Id] = JsonSerializer.SerializeToElement(new JsonObject { ["enabled"] = !airContextHidden }, HostJson.Default.JsonObject) } };
        migrated = MigrateDisabledLanguageServers(migrated);
        if (directory is not null && (legacyContextWorkspaces is not null || Directory.Exists(Path.Combine(directory, AirContextMigration.LegacyDirectory))))
        {
            try { AirContextMigration.Migrate(directory, [.. (legacyContextWorkspaces ?? []).Where(ValidPath).Distinct().Order(StringComparer.Ordinal)]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
        }
        state = Normalize(migrated) with { WorkspaceCreators = CreatorEntries() };
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
            state.Settings != initial.Settings || airContextLegacy || gatewayRenamed || state.PluginSettings.Count != initial.PluginSettings.Count || !state.Workspaces.SequenceEqual(initial.Workspaces))) Save(state);
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

    // Projects, their workspaces and recents are a projection checked against the filesystem on every read: a folder that no
    // longer exists, or became a file, drops out, while other filesystem errors keep it. The stored lists are untouched, so a
    // restored folder (or a remounted volume) returns.
    private static HostState Visible(HostState current)
    {
        var projects = current.Projects.Where(FolderExists).ToArray();
        var recents = current.RecentProjects.Where(FolderExists).ToArray();
        var workspaces = current.Workspaces.Where(workspace => FolderExists(workspace.ProjectRoot) && (workspace.Path == workspace.ProjectRoot || FolderExists(workspace.Path))).ToArray();
        return projects.Length == current.Projects.Count && recents.Length == current.RecentProjects.Count && workspaces.Length == current.Workspaces.Count ? current
            : current with { Projects = projects, RecentProjects = recents, Workspaces = workspaces };
    }

    private static bool FolderExists(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Sends every client a fresh snapshot without changing the state, so a folder found gone leaves their rails.</summary>
    internal void Republish()
    {
        lock (gate)
            if (!ReferenceEquals(Visible(state), state)) Publish(state with { }, persist: false);
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
            // A closed project's language servers stop now; its settings, drafts and exit records stay for when it reopens.
            if (languages is { } closing)
                foreach (var project in previous.Projects.Except(next.Projects))
                    foreach (var workspace in previous.WorkspacesOf(project).Select(workspace => workspace.Path).Append(project).Distinct())
                        await closing.StopWorkspaceAsync(workspace, "its project was closed");
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
            foreach (var answer in changes.Where(change => change.Kind == "terminal-resume-answer"))
                if (JsonSerializer.Deserialize(answer.Key ?? "", HostJson.Default.TerminalRef) is { } answered) ResumeAnswered?.Invoke(answered, answer.Value);
            foreach (var answer in changes.Where(change => change.Kind == "terminal-emigration-answer"))
                if (emigrationAnswers.TryRemove(answer.Key ?? "", out var waiting)) waiting.TrySetResult(answer.Value == "approve");
            if (previous.Settings.LspEnabled && !next.Settings.LspEnabled && languages is { } languageService)
                await languageService.SuspendAsync();
            if (previous.Settings.LanguageLimits != next.Settings.LanguageLimits && languages is { } limited)
                _ = Task.Run(limited.EnforceAsync, CancellationToken.None);
            if (previous.Settings.JavaKotlinProvider != next.Settings.JavaKotlinProvider && languages is { } jvmLanguages)
                await jvmLanguages.SwitchProviderAsync(CancellationToken.None);
            // A project opened for the first time starts the language servers its shared configuration marks autoStart in its Default workspace.
            foreach (var project in next.Projects.Except(previous.Projects))
            {
                if (languages is { } reopened)
                    foreach (var workspace in next.WorkspacesOf(project).Select(workspace => workspace.Path).Append(project)) reopened.ReopenWorkspace(workspace);
                StartWorkspaceLanguages(project);
            }
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
            if (PluginJson.Serialize(roster) == PluginJson.Serialize(state.Plugins)) return;
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

    /// <summary>
    /// Sets or clears the title of one terminal's tab from one source. A title from a stronger source stays: a user
    /// rename outranks an agent's <c>set_title</c>, which outranks the program's OSC title. Program titles are not persisted.
    /// </summary>
    /// <returns>False when a stronger source's title kept the tab.</returns>
    public bool SetTerminalTitle(TerminalRef terminal, string? title, TerminalTitleSource source)
    {
        lock (gate)
        {
            var existing = state.TerminalTitles.FirstOrDefault(known => known.Terminal == terminal);
            if (existing is not null && existing.Source < source) return false;
            var titles = state.TerminalTitles.Where(known => known.Terminal != terminal).ToList();
            if (title is not null) titles.Add(new(terminal, title, source));
            if (titles.SequenceEqual(state.TerminalTitles)) return true;
            Publish(state with { TerminalTitles = titles }, persist: source != TerminalTitleSource.Program);
            return true;
        }
    }

    /// <summary>Records the terminal whose <c>workspace_create</c> made a workspace, so only it may emigrate there.</summary>
    public void RecordWorkspaceCreator(string workspace, TerminalRef creator)
    {
        lock (gate)
        {
            workspaceCreators.Remove(workspace);
            workspaceCreators[workspace] = creator;
            Publish(state with { WorkspaceCreators = CreatorEntries() }, persist: true);
        }
    }

    private WorkspaceCreatorEntry[] CreatorEntries() => [.. workspaceCreators.Select(entry => new WorkspaceCreatorEntry(entry.Key, entry.Value))];

    /// <summary>The terminal that created a workspace through <c>workspace_create</c>, or null for any other workspace.</summary>
    public TerminalRef? WorkspaceCreator(string workspace)
    {
        lock (gate) return workspaceCreators.GetValueOrDefault(workspace);
    }

    /// <summary>
    /// Publishes an emigration request for every client and waits for the user's answer through
    /// <see cref="HostStateChange.AnswerEmigration"/>. Cancelling withdraws the request; either way it leaves the state.
    /// </summary>
    public async Task<bool> RequestEmigrationAsync(TerminalEmigrationRequest request, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!emigrationAnswers.TryAdd(request.Id, answer)) throw new InvalidOperationException("An emigration request with this id is already pending.");
        lock (gate) Publish(state with { PendingEmigrations = [.. state.PendingEmigrations, request] }, persist: false);
        try { return await answer.Task.WaitAsync(cancellationToken); }
        finally
        {
            emigrationAnswers.TryRemove(request.Id, out _);
            lock (gate)
                if (state.PendingEmigrations.Any(pending => pending.Id == request.Id))
                    Publish(state with { PendingEmigrations = state.PendingEmigrations.Where(pending => pending.Id != request.Id).ToArray() }, persist: false);
        }
    }

    /// <summary>A client answered a revived tab's resume offer: "resume", "fresh" or "quit" (end the stray owner, then resume). Raised after the change applied.</summary>
    public event Action<TerminalRef, string>? ResumeAnswered;

    /// <summary>Publishes or replaces a revived tab's resume offer; never persisted.</summary>
    public void OfferResume(TerminalResumeOffer offer)
    {
        lock (gate)
        {
            var offers = state.ResumeOffers.Where(known => known.Terminal != offer.Terminal).Append(offer).ToArray();
            if (!offers.SequenceEqual(state.ResumeOffers)) Publish(state with { ResumeOffers = offers }, persist: false);
        }
    }

    /// <summary>Withdraws a tab's resume offer, returning it; null when it had none.</summary>
    public TerminalResumeOffer? WithdrawResume(Func<TerminalRef, bool> terminal)
    {
        lock (gate)
        {
            var found = state.ResumeOffers.FirstOrDefault(offer => terminal(offer.Terminal));
            if (found is not null) Publish(state with { ResumeOffers = state.ResumeOffers.Where(offer => !terminal(offer.Terminal)).ToArray() }, persist: false);
            return found;
        }
    }

    /// <summary>Publishes an approved emigration whose relaunch failed so every client can notify; kept for the last 16, never persisted.</summary>
    public void PublishEmigrationFailure(TerminalEmigrationRequest failure)
    {
        lock (gate) Publish(state with { FailedEmigrations = [.. state.FailedEmigrations.TakeLast(15), failure] }, persist: false);
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
            namespaces[id] = JsonSerializer.SerializeToElement(value, HostJson.Default.JsonElement);
            Publish(state with { PluginSettings = namespaces }, persist: true);
        }
    }

    public IAsyncEnumerable<HostState> WatchAsync(CancellationToken cancellationToken = default) => WatchAsync(visible: true, cancellationToken);

    /// <summary>
    /// The stored state, folders gone or not: the host's own lifecycle (plugin workspace events) must not take a folder that is
    /// briefly missing, such as an unmounted volume, for a removed workspace and forget what belongs to it.
    /// </summary>
    internal IAsyncEnumerable<HostState> WatchStoredAsync(CancellationToken cancellationToken = default) => WatchAsync(visible: false, cancellationToken);

    private async IAsyncEnumerable<HostState> WatchAsync(bool visible, [EnumeratorCancellation] CancellationToken cancellationToken)
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
                if (latest is not null) yield return visible ? Visible(latest) : latest;
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
            if (!WriteInBackground) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stored = new Stored
            {
                Settings = snapshot.Settings,
                Presets = snapshot.Presets.ToList(),
                KeymapPresets = snapshot.KeymapPresets.ToList(),
                Projects = snapshot.Projects.ToList(),
                RecentProjects = snapshot.RecentProjects.ToList(),
                ProjectRecords = snapshot.ProjectRecords.ToList(),
                WorkspaceLanguages = snapshot.WorkspaceLanguages.ToDictionary(),
                ProjectLanguageAutostart = snapshot.ProjectLanguageAutostart.ToDictionary(),
                ProjectLanguageHidden = snapshot.ProjectLanguageHidden.ToDictionary(),
                TereditorExcludeDeclined = snapshot.TereditorExcludeDeclined.ToList(),
                WorkspaceLabels = snapshot.WorkspaceLabels.ToDictionary(),
                ProjectLabels = snapshot.ProjectLabels.ToDictionary(),
                UnreadWorkspaces = snapshot.UnreadWorkspaces.ToDictionary(),
                WorkspaceBases = snapshot.WorkspaceBases.ToDictionary(),
                WorkspaceDiffBases = snapshot.WorkspaceDiffBases.ToDictionary(),
                Workspaces = snapshot.Workspaces.ToList(),
                PluginSettings = snapshot.PluginSettings.ToDictionary(),
                PluginPaths = snapshot.PluginPaths.ToList(),
                TerminalAgents = snapshot.TerminalAgents.ToList(),
                // A program's OSC title lives only as long as its shell.
                TerminalTitles = snapshot.TerminalTitles.Where(title => title.Source != TerminalTitleSource.Program).ToList(),
                WorkspaceCreators = workspaceCreators.Where(entry => snapshot.Workspaces.Any(workspace => workspace.Path == entry.Key)).ToDictionary()
            };
            var document = JsonSerializer.SerializeToNode(stored, Json.Info<Stored>())!;
            foreach (var (name, value) in unknownSettings) document[nameof(Stored.Settings)]![name] = value?.DeepClone();
            if (WriteInBackground) { WriteLater(document.ToJsonString(Json)); return; }
            WriteFile(document.ToJsonString(Json));
            LastError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
    }

    /// <summary>
    /// Writes the state file after the state's lock is released, newest snapshot only. A host whose file system is
    /// reached through another thread (a browser tab's) must not hold a lock the page's thread also takes while it waits.
    /// </summary>
    public bool WriteInBackground { get; set; }

    private readonly Lock writes = new();
    private string? pendingWrite;
    private bool writing;

    private void WriteLater(string json)
    {
        lock (writes)
        {
            pendingWrite = json;
            if (writing) return;
            writing = true;
        }
        _ = Task.Run(() =>
        {
            while (true)
            {
                string next;
                lock (writes)
                {
                    if (pendingWrite is null) { writing = false; return; }
                    next = pendingWrite;
                    pendingWrite = null;
                }
                try { WriteFile(next); LastError = null; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
            }
        });
    }

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path!, overwrite: true);
    }

    private static bool ValidPath(string value) => value.Length > 0 && !value.Contains('\0') && Path.IsPathFullyQualified(value);

    /// <summary>An accent is opaque to the host like a theme id: empty, a curated slug or a custom <c>#rrggbb</c>; the UI resolves it.</summary>
    private static bool ValidAccent(string value) => value.Length == 0 || Regex.IsMatch(value, "^(?:[a-z][a-z0-9-]{0,31}|#[0-9a-f]{6})$");

    private static bool ValidText(string value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');

    private static HostState Normalize(HostState value)
    {
        var light = Clean(value.Settings.SystemLight); var dark = Clean(value.Settings.SystemDark);
        // Half a system pair is malformed: the client falls back to its own default pair.
        if (light.Length == 0 || dark.Length == 0) light = dark = "";
        var normalized = value with
        {
            Revision = 0,
            TereditorExcludeDeclined = value.TereditorExcludeDeclined.Where(item => item is not null && ValidPath(item)).Distinct().Order(StringComparer.Ordinal).ToArray(),
            Settings = value.Settings with
            {
                ThemeMode = value.Settings.ThemeMode is "fixed" ? "fixed" : "system",
                Theme = Clean(value.Settings.Theme),
                Accent = ValidAccent(Clean(value.Settings.Accent)) ? Clean(value.Settings.Accent) : "",
                SystemLight = light,
                SystemDark = dark,
                FileLineWidth = Width(value.Settings.FileLineWidth),
                MarkdownLineWidth = Width(value.Settings.MarkdownLineWidth),
                EditorTabWidth = value.Settings.EditorTabWidth is >= HostSettings.MinEditorTabWidth and <= HostSettings.MaxEditorTabWidth ? value.Settings.EditorTabWidth : 0,
                TerminalReplayKb = Math.Clamp(value.Settings.TerminalReplayKb, 0, HostSettings.MaxTerminalReplayKb),
                BellFeatures = HostSettings.NormalizeBellFeatures(value.Settings.BellFeatures ?? "") is { } bell && value.Settings.BellFeatures is { Length: > 0 } ? bell : HostSettings.DefaultBellFeatures,
                TerminalUndoCloseSeconds = Math.Clamp(value.Settings.TerminalUndoCloseSeconds, 0, HostSettings.MaxTerminalUndoCloseSeconds),
                CustomHighlighting = ValidHighlighting(value.Settings.CustomHighlighting) ? value.Settings.CustomHighlighting : "",
                WindowDecorations = value.Settings.WindowDecorations is "client" or "system" ? value.Settings.WindowDecorations : "",
                ActiveKeymap = value.KeymapPresets.Any(preset => preset is not null && preset.Name == value.Settings.ActiveKeymap) ? value.Settings.ActiveKeymap : ""
            },
            Presets = value.Presets.Where(preset => preset is not null && ValidText(preset.Name) && preset.Layout is { Length: > 0 })
                .DistinctBy(preset => preset.Name).ToArray(),
            KeymapPresets = value.KeymapPresets.Where(preset => preset is not null && Keymaps.ValidateName(preset.Name, []) is null && KeymapBindings.IsValid(preset.Bindings))
                .DistinctBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
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
            .ToDictionary(entry => entry.Key, entry => JsonSerializer.SerializeToElement(entry.Value, HostJson.Default.JsonElement)),
            PluginPaths = value.PluginPaths.Where(item => item is not null && ValidPath(item)).Distinct().ToArray(),
            Plugins = [],
            TerminalAgents = value.TerminalAgents.Where(agent => agent?.Terminal is { WorkspaceId: { } workspace, TabKey: { Length: > 0 } } && ValidPath(workspace) &&
                agent.Record is { Kind: not null, Command: not null }).DistinctBy(agent => agent.Terminal).ToArray(),
            TerminalTitles = value.TerminalTitles.Where(title => title?.Terminal is { WorkspaceId: { } workspace, TabKey: { Length: > 0 } } && ValidPath(workspace) &&
                !string.IsNullOrWhiteSpace(title.Title)).DistinctBy(title => title.Terminal).ToArray(),
            HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? home : null,
            Platform = OperatingSystem.IsMacOS() ? HostPlatform.MacOS : OperatingSystem.IsWindows() ? HostPlatform.Windows : HostPlatform.Linux,
            Workspaces = value.Workspaces.Where(ValidWorkspace).DistinctBy(workspace => workspace.Id).DistinctBy(workspace => workspace.Path).ToArray(),
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
            case "terminal-resume-answer":
                if (value is not ("resume" or "fresh" or "quit") || JsonSerializer.Deserialize(key, HostJson.Default.TerminalRef) is null)
                    throw new ArgumentException("A resume answer names a terminal and is resume, fresh or quit.");
                return current;
            case "terminal-emigration-answer":
                if (value is not ("approve" or "deny")) throw new ArgumentException("An emigration answer is approve or deny.");
                return current.PendingEmigrations.Any(pending => pending.Id == key)
                    ? current with { PendingEmigrations = current.PendingEmigrations.Where(pending => pending.Id != key).ToArray() } : current;
            case "terminal-title":
                var terminal = JsonSerializer.Deserialize(key, HostJson.Default.TerminalRef) ?? throw new ArgumentException("A terminal is required.");
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
                    "accent" when ValidAccent(value) => settings with { Accent = value },
                    "theme-mode" when value is "fixed" or "system" => settings with { ThemeMode = value },
                    "system-light" when ValidText(value) => settings with { SystemLight = value.Trim() },
                    "system-dark" when ValidText(value) => settings with { SystemDark = value.Trim() },
                    "file-width" when Number(value) is { } width => settings with { FileLineWidth = width },
                    "file-bounded" when bool.TryParse(value, out var bounded) => settings with { FileLineWidthBounded = bounded },
                    "markdown-width" when Number(value) is { } width => settings with { MarkdownLineWidth = width },
                    "markdown-bounded" when bool.TryParse(value, out var bounded) => settings with { MarkdownLineWidthBounded = bounded },
                    "editor-tab-width" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var tab) && tab is >= HostSettings.MinEditorTabWidth and <= HostSettings.MaxEditorTabWidth
                        => settings with { EditorTabWidth = tab },
                    "editor-line-numbers" when bool.TryParse(value, out var numbers) => settings with { EditorLineNumbers = numbers },
                    "terminal-replay" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) && kb <= HostSettings.MaxTerminalReplayKb
                        => settings with { TerminalReplayKb = kb },
                    "terminal-keep-after-exit" when bool.TryParse(value, out var keep) => settings with { TerminalKeepAfterExit = keep },
                    "terminal-undo-close" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= HostSettings.MaxTerminalUndoCloseSeconds
                        => settings with { TerminalUndoCloseSeconds = seconds },
                    "file-type-icon-style" when value is "plain" or "" => settings with { FileTypeIconStyle = "" },
                    "file-type-icon-style" when value is "colored" or "material" => settings with { FileTypeIconStyle = value },
                    "file-type-icons" when bool.TryParse(value, out var icons) => settings with { FileTypeIconsEnabled = icons },
                    "shell-integration" when bool.TryParse(value, out var integrate) => settings with { ShellIntegrationEnabled = integrate },
                    "lsp-enabled" when bool.TryParse(value, out var lspEnabled) => settings with { LspEnabled = lspEnabled },
                    "java-kotlin-provider" when IsProviderChoice(value) => settings with { JavaKotlinProvider = value },
                    "notifications" when bool.TryParse(value, out var enabled) => settings with { NotificationsEnabled = enabled },
                    "terminal-notifications" when bool.TryParse(value, out var programs) => settings with { TerminalNotificationsEnabled = programs },
                    "terminal-option-as-alt" when bool.TryParse(value, out var optionAsAlt) => settings with { TerminalOptionAsAlt = optionAsAlt },
                    "bell-features" when HostSettings.NormalizeBellFeatures(value) is { } bell => settings with { BellFeatures = bell },
                    _ when key.StartsWith("language-server:", StringComparison.Ordinal) => settings with { LanguageServers = SetLanguageServer(settings.LanguageServers, key[16..], value) },
                    _ when key.StartsWith("language-tool:", StringComparison.Ordinal) => settings with { LanguageTools = SetLanguageTool(settings.LanguageTools, key[14..], value) },
                    "language-limits" => settings with { LanguageLimits = value.Length == 0 ? "" : Language.LanguageServerLimits.Parse(value).Serialize() },
                    "terminal-memory" => settings with { TerminalMemory = value.Length == 0 ? "" : TerminalMemoryLimits.Parse(value).Serialize() },
                    "custom-highlighting" when ValidHighlighting(value) => settings with { CustomHighlighting = value },
                    "vertical-tabs" when bool.TryParse(value, out var vertical) => settings with { VerticalCenterTabs = vertical },
                    "window-decorations" when value is "auto" or "" => settings with { WindowDecorations = "" },
                    "window-decorations" when value is "client" or "system" => settings with { WindowDecorations = value },
                    "keymap" when value.Length == 0 || Keymaps.IsBuiltin(value) => settings with { ActiveKeymap = "" },
                    "keymap" when current.KeymapPresets.Any(preset => preset.Name == value) => settings with { ActiveKeymap = value },
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
            case "keymap-save":
                {
                    var name = key.Trim();
                    var others = current.KeymapPresets.Where(preset => preset.Name != name).Select(preset => preset.Name);
                    if (Keymaps.ValidateName(name, others) is { } problem) throw new ArgumentException(problem);
                    if (!KeymapBindings.IsValid(value)) throw new ArgumentException("The keymap is not valid.");
                    var presets = current.KeymapPresets.Where(preset => preset.Name != name).ToList();
                    var position = current.KeymapPresets.ToList().FindIndex(preset => preset.Name == name);
                    presets.Insert(position >= 0 ? position : presets.Count, new(name, value));
                    return current with { KeymapPresets = presets };
                }
            case "keymap-rename":
                {
                    var next = value.Trim();
                    if (!current.KeymapPresets.Any(preset => preset.Name == key)) return current;
                    if (Keymaps.ValidateName(next, current.KeymapPresets.Select(preset => preset.Name), except: key) is { } problem) throw new ArgumentException(problem);
                    return current with
                    {
                        KeymapPresets = current.KeymapPresets.Select(preset => preset.Name == key ? preset with { Name = next } : preset).ToArray(),
                        Settings = current.Settings.ActiveKeymap == key ? current.Settings with { ActiveKeymap = next } : current.Settings
                    };
                }
            case "keymap-delete":
                return current.KeymapPresets.Any(preset => preset.Name == key)
                    ? current with
                    {
                        KeymapPresets = current.KeymapPresets.Where(preset => preset.Name != key).ToArray(),
                        Settings = current.Settings.ActiveKeymap == key ? current.Settings with { ActiveKeymap = "" } : current.Settings
                    }
                    : current;
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
            case "tereditor-exclude-declined":
                if (!ValidPath(key)) throw new ArgumentException("Invalid project path.");
                return current.TereditorExcludeDeclined.Contains(key) ? current : current with { TereditorExcludeDeclined = [.. current.TereditorExcludeDeclined, key] };
            case "project-open":
                if (!ValidPath(key)) throw new ArgumentException("Invalid project path.");
                if (current.Projects.Contains(key) && !current.RecentProjects.Contains(key)) return current;
                // A linked worktree of an open project is that project's workspace, never a second project.
                var owner = current.Workspaces.FirstOrDefault(workspace => workspace.Path == key && workspace.ProjectRoot != key)?.ProjectRoot ?? ProjectPaths.LinkedWorktreeOwner(key);
                if (owner is not null && owner != key && current.Projects.Contains(owner))
                    throw new HostException(HostErrorCode.AlreadyOpen, $"This folder is already open in tereditor as a workspace: {key}");
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
                try { paths = JsonSerializer.Deserialize(value, HostJson.Default.StringArray) ?? throw new JsonException(); }
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
        var element = JsonSerializer.SerializeToElement(merged, HostJson.Default.JsonObject);
        if (patch is not null && PluginNamespaceValidator is { } validate) element = validate(id, element);
        namespaces[id] = element;
        if (patch?["enabled"]?.GetValue<bool>() == false && PluginDependents is { } dependents)
            foreach (var (dependent, cause) in dependents(id, current.PluginSettings))
            {
                var off = new JsonObject { ["enabled"] = false };
                // The memory of what was switched off goes in the same batch; an older mark is kept.
                if (cause is not null && !(namespaces.GetValueOrDefault(dependent) is { ValueKind: JsonValueKind.Object } held && held.TryGetProperty("suspendedBy", out _)))
                    off["suspendedBy"] = cause;
                namespaces[dependent] = JsonSerializer.SerializeToElement(Merge(namespaces.GetValueOrDefault(dependent), off), HostJson.Default.JsonObject);
            }
        var changed = namespaces.Count != current.PluginSettings.Count || namespaces.Any(entry =>
            !current.PluginSettings.TryGetValue(entry.Key, out var previous) || !JsonElement.DeepEquals(previous, entry.Value));
        return changed ? current with { PluginSettings = namespaces } : current;
    }

    private static JsonObject Merge(JsonElement existing, JsonObject patch)
    {
        var merged = existing.ValueKind == JsonValueKind.Object ? JsonSerializer.SerializeToNode(existing, HostJson.Default.JsonElement)!.AsObject() : new JsonObject();
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