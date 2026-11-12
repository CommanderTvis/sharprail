using Avalonia.Input;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI;

/// <summary>
/// The keymap this client runs: the active preset's chords per action, set from the host snapshot (<see cref="Set(HostState)"/>) and read by
/// <see cref="WindowShortcuts"/>. UI-thread state; a change raises <see cref="Changed"/> so menus re-show their chords in place.
/// </summary>
public static class ActiveKeymap
{
    private static IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> chords = Keymaps.Effective([], "", Platform);

    /// <summary>Raised on the UI thread after the active chords changed.</summary>
    public static event Action? Changed;

    private static bool recording;

    /// <summary>
    /// Whether a keymap recorder holds the keyboard: window chords and the menu bar's key equivalents step aside so the pressed chord
    /// reaches the recorder instead of running its command.
    /// </summary>
    public static bool Recording
    {
        get => recording;
        set { if (recording == value) return; recording = value; Changed?.Invoke(); }
    }

    /// <summary>Which spelling of the command key this client uses.</summary>
    public static KeymapPlatform Platform => OperatingSystem.IsMacOS() ? KeymapPlatform.Mac : KeymapPlatform.Other;

    /// <summary>The chords of one action in the active preset, in order; empty when unbound.</summary>
    public static IReadOnlyList<KeyChord> Chords(string actionId) => chords.GetValueOrDefault(actionId) ?? [];

    private static HostState lastState = new();
    private static IReadOnlyList<KeymapAction> pluginActions = [];

    /// <summary>The keymap actions the enabled plugins contribute now, in registration order.</summary>
    public static IReadOnlyList<KeymapAction> PluginActions => pluginActions;

    /// <summary>Takes the active preset from a host snapshot; raises <see cref="Changed"/> only when some action's chords differ.</summary>
    public static void Set(HostState state)
    {
        lastState = state;
        Recompute();
    }

    /// <summary>
    /// Takes the plugins' actions (a plugin enabled, disabled or re-registering): their defaults and stored bindings join or leave the live
    /// chords. The stored bindings stay in the presets either way.
    /// </summary>
    public static void SetPluginActions(IReadOnlyList<KeymapAction> actions)
    {
        if (pluginActions.SequenceEqual(actions)) return;
        pluginActions = actions;
        Recompute();
    }

    private static void Recompute() =>
        Set(Keymaps.Effective(lastState.KeymapPresets, lastState.Settings.ActiveKeymap, Platform, pluginActions));

    internal static void Set(IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> next)
    {
        if (chords.Count == next.Count && chords.All(entry => next.TryGetValue(entry.Key, out var other) && other.SequenceEqual(entry.Value))) return;
        chords = next;
        Changed?.Invoke();
    }

    /// <summary>Whether a key-down is one of the action's chords.</summary>
    public static bool Matches(string actionId, KeyEventArgs e) => KeyboardChords.FromEvent(e) is { } pressed && Chords(actionId).Contains(pressed);

    /// <summary>The action's first chord as a menu gesture, or a gesture with no key when unbound.</summary>
    public static KeyGesture Gesture(string actionId) => Chords(actionId).Count > 0 && KeyboardChords.ToGesture(Chords(actionId)[0]) is { } gesture ? gesture : new KeyGesture(Key.None);
}

/// <summary>Converts between Avalonia key events and gestures and the keymap's platform-neutral <see cref="KeyChord"/>.</summary>
public static class KeyboardChords
{
    private static readonly Dictionary<string, Key> Named = new()
    {
        ["Left"] = Key.Left,
        ["Right"] = Key.Right,
        ["Up"] = Key.Up,
        ["Down"] = Key.Down,
        ["Home"] = Key.Home,
        ["End"] = Key.End,
        ["PageUp"] = Key.PageUp,
        ["PageDown"] = Key.PageDown,
        ["Insert"] = Key.Insert,
        ["Delete"] = Key.Delete,
        ["Backspace"] = Key.Back,
        ["Enter"] = Key.Enter,
        ["Tab"] = Key.Tab,
        ["Space"] = Key.Space,
        ["Comma"] = Key.OemComma,
        ["Period"] = Key.OemPeriod,
        ["Plus"] = Key.OemPlus,
        ["Minus"] = Key.OemMinus,
        ["Slash"] = Key.OemQuestion,
        ["Backslash"] = Key.OemBackslash,
        ["Semicolon"] = Key.OemSemicolon,
        ["Quote"] = Key.OemQuotes,
        ["Backtick"] = Key.OemTilde,
        ["OpenBracket"] = Key.OemOpenBrackets,
        ["CloseBracket"] = Key.OemCloseBrackets
    };

    /// <summary>The Avalonia key of a chord's key token, or null for a token this build cannot press.</summary>
    public static Key? ToKey(string token)
    {
        if (Named.TryGetValue(token, out var named)) return named;
        if (token.Length == 1 && token[0] is >= 'A' and <= 'Z') return Key.A + (token[0] - 'A');
        if (token.Length == 1 && token[0] is >= '0' and <= '9') return Key.D0 + (token[0] - '0');
        if (token.Length > 1 && token[0] == 'F' && int.TryParse(token.AsSpan(1), out var number) && number is >= 1 and <= 24) return Key.F1 + (number - 1);
        return null;
    }

    private static string? Token(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Add => "Plus",
        Key.Subtract => "Minus",
        _ => Named.FirstOrDefault(entry => entry.Value == key).Key
    };

    /// <summary>The chord a key-down is, with the command key folded for this platform; null for modifier-only and unknown keys.</summary>
    public static KeyChord? FromEvent(KeyEventArgs e)
    {
        if (Token(e.Key) is not { Length: > 0 } token) return null;
        var mods = KeyMods.None;
        var modifiers = e.KeyModifiers;
        // The plus key is a shifted equals key on most layouts; Mod+Plus is the same press with or without Shift.
        if (modifiers.HasFlag(KeyModifiers.Shift) && token != "Plus") mods |= KeyMods.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= KeyMods.Alt;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= KeyMods.Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= KeyMods.Meta;
        return new KeyChord(mods, token).Normalized(ActiveKeymap.Platform);
    }

    /// <summary>The menu gesture of a chord, using this platform's command key; null for a key this build cannot press.</summary>
    public static KeyGesture? ToGesture(KeyChord chord)
    {
        if (ToKey(chord.Key) is not { } key) return null;
        var modifiers = KeyModifiers.None;
        if ((chord.Mods & KeyMods.Mod) != 0) modifiers |= CommandKeys.Modifier;
        if ((chord.Mods & KeyMods.Ctrl) != 0) modifiers |= KeyModifiers.Control;
        if ((chord.Mods & KeyMods.Alt) != 0) modifiers |= KeyModifiers.Alt;
        if ((chord.Mods & KeyMods.Shift) != 0) modifiers |= KeyModifiers.Shift;
        if ((chord.Mods & KeyMods.Meta) != 0) modifiers |= KeyModifiers.Meta;
        return new KeyGesture(key, modifiers);
    }
}