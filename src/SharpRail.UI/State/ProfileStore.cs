using System.Text.Json;
using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.State;

public sealed class Preferences
{
    /// <summary>The fixed theme's manifest id, kept opaque so an unavailable theme survives until another is chosen.</summary>
    public string Theme { get; set; } = Themes.DefaultId;
    public string ThemeMode { get; set; } = "fixed";
    public SystemThemePair? SystemThemePair { get; set; }
    public int FileLineWidth { get; set; } = Rendering.LineWidths.FileDefault;
    public bool FileLineWidthBounded { get; set; } = true;
    public int MarkdownLineWidth { get; set; } = Rendering.LineWidths.MarkdownDefault;
    public bool MarkdownLineWidthBounded { get; set; } = true;
    public double FontSize { get; set; } = 14;
    public bool ShowHiddenFiles { get; set; }
    public string DefaultPreset { get; set; } = "balanced";
    public Dictionary<string, DockState> CustomPresets { get; set; } = [];
}

public sealed class SystemThemePair
{
    public string Light { get; set; } = "";
    public string Dark { get; set; } = "";
}

public record GitSelection(string Target, string Scope, GitCommit? Commit);

public sealed class Profile
{
    public Preferences Preferences { get; set; } = new();
    public DockState Layout { get; set; } = DockState.Preset("balanced");
    public List<string> Projects { get; set; } = [];
    public HashSet<string> CollapsedProjects { get; set; } = [];
    public string LastProject { get; set; } = "";
    public Dictionary<string, GitSelection> GitSelections { get; set; } = [];
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
    public string DirectoryPath => Path.GetDirectoryName(path)!;
    public Profile Data { get; }
    public string? LastError { get; private set; }
    public ProfileStore(string directory)
    {
        path = Path.Combine(directory, "profile.json");
        try
        {
            Data = File.Exists(path) ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)) ?? new() : new();
            Data.Preferences ??= new(); Data.Projects ??= []; Data.LastProject ??= "";
            Data.CollapsedProjects ??= [];
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
            Data.Projects.RemoveAll(project => string.IsNullOrWhiteSpace(project) || project.Contains('\0') || !Path.IsPathFullyQualified(project));
            if (Data.LastProject.Contains('\0') || Data.LastProject.Length > 0 && !Path.IsPathFullyQualified(Data.LastProject)) Data.LastProject = "";
            Data.Preferences.CustomPresets ??= [];
            if (!LayoutSession.IsValid(Data.Layout)) Data.Layout = DockState.Preset("balanced");
            if (!double.IsFinite(Data.Preferences.FontSize) || Data.Preferences.FontSize is < 10 or > 24) Data.Preferences.FontSize = 14;
            if (!Rendering.LineWidths.IsValid(Data.Preferences.FileLineWidth)) Data.Preferences.FileLineWidth = Rendering.LineWidths.FileDefault;
            if (!Rendering.LineWidths.IsValid(Data.Preferences.MarkdownLineWidth)) Data.Preferences.MarkdownLineWidth = Rendering.LineWidths.MarkdownDefault;
            foreach (var name in Data.Preferences.CustomPresets.Keys.Where(name => string.IsNullOrWhiteSpace(name) ||
                !LayoutSession.IsValid(Data.Preferences.CustomPresets[name])).ToArray()) Data.Preferences.CustomPresets.Remove(name);
            NormalizeTheme(Data.Preferences);
            Data.Preferences.DefaultPreset ??= "balanced";
            if (Data.Preferences.DefaultPreset is not ("balanced" or "focus" or "review") &&
                !Data.Preferences.CustomPresets.ContainsKey(Data.Preferences.DefaultPreset)) Data.Preferences.DefaultPreset = "balanced";
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            Data = new(); LastError = error.Message;
        }
    }

    private static void NormalizeTheme(Preferences preferences)
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            LastError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastError = error.Message; }
    }
}
