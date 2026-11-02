using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Keymap model checks: chord spelling, platform variants, presets, conflicts and the terminal-reserved chords. Headless and pure.</summary>
internal static class KeymapChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Model()
    {
        // Spelling: modifiers in one order, the command key folded per platform, unknown input rejected.
        Require(KeyChord.TryParse("shift+mod+n", KeymapPlatform.Mac, out var chord) && chord.Text == "Mod+Shift+N", "A chord spells its modifiers in canonical order.");
        Require(KeyChord.TryParse("Meta+O", KeymapPlatform.Mac, out var meta) && meta.Text == "Mod+O", "Meta is the command key on macOS.");
        Require(KeyChord.TryParse("Ctrl+O", KeymapPlatform.Other, out var control) && control.Text == "Mod+O", "Ctrl is the command key elsewhere.");
        Require(KeyChord.TryParse("Ctrl+O", KeymapPlatform.Mac, out var realControl) && realControl.Text == "Ctrl+O", "Ctrl stays Ctrl on macOS.");
        Require(!KeyChord.TryParse("Mod+Nope", KeymapPlatform.Mac, out _) && !KeyChord.TryParse("Hyper+A", KeymapPlatform.Mac, out _) && !KeyChord.TryParse("", KeymapPlatform.Mac, out _),
            "Unknown keys, modifiers and empty text are not chords.");
        Require(chord.Display(KeymapPlatform.Mac) == "⇧⌘N" && chord.Display(KeymapPlatform.Other) == "Ctrl+Shift+N", "A chord shows with the platform's symbols.");

        // IntelliJ: both variants parse for every action, and no action shares a chord with another.
        foreach (var platform in new[] { KeymapPlatform.Mac, KeymapPlatform.Other })
        {
            var intellij = KeymapBindings.IntelliJ(platform);
            var seen = new Dictionary<KeyChord, string>();
            foreach (var action in KeymapActions.All)
            {
                var chords = intellij.Chords(action.Id, platform);
                Require(chords.Count > 0 && chords.Count == intellij.Spellings(action.Id).Count, $"IntelliJ binds {action.Id} on {platform}.");
                foreach (var each in chords)
                {
                    Require(Keymaps.Reserved(each, platform) is null || action.Id == KeymapActions.UndoClose, $"IntelliJ's {each} on {platform} must not be terminal-reserved.");
                    Require(seen.TryAdd(each, action.Id), $"{each} is bound to both {seen.GetValueOrDefault(each)} and {action.Id} on {platform}.");
                }
            }
        }
        Require(KeymapBindings.IntelliJ(KeymapPlatform.Other).Chords(KeymapActions.Back, KeymapPlatform.Other)[0].Text == "Alt+Left" &&
            KeymapBindings.IntelliJ(KeymapPlatform.Mac).Chords(KeymapActions.Back, KeymapPlatform.Mac)[0].Text == "Mod+OpenBracket", "Back differs between the platform variants.");

        // Names: non-empty, trimmed, unique, and never the built-in's.
        Require(Keymaps.ValidateName("  ", []) is not null && Keymaps.ValidateName("intellij", []) is not null && Keymaps.ValidateName("Mine", ["mine"]) is not null &&
            Keymaps.ValidateName(" Mine ", ["Other"]) is null && Keymaps.ValidateName("Mine", ["Mine"], except: "Mine") is null, "Preset names validate.");

        // Resolution: a missing or built-in name is IntelliJ; an action a preset does not mention keeps IntelliJ's chords.
        var mine = KeymapBindings.IntelliJ(KeymapPlatform.Other);
        mine.Remove(KeymapActions.Refresh);
        mine.Set(KeymapActions.ToggleRight, []);
        KeymapPreset[] presets = [new("Mine", mine.ToJson())];
        var effective = Keymaps.Effective(presets, "Mine", KeymapPlatform.Other);
        Require(effective[KeymapActions.Refresh].Count == 2 && effective[KeymapActions.ToggleRight].Count == 0, "An unlisted action keeps IntelliJ; an empty list unbinds.");
        Require(Keymaps.Effective(presets, "Gone", KeymapPlatform.Other)[KeymapActions.ToggleRight].Count == 1 && Keymaps.Effective(presets, "IntelliJ", KeymapPlatform.Other)[KeymapActions.ToggleRight].Count == 1,
            "An unknown or built-in active name resolves to IntelliJ.");

        // Binding: conflicts name the other action, Reassign takes the chord, reserved chords are refused, two chords at most.
        var bindings = KeymapBindings.IntelliJ(KeymapPlatform.Other);
        Require(KeyChord.TryParse("Mod+J", KeymapPlatform.Other, out var toggleRight), "parse");
        var clash = Keymaps.Bind(bindings, KeymapActions.ToggleLeft, 0, toggleRight, KeymapPlatform.Other);
        Require(!clash.Applied && clash.Conflicts.SequenceEqual([KeymapActions.ToggleRight]) && bindings.Chords(KeymapActions.ToggleLeft, KeymapPlatform.Other)[0].Text == "Mod+B", "A taken chord is a conflict that changes nothing.");
        var moved = Keymaps.Bind(bindings, KeymapActions.ToggleLeft, 0, toggleRight, KeymapPlatform.Other, reassign: true);
        Require(moved.Applied && moved.Bindings.Chords(KeymapActions.ToggleRight, KeymapPlatform.Other).Count == 0 && moved.Bindings.Chords(KeymapActions.ToggleLeft, KeymapPlatform.Other)[0] == toggleRight, "Reassign moves the chord.");
        Require(KeyChord.TryParse("Ctrl+C", KeymapPlatform.Other, out var copy) && !Keymaps.Bind(bindings, KeymapActions.ToggleLeft, 0, copy, KeymapPlatform.Other).Applied &&
            Keymaps.Bind(bindings, KeymapActions.ToggleLeft, 0, copy, KeymapPlatform.Other).Reserved is not null, "Ctrl+C is the terminal's.");
        Require(KeyChord.TryParse("Cmd+K", KeymapPlatform.Mac, out _) == false && KeyChord.TryParse("Meta+K", KeymapPlatform.Mac, out var clear) && Keymaps.Reserved(clear, KeymapPlatform.Mac) is not null, "Cmd+K clears terminals.");
        Require(KeyChord.TryParse("Mod+Shift+K", KeymapPlatform.Other, out var free), "parse");
        var second = Keymaps.Bind(bindings, KeymapActions.ToggleLeft, 1, free, KeymapPlatform.Other);
        Require(second.Applied && second.Bindings.Chords(KeymapActions.ToggleLeft, KeymapPlatform.Other).Count == 2, "An action takes a second chord.");
        var cleared = Keymaps.Bind(second.Bindings, KeymapActions.ToggleLeft, 0, null, KeymapPlatform.Other);
        Require(cleared.Applied && cleared.Bindings.Chords(KeymapActions.ToggleLeft, KeymapPlatform.Other).SequenceEqual([free]), "Clearing a slot drops that chord.");
        Require(Keymaps.Reset(cleared.Bindings, bindings, KeymapActions.ToggleLeft, KeymapPlatform.Other).Chords(KeymapActions.ToggleLeft, KeymapPlatform.Other)[0].Text == "Mod+B", "Reset restores the source's chords.");

        // Tolerance: unknown ids, spellings and top-level members survive an edit; malformed text is an empty preset.
        var newer = KeymapBindings.Parse("""{"version":7,"future":{"x":1},"bindings":{"FutureAction":["Hyper+Q"],"Settings":["Mod+Comma"]}}""");
        var edited = Keymaps.Bind(newer, KeymapActions.Refresh, 0, free, KeymapPlatform.Other);
        Require(edited.Applied && KeymapBindings.Parse(edited.Bindings.ToJson()).Spellings("FutureAction").SequenceEqual(["Hyper+Q"]) && edited.Bindings.ToJson().Contains("\"future\""), "Unknown bindings survive an edit.");
        Require(KeymapBindings.Parse("not json").Ids.Count() == 0 && !KeymapBindings.IsValid("""{"bindings":{"A":[1]}}""") && KeymapBindings.IsValid("""{"bindings":{"A":["Mod+A"]}}"""), "Malformed documents are tolerated and validated.");
    }

    private static async Task Rejected(IHostStateService service, HostStateChange change, string message)
    {
        try { await service.ChangeAsync([change]); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or RpcException) { return; }
        throw new InvalidOperationException(message);
    }

    /// <summary>Preset changes through the direct and the WebSocket adapters: validation, selection, renaming, deleting, persistence and older profiles.</summary>
    internal static async Task Host(string root)
    {
        var bindings = KeymapBindings.IntelliJ(KeymapPlatform.Other);
        bindings.Set(KeymapActions.Refresh, []);
        var document = bindings.ToJson();
        var future = """{"version":9,"extra":true,"bindings":{"FutureAction":["Hyper+Q"]}}""";

        // An older profile has no keymap members: no presets, IntelliJ active.
        var older = Path.Combine(root, "keymap-older");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, HostStateStore.FileName), "{\"Settings\":{},\"Presets\":[]}\n");
        var oldStore = new HostStateStore(older);
        Require(oldStore.Current.KeymapPresets.Count == 0 && oldStore.Current.Settings.ActiveKeymap == "", "A profile without keymap members has IntelliJ active.");

        var localDirectory = Path.Combine(root, "keymap-local");
        var remoteDirectory = Path.Combine(root, "keymap-remote");
        var local = new LocalStateAdapter(new HostStateStore(localDirectory));
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, "keymap-test", remoteDirectory);
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var remote = new RemoteStateAdapter(new Uri(address), "keymap-test");
        foreach (var (name, service) in new (string, IHostStateService)[] { ("local", local), ("remote", remote) })
        {
            using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var stream = service.WatchAsync(watch.Token).GetAsyncEnumerator(watch.Token);
            Require(await stream.MoveNextAsync() && stream.Current.KeymapPresets.Count == 0, $"A {name} watcher starts without keymap presets.");
            await Rejected(service, HostStateChange.SaveKeymap("IntelliJ", document), $"The {name} host accepted the built-in's name.");
            await Rejected(service, HostStateChange.SaveKeymap("   ", document), $"The {name} host accepted an empty name.");
            await Rejected(service, HostStateChange.SaveKeymap("Bad", "[1]"), $"The {name} host accepted a malformed keymap.");
            await Rejected(service, HostStateChange.SelectKeymap("Missing"), $"The {name} host selected a preset that does not exist.");
            var saved = await service.ChangeAsync([HostStateChange.SaveKeymap(" Mine ", document), HostStateChange.SaveKeymap("Newer", future)]);
            Require(saved.KeymapPresets.Select(preset => preset.Name).SequenceEqual(["Mine", "Newer"]), $"The {name} host trims and keeps preset names in order.");
            await Rejected(service, HostStateChange.SaveKeymap("mine", document), $"The {name} host accepted a name that differs only by case.");
            Require(KeymapBindings.Parse(saved.KeymapPresets[1].Bindings).Spellings("FutureAction").SequenceEqual(["Hyper+Q"]), $"The {name} host kept unknown bindings.");
            HostState seen;
            await service.ChangeAsync([HostStateChange.SelectKeymap("Mine")]);
            do { Require(await stream.MoveNextAsync(), $"A {name} watcher stopped."); seen = stream.Current; } while (seen.Settings.ActiveKeymap != "Mine");
            Require(seen.KeymapPresets.Count == 2, $"A {name} watcher receives the selection and the presets.");
            var renamed = await service.ChangeAsync([HostStateChange.RenameKeymap("Mine", "Renamed")]);
            Require(renamed.Settings.ActiveKeymap == "Renamed" && renamed.KeymapPresets[0].Name == "Renamed", $"The {name} host moves the selection with a rename.");
            await Rejected(service, HostStateChange.RenameKeymap("Renamed", "Newer"), $"The {name} host renamed onto another preset.");
            var deleted = await service.ChangeAsync([HostStateChange.DeleteKeymap("Renamed")]);
            Require(deleted.Settings.ActiveKeymap == "" && deleted.KeymapPresets.Count == 1, $"Deleting the active preset of the {name} host falls back to IntelliJ.");
            await service.ChangeAsync([HostStateChange.SelectKeymap("Newer"), HostStateChange.SelectKeymap("intellij")]);
            Require((await service.GetStateAsync()).Settings.ActiveKeymap == "", $"The {name} host selects the built-in preset by its name.");
        }
        var reopened = new HostStateStore(localDirectory);
        Require(reopened.Current.KeymapPresets.Select(preset => preset.Name).SequenceEqual(["Newer"]) && reopened.Current.KeymapPresets[0].Bindings == future,
            "Keymap presets survive a restart byte for byte.");
    }
}