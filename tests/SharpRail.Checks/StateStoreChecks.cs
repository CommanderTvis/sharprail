using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>What the state store keeps on disk: forward-compatible settings, theme normalization and the installation identity.</summary>
internal static class StateStoreChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "state-store");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, HostStateStore.FileName);
        File.WriteAllText(file, """
            { "Settings": { "Theme": "dark", "ThemeMode": "system", "SystemLight": "light", "SystemDark": "",
                "FutureFlag": true, "FutureShape": { "depth": [1, 2] } } }
            """);
        var store = new HostStateStore(directory);
        Require(store.Current.Settings is { Theme: "dark", ThemeMode: "system", SystemLight: "", SystemDark: "" },
            "Half a system theme pair must be dropped on load without discarding valid siblings.");
        await store.ChangeAsync([HostStateChange.Setting("file-width", "100")]);
        var settings = JsonNode.Parse(File.ReadAllText(file))!["Settings"]!;
        Require(settings["FutureFlag"]!.GetValue<bool>() && settings["FutureShape"]!["depth"]!.AsArray().Count == 2 &&
            settings["FileLineWidth"]!.GetValue<int>() == 100, "A valid update must keep settings this host does not know.");
        Require(new HostStateStore(directory).Current.Settings.FileLineWidth == 100, "State with unknown settings must load again.");
        try
        {
            await store.ChangeAsync([HostStateChange.Setting("FutureFlag", "false")]);
            throw new InvalidOperationException("An unknown setting key was accepted as a change.");
        }
        catch (ArgumentException) { }

        var legacy = await store.ChangeAsync([HostStateChange.Setting("theme", "light")]);
        Require(legacy.Settings is { Theme: "light", ThemeMode: "fixed" }, "A theme change without a mode must switch the host to fixed mode.");
        var paired = await store.ChangeAsync([HostStateChange.Setting("theme-mode", "system"), HostStateChange.Setting("theme", "dark")]);
        Require(paired.Settings is { Theme: "dark", ThemeMode: "system" }, "An explicit mode in the same batch wins over the legacy rule.");

        var id = store.InstallationId;
        Require(Guid.TryParse(id, out _) && File.Exists(Path.Combine(directory, Installation.FileName)), "A state directory must receive an installation identity.");
        Require(new HostStateStore(directory).InstallationId == id && Installation.EnsureIn(directory) == id, "The installation identity must be stable.");
        Require(new HostStateStore(null).InstallationId is null, "A memory-only store has no installation identity.");
        var raced = Path.Combine(root, "state-installation-race");
        var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Installation.EnsureIn(raced))));
        Require(ids.Distinct().Count() == 1, "Racing first launches must agree on one installation identity.");
        File.WriteAllText(Path.Combine(raced, Installation.FileName), "[]");
        try { Installation.EnsureIn(raced); throw new InvalidOperationException("A malformed installation file was replaced."); }
        catch (IOException) { }
        Console.WriteLine("PASS host state keeps unknown settings, normalizes theme changes and mints one installation identity");
    }
}