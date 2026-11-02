using System.Net;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;
using SharpRail.UI;
using SharpRail.UI.State;

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

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    /// <summary>The active keymap reaching a live window: dispatch, gestures, menu key equivalents, unbinding, fallback, no focus change.</summary>
    internal static void Runtime(string root)
    {
        var mac = OperatingSystem.IsMacOS();
        var command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        var window = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(root + "-keymap-runtime"), E2E.E2eTerminals.Plain);
        window.Show();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!window.WorkspaceMounted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Require(window.WorkspaceMounted, "The keymap window did not mount.");
        var menu = ApplicationMenu.Build(window, window.Commands);
        NativeMenuItem Item(string menuName, string header) =>
            menu.Items.OfType<NativeMenuItem>().Single(entry => entry.Header == menuName).Menu!.Items.OfType<NativeMenuItem>().Single(entry => entry.Header == header);
        var focus = window.FocusManager?.GetFocusedElement();
        try
        {
            ActiveKeymap.Set(new HostState());
            Require(window.ShortcutFor(Press(Key.N, command)) == WindowShortcuts.NewWorkspace, "IntelliJ is the default keymap.");
            Require(Item("File", "New Workspace").Gesture is { } before && before.Equals(WindowShortcuts.NewWorkspace.Gesture), "The menu shows the active chord.");

            // A custom preset moves New Workspace to Mod+Shift+K and unbinds Open Project.
            var custom = KeymapBindings.IntelliJ(ActiveKeymap.Platform);
            var moved = Keymaps.Bind(custom, KeymapActions.NewWorkspace, 0, new KeyChord(KeyMods.Mod | KeyMods.Shift, "K"), ActiveKeymap.Platform);
            Require(moved.Applied, "bind");
            var edited = Keymaps.Bind(moved.Bindings, KeymapActions.OpenProject, 0, null, ActiveKeymap.Platform).Bindings;
            var seen = 0;
            ActiveKeymap.Changed += () => seen++;
            ActiveKeymap.Set(new HostState { KeymapPresets = [new("Mine", edited.ToJson())], Settings = new HostSettings { ActiveKeymap = "Mine" } });
            Require(seen == 1, "A changed keymap raises Changed once.");
            Require(window.ShortcutFor(Press(Key.K, command | KeyModifiers.Shift)) == WindowShortcuts.NewWorkspace && window.ShortcutFor(Press(Key.N, command)) is null,
                "The new chord reaches the command and the old one no longer does.");
            Require(window.ShortcutFor(Press(Key.O, command)) is null, "An unbound command has no chord.");
            Require(Item("File", "New Workspace").Gesture is { Key: Key.K } && Item("File", "Open Project…").Gesture is null, "Menu key equivalents follow the keymap in place.");
            ActiveKeymap.Set(new HostState { KeymapPresets = [new("Mine", edited.ToJson())], Settings = new HostSettings { ActiveKeymap = "Mine" } });
            Require(seen == 1, "An identical snapshot raises nothing.");
            Require(window.FocusManager?.GetFocusedElement() == focus, "A keymap change does not move focus.");

            // Exact modifiers: the other command key and extra modifiers no longer reach a chord.
            Require(window.ShortcutFor(Press(Key.B, command | KeyModifiers.Shift)) is null, "Extra modifiers do not match a chord.");
            Require(window.ShortcutFor(Press(Key.OemPlus, command | KeyModifiers.Shift)) == WindowShortcuts.ZoomIn, "Shifted plus still zooms in.");

            // Deleting the preset falls back to IntelliJ.
            ActiveKeymap.Set(new HostState { Settings = new HostSettings { ActiveKeymap = "Mine" } });
            Require(window.ShortcutFor(Press(Key.N, command)) == WindowShortcuts.NewWorkspace && window.ShortcutFor(Press(Key.O, command)) == WindowShortcuts.OpenProject, "A missing preset is IntelliJ.");
        }
        finally { ActiveKeymap.Set(new HostState()); window.Close(); }
    }

    /// <summary>Settings › Keymap driven like a user: presets, search, the recorder, conflicts, Reassign, reserved chords, clearing, Reset, delete and narrow layout.</summary>
    internal static void Page(string root)
    {
        var mac = OperatingSystem.IsMacOS();
        var command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        var window = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(root + "-keymap-page"), E2E.E2eTerminals.Plain);
        window.Show();
        void Until(Func<bool> done, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Require(done(), message);
        }
        Until(() => window.WorkspaceMounted && window.Workbench.State.Handshake is not null, "The keymap page window did not mount.");
        var state = window.Workbench.State;
        try
        {
            window.ShowSettings("Keymap");
            Until(() => window.OpenSettingsViews.Any(), "Settings did not open.");
            var settings = window.OpenSettingsViews.Single();
            T Find<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            T? Maybe<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);
            int Rows() => settings.GetLogicalDescendants().OfType<Border>().Count(border => border.Name?.StartsWith("KeymapRow_") == true);
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Record(Key key, KeyModifiers modifiers)
            {
                Until(() => Maybe<Border>("KeymapRecorder") is not null, "The recorder did not appear.");
                Find<Border>("KeymapRecorder").RaiseEvent(Press(key, modifiers));
            }
            Require(Find<ComboBox>("KeymapPreset").SelectedItem as string == "IntelliJ" && Rows() == KeymapActions.All.Count, "IntelliJ is selected and every action is listed.");
            Require(!Find<Button>("KeymapRename").IsEnabled && !Find<Button>("KeymapDelete").IsEnabled && Find<Button>("KeymapDuplicate").IsEnabled, "The built-in preset can be duplicated but not renamed or deleted.");
            Find<TextBox>("KeymapSearch").Text = "zoom";
            Until(() => Rows() == 2, "Search narrows the list to the two zoom actions.");
            Find<TextBox>("KeymapSearch").Text = "";
            Until(() => Rows() == KeymapActions.All.Count, "Clearing the search lists everything.");

            // Layout: the page never needs more width than a phone gives it.
            foreach (var width in new[] { 320.0, 900.0 })
            {
                settings.Measure(new Size(width, double.PositiveInfinity));
                Require(settings.DesiredSize.Width <= width + 1, $"The Keymap page overflows at {width} wide.");
            }

            // A custom preset made from IntelliJ becomes active.
            var mine = KeymapBindings.IntelliJ(ActiveKeymap.Platform); mine.BasedOn = "IntelliJ";
            _ = state.ChangeAsync([HostStateChange.SaveKeymap("Mine", mine.ToJson()), HostStateChange.SelectKeymap("Mine")]);
            Until(() => Maybe<ComboBox>("KeymapPreset")?.SelectedItem as string == "Mine" && Find<Button>("KeymapRename").IsEnabled, "The new preset is selected and editable.");
            Chords();
            IReadOnlyList<KeyChord> Chords() => ActiveKeymap.Chords(KeymapActions.ToggleRight);

            // Record a free chord on Toggle right panel.
            Click(Find<Button>("KeymapEdit_" + KeymapActions.ToggleRight));
            Record(Key.K, command | KeyModifiers.Shift);
            Until(() => Chords().Select(chord => chord.Text).SequenceEqual(["Mod+Shift+K"]), "The recorded chord is live in the window.");
            Require(window.ShortcutFor(Press(Key.K, command | KeyModifiers.Shift)) == WindowShortcuts.ToggleRight, "The recorded chord reaches the command.");

            // Escape cancels and leaves Settings open; Backspace clears.
            Click(Find<Button>("KeymapEdit_" + KeymapActions.ToggleRight));
            Record(Key.Escape, KeyModifiers.None);
            Until(() => Maybe<Border>("KeymapRecorder") is null && window.OpenSettingsViews.Any() && Chords().Count == 1, "Escape cancels the recorder without closing Settings.");

            // Conflict: Toggle left panel takes Toggle right panel's chord; Cancel keeps both, Reassign moves it.
            Click(Find<Button>("KeymapEdit_" + KeymapActions.ToggleLeft));
            Record(Key.K, command | KeyModifiers.Shift);
            Until(() => Maybe<TextBlock>("KeymapConflictText")?.Text?.Contains("Toggle right panel") == true, "The conflict names the other action.");
            Click(Find<Button>("KeymapConflictCancel"));
            Until(() => Maybe<TextBlock>("KeymapConflictText") is null, "Cancel dismisses the conflict.");
            Require(Chords().Count == 1, "Cancel changes nothing.");
            Click(Find<Button>("KeymapEdit_" + KeymapActions.ToggleLeft));
            Record(Key.K, command | KeyModifiers.Shift);
            Until(() => Maybe<Button>("KeymapReassign") is not null, "The conflict offers Reassign.");
            Click(Find<Button>("KeymapReassign"));
            Until(() => Chords().Count == 0 && ActiveKeymap.Chords(KeymapActions.ToggleLeft).Select(chord => chord.Text).SequenceEqual(["Mod+Shift+K"]), "Reassign moves the chord.");

            // A terminal's chord is refused with a reason.
            Click(Find<Button>("KeymapEdit_" + KeymapActions.ToggleRight));
            Record(Key.C, command);
            Until(() => Maybe<TextBlock>("KeymapNoticeText") is not null && Chords().Count == 0, "A terminal-reserved chord is refused.");

            // A second chord, then Reset to preset.
            Click(Find<Button>("KeymapChip_" + KeymapActions.ToggleLeft + "_1"));
            Record(Key.L, command | KeyModifiers.Alt);
            Until(() => ActiveKeymap.Chords(KeymapActions.ToggleLeft).Count == 2, "An action takes a second chord.");
            Until(() => Maybe<Button>("KeymapReset_" + KeymapActions.ToggleLeft) is not null, "A changed row offers Reset to preset.");
            Click(Find<Button>("KeymapReset_" + KeymapActions.ToggleLeft));
            Until(() => ActiveKeymap.Chords(KeymapActions.ToggleLeft).Select(chord => chord.Text).SequenceEqual(KeymapBindings.IntelliJ(ActiveKeymap.Platform).Chords(KeymapActions.ToggleLeft, ActiveKeymap.Platform).Select(chord => chord.Text)),
                "Reset restores the preset's chords.");

            // Clearing with Backspace unbinds.
            Click(Find<Button>("KeymapEdit_" + KeymapActions.ZoomReset));
            Record(Key.Back, KeyModifiers.None);
            Until(() => ActiveKeymap.Chords(KeymapActions.ZoomReset).Count == 0, "Backspace clears the chord.");

            // Deleting the active preset falls back to IntelliJ in the page and the window.
            _ = state.ChangeAsync([HostStateChange.DeleteKeymap("Mine")]);
            Until(() => Find<ComboBox>("KeymapPreset").SelectedItem as string == "IntelliJ" && ActiveKeymap.Chords(KeymapActions.ZoomReset).Count == 1, "Deleting the active preset selects IntelliJ everywhere.");
            Require(!ActiveKeymap.Recording, "No recorder is left holding the keyboard.");
            settings.Close();
        }
        finally { ActiveKeymap.Set(new HostState()); window.Close(); }
    }
}