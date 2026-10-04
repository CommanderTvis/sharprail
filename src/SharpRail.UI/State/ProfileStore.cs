using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.State;

public sealed class Preferences
{
    /// <summary>The fixed theme's manifest id, kept opaque so an unavailable theme survives until another is chosen.</summary>
    public string Theme { get; set; } = Themes.DefaultId;
    public string ThemeMode { get; set; } = "fixed";
    public SystemThemePair? SystemThemePair { get; set; }
    public int FileLineWidth { get; set; } = LineWidths.FileDefault;
    public bool FileLineWidthBounded { get; set; } = true;
    public int MarkdownLineWidth { get; set; } = LineWidths.MarkdownDefault;
    public bool MarkdownLineWidthBounded { get; set; } = true;
    public Dictionary<string, DockState> CustomPresets { get; set; } = [];
    public double FontSize { get; set; } = 14;
    /// <summary>Browser-style page zoom shared by every window; Mod+=, Mod+- and Mod+0 step it.</summary>
    public double Zoom { get; set; } = 1;
    /// <summary>The local terminal renderer: composited Metal texture or Skia cells.</summary>
    public string TerminalRenderer { get; set; } = Terminal.TerminalRenderers.Texture;
    /// <summary>A single click previews into the group's reusable slot; off, every open keeps its tab.</summary>
    public bool PreviewTabs { get; set; } = true;
    /// <summary>Centre tabs as a column beside the editor instead of a strip above it; only while the centre is one group.</summary>
    public bool VerticalCenterTabs { get; set; }
    /// <summary>The vertical column's width in pixels, so it keeps its size when the window resizes.</summary>
    public double VerticalCenterTabsWidth { get; set; } = VerticalTabs.DefaultWidth;
    /// <summary>With vertical tabs on, the column lives under its workspace's row in Projects.</summary>
    public bool VerticalTabsInProjects { get; set; }
    /// <summary>How two tabs first shown together are arranged: <c>horizontal</c> columns or <c>vertical</c> rows.</summary>
    public string DefaultPaneDirection { get; set; } = "horizontal";
    /// <summary>Read from profiles that predate window-local defaults.</summary>
    [JsonPropertyName("DefaultPreset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDefaultPreset { get; set; }

    /// <summary>Properties the host shares; the profile writes them only until they migrate to host state.</summary>
    internal static readonly string[] Shared =
    [
        nameof(Theme), nameof(ThemeMode), nameof(SystemThemePair), nameof(FileLineWidth), nameof(FileLineWidthBounded),
        nameof(MarkdownLineWidth), nameof(MarkdownLineWidthBounded), nameof(CustomPresets)
    ];
}

public sealed class SystemThemePair
{
    public string Light { get; set; } = "";
    public string Dark { get; set; } = "";
}

public record GitSelection(string Target, string Scope, GitCommit? Commit);

/// <summary>State one window restores: its frame, window-local default preset and last location.</summary>
public sealed class WindowProfile
{
    public DockState Layout { get; set; } = DockState.Preset("balanced");
    public string DefaultPreset { get; set; } = "balanced";
    public string LastProject { get; set; } = "";
    public string LastProjectRoot { get; set; } = "";
    public bool LastAtHome { get; set; }
    /// <summary>The companion open beside each terminal, by <c>workspace\ntab key</c>: <c>plugin id:kind</c>. View state of this window.</summary>
    public Dictionary<string, string> Companions { get; set; } = [];
}

public sealed class Profile
{
    public Preferences Preferences { get; set; } = new();
    /// <summary>One entry per open window, restored in order at launch.</summary>
    public List<WindowProfile> Windows { get; set; } = [];
    public HashSet<string> CollapsedProjects { get; set; } = [];
    /// <summary>Remotes whose branches every branch picker shows collapsed.</summary>
    public HashSet<string> CollapsedRemotes { get; set; } = [];
    public Dictionary<string, GitSelection> GitSelections { get; set; } = [];
    /// <summary>Plugins' client-local preferences, keyed <c>endpoint|plugin:id:key</c>, so two hosts' plugins never share a value.</summary>
    public Dictionary<string, string> PluginPreferences { get; set; } = [];
    /// <summary>Set once shared fields have moved to the local host's <c>state.json</c>.</summary>
    public bool StateMigrated { get; set; }

    // Fields of older profiles, read for migration and cleared once migrated.
    [JsonPropertyName("Layout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DockState? LegacyLayout { get; set; }
    [JsonPropertyName("LastProject"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LegacyLastProject { get; set; }
    [JsonPropertyName("LastProjectRoot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LegacyLastProjectRoot { get; set; }
    [JsonPropertyName("LastAtHome"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? LegacyLastAtHome { get; set; }
    [JsonPropertyName("Projects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? LegacyProjects { get; set; }
    [JsonPropertyName("RecentProjects"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? LegacyRecentProjects { get; set; }
    [JsonPropertyName("WorkspaceLabels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, string>? LegacyWorkspaceLabels { get; set; }
}

public sealed class ProfileStore
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail");

    public static ProfileStore OpenDefault(string initialRoot)
    {
        var directory = DefaultDirectory;
        var legacy = Path.Combine(initialRoot, ".sharprail", "profile.json");
        var current = Path.Combine(directory, "profile.json");
        if (!File.Exists(current) && File.Exists(legacy))
        {
            Directory.CreateDirectory(directory);
            File.Move(legacy, current);
        }
        return new(directory);
    }

    private readonly string path;
    private readonly JsonSerializerOptions writing;
    public string DirectoryPath => Path.GetDirectoryName(path)!;
    public Profile Data { get; }
    public string? LastError { get; private set; }
    public ProfileStore(string directory)
    {
        path = Path.Combine(directory, "profile.json");
        writing = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipMigrated } }
        };
        try
        {
            Data = File.Exists(path) ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)) ?? new() : new();
            Data.Preferences ??= new(); Data.CollapsedProjects ??= []; Data.CollapsedRemotes ??= []; Data.Windows ??= [];
            Data.Windows.RemoveAll(window => window is null);
            if (Data.Windows.Count == 0)
                Data.Windows.Add(new()
                {
                    Layout = Data.LegacyLayout ?? DockState.Preset("balanced"),
                    DefaultPreset = Data.Preferences.LegacyDefaultPreset ?? "balanced",
                    LastProject = Data.LegacyLastProject ?? "",
                    LastProjectRoot = Data.LegacyLastProjectRoot ?? "",
                    LastAtHome = Data.LegacyLastAtHome ?? false
                });
            Data.LegacyLayout = null; Data.LegacyLastProject = null; Data.LegacyLastProjectRoot = null; Data.LegacyLastAtHome = null;
            Data.Preferences.LegacyDefaultPreset = null;
            foreach (var window in Data.Windows) NormalizeWindow(window);
            Data.LegacyProjects?.RemoveAll(project => !ValidPath(project));
            Data.LegacyRecentProjects?.RemoveAll(project => !ValidPath(project));
            if (Data.LegacyWorkspaceLabels is { } labels)
                foreach (var entry in labels.ToArray())
                    if (!ValidPath(entry.Key) || string.IsNullOrWhiteSpace(entry.Value) || entry.Value.Contains('\0'))
                        labels.Remove(entry.Key);
            Data.GitSelections ??= [];
            foreach (var entry in Data.GitSelections.ToArray())
            {
                if (!Path.IsPathFullyQualified(entry.Key) || entry.Key.Contains('\0') || entry.Value is null)
                { Data.GitSelections.Remove(entry.Key); continue; }
                var selection = entry.Value;
                var commit = selection.Commit;
                if (commit is not null && (commit.Sha is null || commit.Sha.Length is < 4 or > 64 ||
                    !commit.Sha.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f'))) commit = null;
                if (commit is not null) commit = commit with
                {
                    ShortSha = string.IsNullOrEmpty(commit.ShortSha) ? commit.Sha[..Math.Min(7, commit.Sha.Length)] : commit.ShortSha,
                    Subject = commit.Subject ?? "",
                    Author = commit.Author ?? "",
                    CommittedAt = commit.CommittedAt ?? ""
                };
                var scope = selection.Scope is "All changes" or "Uncommitted" or "Staged" or "Branch" or "Commit" ? selection.Scope : "All changes";
                if (scope == "Commit" && commit is null) scope = "All changes";
                Data.GitSelections[entry.Key] = new(selection.Target?.Contains('\0') == false ? selection.Target : "", scope, scope == "Commit" ? commit : null);
            }
            Data.Preferences.CustomPresets ??= [];
            if (!double.IsFinite(Data.Preferences.FontSize) || Data.Preferences.FontSize is < 10 or > 24) Data.Preferences.FontSize = 14;
            Data.Preferences.Zoom = InterfaceZoom.Normalize(Data.Preferences.Zoom);
            Data.Preferences.VerticalCenterTabsWidth = VerticalTabs.ClampWidth(Data.Preferences.VerticalCenterTabsWidth);
            if (Data.Preferences.DefaultPaneDirection is not ("horizontal" or "vertical")) Data.Preferences.DefaultPaneDirection = "horizontal";
            if (!LineWidths.IsValid(Data.Preferences.FileLineWidth)) Data.Preferences.FileLineWidth = LineWidths.FileDefault;
            if (!LineWidths.IsValid(Data.Preferences.MarkdownLineWidth)) Data.Preferences.MarkdownLineWidth = LineWidths.MarkdownDefault;
            if (Data.Preferences.TerminalRenderer is not (Terminal.TerminalRenderers.Texture or Terminal.TerminalRenderers.Skia))
                Data.Preferences.TerminalRenderer = Terminal.TerminalRenderers.Texture;
            foreach (var preset in Data.Preferences.CustomPresets.Values) preset?.MigrateLegacyTools();
            foreach (var name in Data.Preferences.CustomPresets.Keys.Where(name => string.IsNullOrWhiteSpace(name) ||
                !LayoutSession.IsValid(Data.Preferences.CustomPresets[name])).ToArray()) Data.Preferences.CustomPresets.Remove(name);
            NormalizeTheme(Data.Preferences);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            Data = new() { Windows = [new()] }; LastError = error.Message;
        }
    }

    private static bool ValidPath(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') && Path.IsPathFullyQualified(value);

    private static void NormalizeWindow(WindowProfile window)
    {
        window.Layout?.MigrateLegacyTools();
        if (!LayoutSession.IsValid(window.Layout!)) window.Layout = DockState.Preset("balanced");
        if (string.IsNullOrWhiteSpace(window.DefaultPreset)) window.DefaultPreset = "balanced";
        if (window.LastProject is null || window.LastProject.Length > 0 && !ValidPath(window.LastProject)) window.LastProject = "";
        if (window.LastProjectRoot is null || window.LastProjectRoot.Length > 0 && !ValidPath(window.LastProjectRoot)) window.LastProjectRoot = "";
    }

    internal static void NormalizeTheme(Preferences preferences)
    {
        static bool Valid(string? id) => !string.IsNullOrWhiteSpace(id) && !id.Contains('\0');
        // Profiles before the manifest catalogue stored "system" in place of a fixed theme id.
        if (preferences.Theme == "system")
        {
            preferences.Theme = Themes.DefaultId;
            preferences.ThemeMode = "system";
            preferences.SystemThemePair ??= Themes.DerivePair(Themes.DefaultId);
        }
        if (!Valid(preferences.Theme)) preferences.Theme = Themes.DefaultId;
        if (preferences.SystemThemePair is { } pair && (!Valid(pair.Light) || !Valid(pair.Dark))) preferences.SystemThemePair = null;
        if (preferences.ThemeMode is not ("fixed" or "system") || preferences.SystemThemePair is null) preferences.ThemeMode = "fixed";
    }

    /// <summary>
    /// Opens the local host's shared state beside this profile, seeding it once from the
    /// profile's pre-host fields. Those fields are cleared only after the state file is written.
    /// </summary>
    public HostStateStore OpenState()
    {
        var store = new HostStateStore(DirectoryPath, Data.StateMigrated ? null : MigratedState);
        if (store.LastError is not null || Data.StateMigrated) return store;
        Data.StateMigrated = true;
        Data.LegacyProjects = null; Data.LegacyRecentProjects = null; Data.LegacyWorkspaceLabels = null;
        Save();
        return store;
    }

    private HostState MigratedState()
    {
        var preferences = Data.Preferences;
        return new()
        {
            Settings = new()
            {
                Theme = preferences.Theme,
                ThemeMode = preferences.ThemeMode,
                SystemLight = preferences.SystemThemePair?.Light ?? "",
                SystemDark = preferences.SystemThemePair?.Dark ?? "",
                FileLineWidth = preferences.FileLineWidth,
                FileLineWidthBounded = preferences.FileLineWidthBounded,
                MarkdownLineWidth = preferences.MarkdownLineWidth,
                MarkdownLineWidthBounded = preferences.MarkdownLineWidthBounded
            },
            Presets = preferences.CustomPresets.Select(entry => new LayoutPreset(entry.Key, JsonSerializer.Serialize(entry.Value))).ToArray(),
            Projects = Data.LegacyProjects ?? [],
            RecentProjects = Data.LegacyRecentProjects ?? [],
            WorkspaceLabels = Data.LegacyWorkspaceLabels ?? []
        };
    }

    private void SkipMigrated(JsonTypeInfo type)
    {
        if (type.Type != typeof(Preferences)) return;
        foreach (var property in type.Properties.Where(property => Preferences.Shared.Contains(property.Name)))
            property.ShouldSerialize = (_, _) => !Data.StateMigrated;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Data, writing));
            File.Move(temporary, path, overwrite: true);
            LastError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
    }
}