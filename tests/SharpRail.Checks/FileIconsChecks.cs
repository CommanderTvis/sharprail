using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Checks;

internal static partial class FileIconsChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Host(string root)
    {
        Names();
        foreach (var name in FileIconTable.ByName.Values.Concat(FileIconTable.ByExtension.Values).Append(FileIconTable.Fallback).Distinct())
        {
            var bytes = FileTypeIcons.Read(name);
            Require(bytes is not null && System.Text.Encoding.UTF8.GetString(bytes).Contains("currentColor", StringComparison.Ordinal),
                "Every named core icon is embedded in the shared kit: " + name);
        }
        await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = Path.Combine(root, "icons-roster"), State = new HostStateStore(null) });
        await runtime.Start();
        Require(!(await runtime.ListAsync()).Any(entry => entry.Id == "file-icons"), "File type icons are not a builtin plugin.");
        foreach (var enabled in new[] { false, true })
        {
            var directory = Path.Combine(root, "icons-migration-" + enabled);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, HostStateStore.FileName), JsonSerializer.Serialize(new
            {
                PluginSettings = new Dictionary<string, object> { ["file-icons"] = new { enabled } }
            }));
            var store = new HostStateStore(directory);
            Require(store.Current.Settings.FileTypeIconsEnabled == enabled && !store.Current.PluginSettings.ContainsKey("file-icons"),
                "An old plugin preference migrates to the core setting and leaves no orphan namespace.");
            await store.ChangeAsync([HostStateChange.Setting("file-type-icons", (!enabled).ToString())]);
            Require(new HostStateStore(directory).Current.Settings.FileTypeIconsEnabled != enabled, "The core setting persists independently of plugin state.");
            File.WriteAllText(Path.Combine(directory, HostStateStore.FileName), JsonSerializer.Serialize(new
            {
                Settings = new { FileTypeIconsEnabled = enabled },
                PluginSettings = new Dictionary<string, object> { ["file-icons"] = new { enabled = !enabled } }
            }));
            Require(new HostStateStore(directory).Current.Settings.FileTypeIconsEnabled == enabled, "An explicit core preference takes precedence over stale plugin settings.");
        }
        Require(new HostStateStore(null).Current.Settings.FileTypeIconsEnabled, "File type icons are enabled by default.");
        Console.WriteLine("PASS core file icons: name precedence, embedded SVGs, plugin removal, default and persisted migration");
    }

    private static void Names()
    {
        Require(FileTypeIcons.Name("src/Main.kt") == "kotlin" && FileTypeIcons.Name("data.JSON") == "json" && FileTypeIcons.Name("/abs/path/app.tsx") == "react_ts",
            "An extension names the icon, whatever case it arrives in.");
        Require(FileTypeIcons.Name("vitest.config.ts") == "vitest" && FileTypeIcons.Name("Dockerfile") == "docker" && FileTypeIcons.Name("package.json") == "nodejs",
            "A whole filename wins over its extension.");
        Require(FileTypeIcons.Name("types/api.d.ts") == "typescript-def" && FileTypeIcons.Name("types/api.ts") == "typescript", "A longer extension wins over a shorter one.");
        Require(FileTypeIcons.Name(".gitignore") == "git" && FileTypeIcons.Name(".env") == "tune", "A dotfile is read the same way.");
        Require(FileTypeIcons.Name("notes.qqqq") == "file" && FileTypeIcons.Name("LICENSE-something") == "file", "Anything unrecognised falls back to the plain file icon.");
        Require(FileTypeIcons.Read("typescript") is { Length: > 0 } && FileTypeIcons.Read("missing") is null,
            "Bundled icon lookup reads local embedded resources, with a missing icon falling back.");
    }

}