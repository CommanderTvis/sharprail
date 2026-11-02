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

    private sealed record ConflictPrompt(string Action, int Slot, KeyChord Chord, IReadOnlyList<string> Others);

    private static KeymapPlatform KeymapPlatform => ActiveKeymap.Platform;

    private string ActiveKeymapName => state.Current.Settings.ActiveKeymap is { Length: > 0 } active && state.Current.KeymapPresets.Any(preset => preset.Name == active) ? active : Keymaps.BuiltinName;

    private bool KeymapBuiltinActive => ActiveKeymapName == Keymaps.BuiltinName;

    private KeymapBindings KeymapBindingsOf(string name) =>
        name == Keymaps.BuiltinName ? KeymapBindings.IntelliJ(KeymapPlatform) : Keymaps.Resolve(state.Current.KeymapPresets, name, KeymapPlatform);

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
        foreach (var group in KeymapActions.All.GroupBy(action => action.Group))
        {
            var actions = group.Where(action => query.Length == 0 || action.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                bindings.Chords(action.Id, KeymapPlatform).Any(chord => chord.Display(KeymapPlatform).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (actions.Length == 0) continue;
            var heading = new TextBlock { Text = group.Key, Classes = { "settings-label" }, Margin = new Thickness(0, 8, 0, 0) };
            list.Children.Add(heading);
            foreach (var action in actions) list.Children.Add(KeymapRow(panel, action, bindings, source, supported));
        }
        if (list.Children.Count == 0) list.Children.Add(Ui.Text("No action matches " + query + ".", Ui.Muted, 12));
    }

    /// <summary>What Reset to preset goes back to: the preset this one was duplicated from, else IntelliJ; null for IntelliJ itself.</summary>
    private KeymapBindings? ReferenceBindings(KeymapBindings current)
    {
        if (KeymapBuiltinActive) return null;
        var basis = current.BasedOn;
        return basis.Length > 0 && basis != Keymaps.BuiltinName && state.Current.KeymapPresets.Any(preset => preset.Name == basis) ? KeymapBindingsOf(basis) : KeymapBindings.IntelliJ(KeymapPlatform);
    }

    private Control KeymapRow(Control panel, KeymapAction action, KeymapBindings bindings, KeymapBindings? source, bool supported)
    {
        var chords = bindings.Has(action.Id) ? bindings.Chords(action.Id, KeymapPlatform) : KeymapBindings.IntelliJ(KeymapPlatform).Chords(action.Id, KeymapPlatform);
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
        var name = Ui.Text(action.Title, Ui.TextBrush);
        name.MinWidth = 192; name.Margin = new Thickness(0, 0, 8, 0);
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
        if (KeymapBuiltinActive) { _ = CreateToEditAsync(actionId, slot); return; }
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
        if (!bindings.Has(actionId)) bindings.Set(actionId, KeymapBindings.IntelliJ(KeymapPlatform).Chords(actionId, KeymapPlatform));
        var result = Keymaps.Bind(bindings, actionId, slot, chord, KeymapPlatform, reassign);
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
            var titles = string.Join(" and ", conflict.Others.Select(id => KeymapActions.Find(id)?.Title ?? id));
            var message = Ui.Text($"{conflict.Chord.Display(KeymapPlatform)} is already used by {titles}.", Ui.Danger, 12);
            message.TextWrapping = TextWrapping.Wrap; message.Name = "KeymapConflictText";
            box.Children.Add(message);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var reassign = Ui.Button("Reassign", () => { var pending = conflict; keymapConflict = null; ApplyKeymapBinding(pending.Action, pending.Slot, pending.Chord, reassign: true); });
            reassign.Name = "KeymapReassign";
            AutomationProperties.SetName(reassign, "Reassign the shortcut to " + (KeymapActions.Find(conflict.Action)?.Title ?? conflict.Action));
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

    /// <summary>IntelliJ is never edited in place: the first edit makes a custom copy, selects it and then records into it.</summary>
    private async Task CreateToEditAsync(string actionId, int slot)
    {
        var made = await NewKeymapAsync(Keymaps.BuiltinName, prompt: true, "Create custom preset to edit", suggested: "My keymap");
        if (made) keymapAfterCreate = (actionId, slot);
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
        var copy = (sourceName == Keymaps.BuiltinName ? KeymapBindings.IntelliJ(KeymapPlatform) : KeymapBindingsOf(sourceName)).Copy();
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