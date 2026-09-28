using System.Text.Json;
using SharpRail.UI.Docking;

namespace SharpRail.UI.State;

public sealed class Preferences
{
    public string Theme { get; set; } = "dark";
    public double PreviewWidth { get; set; } = 900;
    public bool BoundPreviewWidth { get; set; } = true;
    public double FontSize { get; set; } = 14;
    public bool ShowHiddenFiles { get; set; }
    public string DefaultPreset { get; set; } = "balanced";
    public Dictionary<string, DockState> CustomPresets { get; set; } = [];
}

public sealed class Profile
{
    public Preferences Preferences { get; set; } = new();
    public DockState Layout { get; set; } = DockState.Preset("balanced");
    public List<string> Projects { get; set; } = [];
    public string LastProject { get; set; } = "";
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
    public Profile Data { get; }
    public string? LastError { get; private set; }
    public ProfileStore(string directory)
    {
        path = Path.Combine(directory, "profile.json");
        try
        {
            Data = File.Exists(path) ? JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)) ?? new() : new();
            Data.Preferences ??= new(); Data.Projects ??= []; Data.LastProject ??= "";
            Data.Projects.RemoveAll(project => string.IsNullOrWhiteSpace(project) || project.Contains('\0') || !Path.IsPathFullyQualified(project));
            if (Data.LastProject.Contains('\0') || Data.LastProject.Length > 0 && !Path.IsPathFullyQualified(Data.LastProject)) Data.LastProject = "";
            Data.Preferences.CustomPresets ??= [];
            if (!LayoutSession.IsValid(Data.Layout)) Data.Layout = DockState.Preset("balanced");
            if (!double.IsFinite(Data.Preferences.FontSize) || Data.Preferences.FontSize is < 10 or > 24) Data.Preferences.FontSize = 14;
            if (!double.IsFinite(Data.Preferences.PreviewWidth) || Data.Preferences.PreviewWidth is < 320 or > 2400) Data.Preferences.PreviewWidth = 900;
            foreach (var name in Data.Preferences.CustomPresets.Keys.Where(name => string.IsNullOrWhiteSpace(name) ||
                !LayoutSession.IsValid(Data.Preferences.CustomPresets[name])).ToArray()) Data.Preferences.CustomPresets.Remove(name);
            if (Data.Preferences.Theme is not ("dark" or "light" or "system")) Data.Preferences.Theme = "dark";
            Data.Preferences.DefaultPreset ??= "balanced";
            if (Data.Preferences.DefaultPreset is not ("balanced" or "focus" or "review") &&
                !Data.Preferences.CustomPresets.ContainsKey(Data.Preferences.DefaultPreset)) Data.Preferences.DefaultPreset = "balanced";
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            Data = new(); LastError = error.Message;
        }
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
