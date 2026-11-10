using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Panels;

/// <summary>Settings › Keymap: pick or edit the keymap preset every window of this client follows.</summary>
public sealed partial class SettingsView
{
    private string keymapSearch = "";
    private string? keymapSelected;
    private (string Action, int Slot)? keymapRecording;
    private (string Action, int Slot)? keymapAfterCreate;
    private ConflictPrompt? keymapConflict;
    private string keymapNotice = "";
    private readonly HashSet<string> keymapCollapsed = [];
    private HashSet<string> keymapConflicting = [];

    private sealed record ConflictPrompt(string Action, int Slot, KeyChord Chord, IReadOnlyList<string> Others);

    private static KeymapPlatform KeymapPlatform => ActiveKeymap.Platform;

    private string ActiveKeymapName => state.Current.Settings.ActiveKeymap is { Length: > 0 } active && state.Current.KeymapPresets.Any(preset => preset.Name == active) ? active : Keymaps.BuiltinName;

    private bool KeymapBuiltinActive => ActiveKeymapName == Keymaps.BuiltinName;

    private KeymapBindings KeymapBindingsOf(string name) =>
        name == Keymaps.BuiltinName ? KeymapBindings.IntelliJ(KeymapPlatform, KeymapPlugins) : Keymaps.Resolve(state.Current.KeymapPresets, name, KeymapPlatform, KeymapPlugins);

    private Control KeymapSettings()
    {
        var panel = Page("KeymapPage");
        var supported = state.Supports(HostProtocol.Keymaps);
        var unsupported = PageControl<TextBlock>(panel, "KeymapUnsupported");
        unsupported.IsVisible = !supported;
        unsupported.Text = "This host predates keymaps, so the built-in IntelliJ keymap applies and cannot be changed here. Update the host to edit shortcuts.";
        var bar = PageControl<WrapPanel>(panel, "KeymapPresetBar");
        var custom = ActiveKeymapName != Keymaps.BuiltinName;
        var picker = new ComboBox { Name = "KeymapPreset", MinWidth = 200, IsEnabled = supported };
        AutomationProperties.SetName(picker, "Keymap preset");
        var names = new List<string> { Keymaps.BuiltinName };
        names.AddRange(state.Current.KeymapPresets.Select(preset => preset.Name));
        picker.ItemsSource = names;
        picker.SelectedItem = ActiveKeymapName;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is string chosen && chosen != ActiveKeymapName) Share(HostStateChange.SelectKeymap(chosen == Keymaps.BuiltinName ? "" : chosen));
        };
        bar.Children.Add(picker);
        Button Action(string label, string name, bool enabled, Action click)
        {
            var button = Ui.Button(label, click);
            button.Name = name; button.IsEnabled = enabled && supported; button.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(button, label.TrimEnd('…') + " keymap preset");
            bar.Children.Add(button);
            return button;
        }
        Action("New preset…", "KeymapNew", true, () => _ = NewKeymapAsync(ActiveKeymapName, prompt: true));
        Action("Rename…", "KeymapRename", custom, () => _ = RenameKeymapAsync());
        Action("Duplicate", "KeymapDuplicate", true, () => _ = NewKeymapAsync(ActiveKeymapName, prompt: false));
        Action("Delete", "KeymapDelete", custom, () => _ = DeleteKeymapAsync());
        PageControl<TextBlock>(panel, "KeymapBuiltinNote").IsVisible = !custom;
        var search = PageControl<TextBox>(panel, "KeymapSearch");
        AutomationProperties.SetName(search, "Search actions");
        search.Text = keymapSearch;
        search.TextChanged += (_, _) => { keymapSearch = search.Text ?? ""; RenderKeymapList(panel); };
        RenderKeymapList(panel);
        if (keymapAfterCreate is { } after && !KeymapBuiltinActive)
        {
            keymapAfterCreate = null;
            Dispatcher.UIThread.Post(() => BeginKeymapRecording(after.Action, after.Slot), DispatcherPriority.Background);
        }
        return panel;
    }

    private static IReadOnlyList<KeymapAction> KeymapPlugins => ActiveKeymap.PluginActions;

    /// <summary>The chords an action has in the bindings: the preset's own, else the built-in IntelliJ defaults (the plugins' included).</summary>
    private static IReadOnlyList<KeyChord> KeymapChordsOf(KeymapBindings bindings, KeymapAction action) =>
        bindings.Has(action.Id) ? bindings.Chords(action.Id, KeymapPlatform) : KeymapBindings.IntelliJ(KeymapPlatform, KeymapPlugins).Chords(action.Id, KeymapPlatform);

    private bool KeymapMatches(KeymapAction action, KeymapBindings bindings, string query) =>
        query.Length == 0 || action.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || action.Group.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        action.PluginName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        KeymapChordsOf(bindings, action).Any(chord => chord.Display(KeymapPlatform).Contains(query, StringComparison.OrdinalIgnoreCase));

    private void RenderKeymapList(Control panel)
    {
        var list = PageControl<StackPanel>(panel, "KeymapActionList");
        var notice = PageControl<StackPanel>(panel, "KeymapNoticeBox");
        list.Children.Clear();
        RenderKeymapNotice(notice, panel);
        var bindings = KeymapBindingsOf(ActiveKeymapName);
        var source = ReferenceBindings(bindings);
        var supported = state.Supports(HostProtocol.Keymaps);
        var query = keymapSearch.Trim();
        var plugins = KeymapPlugins;
        var everything = KeymapActions.With(plugins).ToArray();

        // Chords two actions share: the page says so and jumps to the first of them.
        var shared = everything.SelectMany(action => KeymapChordsOf(bindings, action).Select(chord => (chord, action.Id))).GroupBy(entry => entry.chord)
            .Where(group => group.Select(entry => entry.Id).Distinct().Count() > 1).ToArray();
        keymapConflicting = [.. shared.SelectMany(group => group.Select(entry => entry.Id))];
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var indicator = Ui.Text(shared.Length == 0 ? "No conflicts" : shared.Length == 1 ? "1 shortcut is used twice" : $"{shared.Length} shortcuts are used twice", shared.Length == 0 ? Ui.Muted : Ui.Danger, 12);
        indicator.Name = "KeymapConflicts"; indicator.VerticalAlignment = VerticalAlignment.Center;
        summary.Children.Add(indicator);
        if (shared.Length > 0)
        {
            var show = Ui.Button("Show", () =>
            {
                keymapSelected = shared[0].First().Item2;
                if (everything.FirstOrDefault(action => action.Id == keymapSelected) is { } first) keymapCollapsed.Remove(KeymapGroupKey(first));
                keymapSearch = ""; RefreshKeymapList();
            });
            show.Name = "KeymapShowConflict";
            AutomationProperties.SetName(show, "Show the first conflicting action");
            summary.Children.Add(show);
        }
        list.Children.Add(summary);

        // Built-in groups by area, then one group per plugin action group under a Plugins heading.
        var builtin = everything.Where(action => !action.IsPlugin && (LanguageSupport.Available || action.Group != KeymapActions.LanguageGroup)).GroupBy(action => action.Group);
        foreach (var group in builtin) AddKeymapGroup(list, panel, group.Key, "KeymapGroup_" + group.Key.Replace(' ', '_'), group.ToArray(), null, bindings, source, supported, query);
        var pluginGroups = everything.Where(action => action.IsPlugin).GroupBy(action => (action.Plugin, action.PluginName, action.Group)).ToArray();
        // A preset keeps the bindings of plugins that are off: listed, unbound from any live action, so people see why a chord is not doing anything.
        var absent = bindings.Ids.Where(id => id.Contains(':') && everything.All(action => action.Id != id) && bindings.Chords(id, KeymapPlatform).Count > 0).ToArray();
        if (pluginGroups.Length > 0 || absent.Length > 0)
        {
            var heading = new TextBlock { Text = "Plugins", Classes = { "settings-label" }, Margin = new Thickness(0, 16, 0, 0), Name = "KeymapPluginsHeading" };
            list.Children.Add(heading);
        }
        foreach (var group in pluginGroups)
            AddKeymapGroup(list, panel, group.Key.Group, "KeymapGroup_" + group.Key.Plugin.Replace(':', '_') + "_" + group.Key.Group.Replace(' ', '_'), group.ToArray(), group.Key.Plugin, bindings, source, supported, query);
        if (absent.Length > 0)
        {
            var unavailable = absent.Where(id => query.Length == 0 || id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (unavailable.Length > 0)
            {
                var expanded = query.Length > 0 || !keymapCollapsed.Contains("unavailable");
                list.Children.Add(KeymapGroupHeader(panel, "Plugin not available", "unavailable", unavailable.Length, expanded, null, "KeymapGroup_Unavailable", null));
                if (expanded)
                    foreach (var id in unavailable)
                    {
                        var chords = bindings.Chords(id, KeymapPlatform);
                        var row = new Border { Name = "KeymapRow_" + id, Padding = new Thickness(8, 4), Focusable = true, Tag = id };
                        var text = $"{id}: {string.Join(", ", chords.Select(chord => chord.Display(KeymapPlatform)))} (plugin not available)";
                        AutomationProperties.SetName(row, text);
                        row.Child = Ui.Text(text, Ui.Muted, 12);
                        list.Children.Add(row);
                    }
            }
        }
        if (list.Children.Count == 1 && query.Length > 0)
            list.Children.Add(Ui.Text("No action matches " + query + ".", Ui.Muted, 12));
    }

    private static string KeymapGroupKey(KeymapAction action) => action.IsPlugin ? action.Plugin + "/" + action.Group : action.Group;

    private void AddKeymapGroup(StackPanel list, Control panel, string title, string name, KeymapAction[] actions, string? pluginId, KeymapBindings bindings, KeymapBindings? source, bool supported, string query)
    {
        var visible = actions.Where(action => KeymapMatches(action, bindings, query)).ToArray();
        if (visible.Length == 0) return;
        var key = KeymapGroupKey(actions[0]);
        // Searching opens every group that has a match; otherwise the group is as the user left it.
        var expanded = query.Length > 0 || !keymapCollapsed.Contains(key);
        list.Children.Add(KeymapGroupHeader(panel, title, key, visible.Length, expanded, pluginId is null ? null : actions[0].PluginName, name, pluginId,
            source is not null && actions.Any(action => !KeymapChordsOf(bindings, action).SequenceEqual(source.Has(action.Id) ? source.Chords(action.Id, KeymapPlatform) : [])) ? (bindings, source!, actions) : null, supported));
        if (expanded)
            foreach (var action in visible) list.Children.Add(KeymapRow(panel, action, bindings, source, supported));
    }

    private Control KeymapGroupHeader(Control panel, string title, string key, int count, bool expanded, string? pluginName, string name, string? pluginId,
        (KeymapBindings Bindings, KeymapBindings Source, KeymapAction[] Actions)? reset = null, bool supported = true)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 4) };
        var toggle = new Button { Name = name, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        // A plugin without an icon gets no slot; the whole row toggles, the state is in its automation name.
        if (pluginId is not null && window.Workbench.PluginRegistry.Manifests.GetValueOrDefault(pluginId)?.Icon is { Length: > 0 } icon)
            content.Children.Add(Plugins.PluginIcons.Resolve(icon, window.Workbench.PluginRegistry.Entry(pluginId), Ui.Muted));
        var heading = pluginName is null ? title : title == pluginName ? pluginName : $"{pluginName}: {title}";
        content.Children.Add(Ui.Text(heading, Ui.TextBrush, 13));
        content.Children.Add(Ui.Text(count.ToString(), Ui.Muted, 12));
        toggle.Content = content;
        AutomationProperties.SetName(toggle, $"{heading}, {count} actions, {(expanded ? "expanded" : "collapsed")}");
        toggle.Click += (_, _) => { if (!keymapCollapsed.Remove(key)) keymapCollapsed.Add(key); RefreshKeymapList(); };
        header.Children.Add(toggle);
        if (reset is { } group)
        {
            var button = Ui.Button("Reset group", () =>
            {
                var next = group.Bindings;
                foreach (var action in group.Actions) next = Keymaps.Reset(next, group.Source, action.Id, KeymapPlatform);
                Share(HostStateChange.SaveKeymap(ActiveKeymapName, next.ToJson()));
            });
            button.Name = name + "_Reset"; button.IsEnabled = supported; Grid.SetColumn(button, 1);
            AutomationProperties.SetName(button, $"Reset {title} to preset");
            header.Children.Add(button);
        }
        return header;
    }

    /// <summary>What Reset to preset goes back to: the preset this one was duplicated from, else IntelliJ; null for IntelliJ itself.</summary>
    private KeymapBindings? ReferenceBindings(KeymapBindings current)
    {
        if (KeymapBuiltinActive) return null;
        var basis = current.BasedOn;
        return basis.Length > 0 && basis != Keymaps.BuiltinName && state.Current.KeymapPresets.Any(preset => preset.Name == basis) ? KeymapBindingsOf(basis) : KeymapBindings.IntelliJ(KeymapPlatform, KeymapPlugins);
    }

    private Control KeymapRow(Control panel, KeymapAction action, KeymapBindings bindings, KeymapBindings? source, bool supported)
    {
        var chords = KeymapChordsOf(bindings, action);
        var selected = keymapSelected == action.Id;
        var row = new Border
        {
            Name = "KeymapRow_" + action.Id,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            Background = selected ? Ui.PrimarySubtle : Brushes.Transparent,
            Focusable = true,
            Tag = action.Id
        };
        AutomationProperties.SetName(row, $"{action.Title}: {(chords.Count == 0 ? "no shortcut" : string.Join(", ", chords.Select(chord => chord.Display(KeymapPlatform))))}");
        var layout = new WrapPanel { Orientation = Orientation.Horizontal };
        var name = new StackPanel { MinWidth = 192, Margin = new Thickness(0, 0, 8, 0) };
        name.Children.Add(Ui.Text(action.Title, Ui.TextBrush));
        // The category only: the group header above already names the plugin.
        if (action.Category is { } category) name.Children.Add(Ui.Text(category, Ui.Muted, 11));
        if (keymapConflicting.Contains(action.Id)) name.Children.Add(Ui.Text("Conflict", Ui.Danger, 11));
        layout.Children.Add(name);
        for (var slot = 0; slot < KeymapBindings.MaxChords; slot++)
        {
            if (slot > chords.Count) break;
            var recording = keymapRecording == (action.Id, slot);
            var captured = slot;
            Control chip = recording ? KeymapRecorder(panel, action, slot)
                : Chip(slot < chords.Count ? chords[slot].Display(KeymapPlatform) : "Add shortcut", slot < chords.Count, supported,
                    () => BeginKeymapRecording(action.Id, captured), $"{(slot < chords.Count ? "Change" : "Add")} shortcut {slot + 1} of {action.Title}", $"KeymapChip_{action.Id}_{slot}");
            chip.Margin = new Thickness(0, 0, 8, 0);
            layout.Children.Add(chip);
        }
        var edit = Ui.Button("Edit", () => BeginKeymapRecording(action.Id, 0));
        edit.Name = "KeymapEdit_" + action.Id; edit.IsEnabled = supported; edit.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(edit, "Edit " + action.Title);
        layout.Children.Add(edit);
        if (source is not null && !chords.SequenceEqual(source.Has(action.Id) ? source.Chords(action.Id, KeymapPlatform) : []))
        {
            var reset = Ui.Button("Reset to preset", () => Share(HostStateChange.SaveKeymap(ActiveKeymapName, Keymaps.Reset(bindings, source, action.Id, KeymapPlatform).ToJson())));
            reset.Name = "KeymapReset_" + action.Id; reset.IsEnabled = supported;
            AutomationProperties.SetName(reset, "Reset " + action.Title + " to preset");
            layout.Children.Add(reset);
        }
        row.Child = layout;
        row.PointerPressed += (_, _) => { keymapSelected = action.Id; RenderKeymapList(panel); };
        row.DoubleTapped += (_, e) => { if (supported && e.Source is not Button) BeginKeymapRecording(action.Id, 0); };
        row.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.Source == row && supported) { BeginKeymapRecording(action.Id, 0); e.Handled = true; } };
        return row;
    }

    private static Button Chip(string text, bool bound, bool enabled, Action click, string automationName, string name)
    {
        var chip = new Button
        {
            Name = name,
            Content = Ui.Text(text, bound ? Ui.TextBrush : Ui.Muted, 12),
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.BorderBrush,
            IsEnabled = enabled
        };
        chip.Click += (_, _) => click();
        AutomationProperties.SetName(chip, automationName);
        return chip;
    }

    private void BeginKeymapRecording(string actionId, int slot)
    {
        if (!state.Supports(HostProtocol.Keymaps)) return;
        keymapSelected = actionId; keymapConflict = null; keymapNotice = "";
        if (KeymapBuiltinActive) { CreateToEdit(actionId, slot); return; }
        keymapRecording = (actionId, slot);
        ActiveKeymap.Recording = true;
        RefreshKeymapList();
    }

    private void EndKeymapRecording()
    {
        keymapRecording = null;
        ActiveKeymap.Recording = false;
    }

    private void RefreshKeymapList()
    {
        if (section != "Keymap" || body.Content is not Control page) return;
        RenderKeymapList(page);
        // The recorder takes the keyboard as it appears; a refresh without one leaves focus where it is.
        if (keymapRecording is not null && page.GetLogicalDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "KeymapRecorder") is { } recorder)
            Dispatcher.UIThread.Post(() => recorder.Focus(), DispatcherPriority.Input);
    }

    private Control KeymapRecorder(Control panel, KeymapAction action, int slot)
    {
        var label = Ui.Text("Press the shortcut…", Ui.Accent, 12);
        var recorder = new Border
        {
            Name = "KeymapRecorder",
            Child = label,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Accent,
            Focusable = true
        };
        AutomationProperties.SetName(recorder, $"Press the new shortcut for {action.Title}; Escape cancels, Backspace clears");
        void Preview(KeyEventArgs e)
        {
            var modifiers = e.KeyModifiers;
            var chord = new KeyChord(
                (modifiers.HasFlag(KeyModifiers.Control) ? KeyMods.Ctrl : 0) | (modifiers.HasFlag(KeyModifiers.Alt) ? KeyMods.Alt : 0) |
                (modifiers.HasFlag(KeyModifiers.Shift) ? KeyMods.Shift : 0) | (modifiers.HasFlag(KeyModifiers.Meta) ? KeyMods.Meta : 0), "A").Normalized(KeymapPlatform);
            var text = chord.Display(KeymapPlatform);
            label.Text = chord.Mods == KeyMods.None ? "Press the shortcut…" : text[..^1] + "…";
        }
        recorder.KeyUp += (_, e) => Preview(e);
        recorder.LostFocus += (_, _) => { if (keymapRecording == (action.Id, slot) && keymapConflict is null) { EndKeymapRecording(); RefreshKeymapList(); } };
        recorder.KeyDown += (_, e) =>
        {
            e.Handled = true;
            if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) { Preview(e); return; }
            if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape) { EndKeymapRecording(); RefreshKeymapList(); return; }
            if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Back or Key.Delete) { EndKeymapRecording(); ApplyKeymapBinding(action.Id, slot, null, reassign: false); return; }
            if (KeyboardChords.FromEvent(e) is not { } chord) return;
            label.Text = chord.Display(KeymapPlatform);
            EndKeymapRecording();
            ApplyKeymapBinding(action.Id, slot, chord, reassign: false);
        };
        return recorder;
    }

    /// <summary>Binds a chord (or clears the slot) in the active custom preset; a conflict or a reserved chord is explained and changes nothing.</summary>
    private void ApplyKeymapBinding(string actionId, int slot, KeyChord? chord, bool reassign)
    {
        var bindings = KeymapBindingsOf(ActiveKeymapName);
        // Materialise IntelliJ's chords for an action the preset never mentioned so editing one slot keeps the other.
        if (!bindings.Has(actionId)) bindings.Set(actionId, KeymapBindings.IntelliJ(KeymapPlatform, KeymapPlugins).Chords(actionId, KeymapPlatform));
        var result = Keymaps.Bind(bindings, actionId, slot, chord, KeymapPlatform, reassign, KeymapPlugins);
        if (result.Reserved is { } reason)
        {
            keymapNotice = $"{chord?.Display(KeymapPlatform)} cannot be assigned: {reason}";
            keymapConflict = null;
            RefreshKeymapList();
            return;
        }
        if (result.Conflicts.Count > 0)
        {
            keymapConflict = new(actionId, slot, chord!.Value, result.Conflicts);
            keymapNotice = "";
            RefreshKeymapList();
            return;
        }
        keymapConflict = null; keymapNotice = "";
        Share(HostStateChange.SaveKeymap(ActiveKeymapName, result.Bindings.ToJson()));
    }

    private void RenderKeymapNotice(StackPanel box, Control panel)
    {
        box.Children.Clear();
        if (keymapConflict is { } conflict)
        {
            var titles = string.Join(" and ", conflict.Others.Select(id => KeymapActions.Find(id, KeymapPlugins)?.Title ?? id));
            var message = Ui.Text($"{conflict.Chord.Display(KeymapPlatform)} is already used by {titles}.", Ui.Danger, 12);
            message.TextWrapping = TextWrapping.Wrap; message.Name = "KeymapConflictText";
            box.Children.Add(message);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var reassign = Ui.Button("Reassign", () => { var pending = conflict; keymapConflict = null; ApplyKeymapBinding(pending.Action, pending.Slot, pending.Chord, reassign: true); });
            reassign.Name = "KeymapReassign";
            AutomationProperties.SetName(reassign, "Reassign the shortcut to " + (KeymapActions.Find(conflict.Action, KeymapPlugins)?.Title ?? conflict.Action));
            var cancel = Ui.Button("Cancel", () => { keymapConflict = null; RefreshKeymapList(); });
            cancel.Name = "KeymapConflictCancel";
            buttons.Children.Add(reassign); buttons.Children.Add(cancel);
            box.Children.Add(buttons);
        }
        else if (keymapNotice.Length > 0)
        {
            var note = Ui.Text(keymapNotice, Ui.Danger, 12);
            note.TextWrapping = TextWrapping.Wrap; note.Name = "KeymapNoticeText";
            box.Children.Add(note);
        }
        box.IsVisible = box.Children.Count > 0;
    }

    /// <summary>IntelliJ is never edited in place: the first edit makes a custom copy without asking, selects it and then records into it.</summary>
    private void CreateToEdit(string actionId, int slot)
    {
        var existing = state.Current.KeymapPresets.Select(preset => preset.Name).ToList();
        var name = "My keymap";
        for (var n = 2; Keymaps.ValidateName(name, existing) is not null; n++) name = $"My keymap {n}";
        var copy = KeymapBindingsOf(Keymaps.BuiltinName).Copy();
        copy.BasedOn = Keymaps.BuiltinName;
        keymapAfterCreate = (actionId, slot);
        Share(HostStateChange.SaveKeymap(name, copy.ToJson()), HostStateChange.SelectKeymap(name));
    }

    private async Task<bool> NewKeymapAsync(string sourceName, bool prompt, string title = "New keymap preset", string? suggested = null)
    {
        var existing = state.Current.KeymapPresets.Select(preset => preset.Name).ToList();
        var name = suggested ?? sourceName + " copy";
        for (var n = 2; Keymaps.ValidateName(name, existing) is not null && !prompt; n++) name = $"{sourceName} copy {n}";
        if (prompt)
        {
            string? problem = null;
            while (true)
            {
                var answer = await Dialogs.Prompt(window, problem is null ? title : $"{title} ({problem})", ("Preset name", name));
                if (answer is null) return false;
                name = answer[0];
                problem = Keymaps.ValidateName(name, existing);
                if (problem is null) break;
            }
        }
        name = name.Trim();
        var copy = KeymapBindingsOf(sourceName).Copy();
        copy.BasedOn = sourceName;
        Share(HostStateChange.SaveKeymap(name, copy.ToJson()), HostStateChange.SelectKeymap(name));
        return true;
    }

    private async Task RenameKeymapAsync()
    {
        var current = ActiveKeymapName;
        if (current == Keymaps.BuiltinName) return;
        var others = state.Current.KeymapPresets.Select(preset => preset.Name).ToList();
        string? problem = null; var name = current;
        while (true)
        {
            var answer = await Dialogs.Prompt(window, problem is null ? "Rename keymap preset" : $"Rename keymap preset ({problem})", ("Preset name", name));
            if (answer is null) return;
            name = answer[0];
            problem = Keymaps.ValidateName(name, others, except: current);
            if (problem is null) break;
        }
        if (name.Trim() != current) Share(HostStateChange.RenameKeymap(current, name.Trim()));
    }

    private async Task DeleteKeymapAsync()
    {
        var current = ActiveKeymapName;
        if (current == Keymaps.BuiltinName) return;
        if (!await Dialogs.Confirm(window, $"Delete {current}?", "Its shortcuts are lost and the built-in IntelliJ keymap becomes active.", "Delete preset")) return;
        Share(HostStateChange.DeleteKeymap(current));
    }
}