using System.Text.Json;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.AirContext.Host;

/// <summary>
/// Moves what Air Context kept in the host before it became a plugin into the plugin's own state: the CLI storage
/// directory (agreement receipts and the downloaded executable) and the list of workspaces the user enabled. The host
/// calls it once while opening a state file that still carries the old list or directory.
/// </summary>
public static class AirContextMigration
{
    /// <summary>The directory the host kept Context in, beside its state file.</summary>
    public const string LegacyDirectory = "context";

    /// <summary>Moves the legacy storage and writes the enabled workspaces, keeping any plugin state that already exists.</summary>
    /// <param name="stateDirectory">The host state directory.</param>
    /// <param name="workspaces">The workspaces the old state file listed as enabled.</param>
    public static void Migrate(string stateDirectory, IReadOnlyList<string> workspaces)
    {
        var legacy = Path.Combine(stateDirectory, LegacyDirectory);
        var files = Path.Combine(stateDirectory, "plugin-state", AirContextContract.Id, "files");
        if (Directory.Exists(legacy) && !Directory.Exists(files))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(files)!);
            Directory.Move(legacy, files);
        }
        var state = Path.Combine(stateDirectory, PluginIdentity.StateFile(AirContextContract.Id, AirContextContract.WorkspacesState));
        if (workspaces.Count == 0 || File.Exists(state)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        var temporary = state + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new ContextWorkspaces([.. workspaces]), PluginJson.Options));
        File.Move(temporary, state, overwrite: true);
    }
}