using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Host.Abstractions;

/// <summary>The modifiers of a <see cref="KeyChord"/>; <c>Mod</c> is the platform's command key (Command on macOS, Control elsewhere).</summary>
[Flags]
public enum KeyMods { None = 0, Mod = 1, Ctrl = 2, Alt = 4, Shift = 8, Meta = 16 }

/// <summary>Which spelling of the command key a client uses: Command on macOS, Control on every other platform.</summary>
public enum KeymapPlatform { Mac, Other }

/// <summary>
/// One key with modifiers, spelled <c>Mod+Shift+N</c>. The command key folds into <c>Mod</c> on its platform (Meta on macOS, Ctrl
/// elsewhere), so a chord means the same on every platform that shares a preset. Keys are the tokens of <see cref="Keys"/>.
/// </summary>
public readonly record struct KeyChord(KeyMods Mods, string Key)
{
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(
        [.. Enumerable.Range('A', 26).Select(letter => ((char)letter).ToString()), .. Enumerable.Range(0, 10).Select(digit => digit.ToString()),
         .. Enumerable.Range(1, 24).Select(number => "F" + number),
         "Left", "Right", "Up", "Down", "Home", "End", "PageUp", "PageDown", "Insert", "Delete", "Backspace", "Enter", "Tab", "Space",
         "Comma", "Period", "Plus", "Minus", "Slash", "Backslash", "Semicolon", "Quote", "Backtick", "OpenBracket", "CloseBracket"]);

    private static readonly (KeyMods Flag, string Name)[] Names =
        [(KeyMods.Mod, "Mod"), (KeyMods.Ctrl, "Ctrl"), (KeyMods.Alt, "Alt"), (KeyMods.Shift, "Shift"), (KeyMods.Meta, "Meta")];

    /// <summary>The same chord with the platform's command key folded into <c>Mod</c>.</summary>
    public KeyChord Normalized(KeymapPlatform platform)
    {
        var fold = platform == KeymapPlatform.Mac ? KeyMods.Meta : KeyMods.Ctrl;
        return (Mods & fold) != 0 ? new((Mods & ~fold) | KeyMods.Mod, Key) : this;
    }

    /// <summary>The canonical spelling, modifiers in a fixed order.</summary>
    public string Text { get { var mods = Mods; return string.Concat(Names.Where(name => (mods & name.Flag) != 0).Select(name => name.Name + "+")) + Key; } }

    public override string ToString() => Text;

    /// <summary>Parses a chord, normalizing the command key for <paramref name="platform"/>; false for anything that is not a known key with known modifiers.</summary>
    public static bool TryParse(string? text, KeymapPlatform platform, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        // "Mod++" is not a spelling: the plus key is the token "Plus".
        var key = parts[^1];
        var mods = KeyMods.None;
        foreach (var part in parts[..^1])
        {
            var match = Names.FirstOrDefault(name => name.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null) return false;
            mods |= match.Flag;
        }
        var token = Keys.FirstOrDefault(known => known.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (token is null) return false;
        chord = new KeyChord(mods, token).Normalized(platform);
        return true;
    }

    /// <summary>The chord as people read it: symbols on macOS (⌃⌥⇧⌘N), words elsewhere (Ctrl+Alt+Shift+N).</summary>
    public string Display(KeymapPlatform platform)
    {
        var key = Key switch
        {
            "Comma" => ",",
            "Period" => ".",
            "Plus" => "+",
            "Minus" => "−",
            "Slash" => "/",
            "Backslash" => "\\",
            "Semicolon" => ";",
            "Quote" => "'",
            "Backtick" => "`",
            "OpenBracket" => "[",
            "CloseBracket" => "]",
            "Backspace" => platform == KeymapPlatform.Mac ? "⌫" : "Backspace",
            "Delete" => platform == KeymapPlatform.Mac ? "⌦" : "Delete",
            "Enter" => platform == KeymapPlatform.Mac ? "↩" : "Enter",
            "Left" => "←",
            "Right" => "→",
            "Up" => "↑",
            "Down" => "↓",
            _ => Key
        };
        if (platform == KeymapPlatform.Mac)
        {
            var mods = Normalized(platform).Mods;
            return ((mods & KeyMods.Ctrl) != 0 ? "⌃" : "") + ((mods & KeyMods.Alt) != 0 ? "⌥" : "") + ((mods & KeyMods.Shift) != 0 ? "⇧" : "") +
                ((mods & (KeyMods.Mod | KeyMods.Meta)) != 0 ? "⌘" : "") + key;
        }
        var words = new List<string>();
        var other = Normalized(platform).Mods;
        if ((other & KeyMods.Mod) != 0) words.Add("Ctrl");
        if ((other & KeyMods.Alt) != 0) words.Add("Alt");
        if ((other & KeyMods.Shift) != 0) words.Add("Shift");
        if ((other & KeyMods.Meta) != 0) words.Add("Win");
        words.Add(key);
        return string.Join("+", words);
    }
}

/// <summary>A command the keymap can bind. The id is stable and persisted; the title and group are for people.</summary>
public sealed record KeymapAction(string Id, string Title, string Group);

/// <summary>The commands with bindable chords, in the order the keymap page lists them.</summary>
public static class KeymapActions
{
    public const string OpenProject = "OpenProject", NewWindow = "NewWindow", SearchWorkspace = "SearchWorkspace", NewWorkspace = "NewWorkspace",
        Settings = "Settings", ToggleLeft = "ToggleLeft", ToggleRight = "ToggleRight", ZoomIn = "ZoomIn", ZoomOut = "ZoomOut", ZoomReset = "ZoomReset",
        Refresh = "Refresh", Back = "Back", Forward = "Forward", UndoClose = "UndoClose", RedoClose = "RedoClose";

    public static IReadOnlyList<KeymapAction> All { get; } =
    [
        new(Settings, "Open Settings", "General"),
        new(OpenProject, "Open project", "General"),
        new(NewWorkspace, "New workspace", "General"),
        new(NewWindow, "New window", "General"),
        new(SearchWorkspace, "Search in workspace", "Navigation"),
        new(Back, "Back", "Navigation"),
        new(Forward, "Forward", "Navigation"),
        new(Refresh, "Refresh", "Navigation"),
        new(ToggleLeft, "Toggle left panel", "View"),
        new(ToggleRight, "Toggle right panel", "View"),
        new(ZoomIn, "Zoom in", "View"),
        new(ZoomOut, "Zoom out", "View"),
        new(ZoomReset, "Actual size", "View"),
        new(UndoClose, "Reopen closed tab", "Tabs and windows"),
        new(RedoClose, "Close reopened tab", "Tabs and windows"),
    ];

    public static KeymapAction? Find(string id) => All.FirstOrDefault(action => action.Id == id);
}

/// <summary>A custom keymap: the name is its identity (like a layout preset), the bindings a JSON document <see cref="KeymapBindings"/> reads.</summary>
public sealed record KeymapPreset(string Name, string Bindings);

/// <summary>
/// The bindings of one preset: action id to at most <see cref="MaxChords"/> chord spellings, as <c>{"version":1,"bindings":{"Id":["Mod+O"]}}</c>.
/// Ids and spellings this build does not know are carried untouched, so a preset saved by a newer build survives an edit here.
/// </summary>
public sealed class KeymapBindings
{
    public const int MaxChords = 2;
    public const int Version = 1;

    private readonly JsonObject root;

    private KeymapBindings(JsonObject root) => this.root = root;

    private JsonObject Map => root["bindings"] as JsonObject ?? (JsonObject)(root["bindings"] = new JsonObject());

    /// <summary>Reads a document; anything malformed is an empty preset, never an error.</summary>
    public static KeymapBindings Parse(string? json)
    {
        try
        {
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) is JsonObject parsed)
            {
                parsed["version"] ??= Version;
                if (parsed["bindings"] is not JsonObject) parsed["bindings"] = new JsonObject();
                return new(parsed);
            }
        }
        catch (JsonException) { }
        return new(new JsonObject { ["version"] = Version, ["bindings"] = new JsonObject() });
    }

    /// <summary>Whether <paramref name="json"/> is a well-formed document (an object, with a bindings object of arrays of strings).</summary>
    public static bool IsValid(string? json)
    {
        try
        {
            if (JsonNode.Parse(json ?? "") is not JsonObject { } parsed) return false;
            if (parsed["bindings"] is not JsonObject map) return parsed["bindings"] is null;
            return map.All(entry => entry.Value is JsonArray array && array.All(item => item is JsonValue value && value.TryGetValue<string>(out _)));
        }
        catch (JsonException) { return false; }
    }

    public string ToJson() => root.ToJsonString();

    /// <summary>The preset this one was duplicated from (empty when none): where a row's Reset to preset goes back to.</summary>
    public string BasedOn
    {
        get => root["basedOn"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : "";
        set => root["basedOn"] = value.Length == 0 ? null : value;
    }

    public KeymapBindings Copy() => new((JsonObject)root.DeepClone());

    /// <summary>Every action id the document binds, known or not.</summary>
    public IEnumerable<string> Ids => Map.Select(entry => entry.Key);

    /// <summary>Whether the document says anything about the action (an empty list means deliberately unbound).</summary>
    public bool Has(string actionId) => Map[actionId] is JsonArray;

    /// <summary>The spellings stored for an action, as written.</summary>
    public IReadOnlyList<string> Spellings(string actionId) =>
        Map[actionId] is JsonArray array ? [.. array.Select(item => item?.GetValue<string>() ?? "")] : [];

    /// <summary>The valid chords of an action in order; spellings this build cannot read are skipped.</summary>
    public IReadOnlyList<KeyChord> Chords(string actionId, KeymapPlatform platform) =>
        [.. Spellings(actionId).Select(text => KeyChord.TryParse(text, platform, out var chord) ? (KeyChord?)chord : null).OfType<KeyChord>().Take(MaxChords)];

    /// <summary>Replaces the action's chords in place.</summary>
    public void Set(string actionId, IEnumerable<KeyChord> chords) =>
        Map[actionId] = new JsonArray([.. chords.Take(MaxChords).Select(chord => (JsonNode)JsonValue.Create(chord.Text)!)]);

    public void Remove(string actionId) => Map.Remove(actionId);

    /// <summary>The built-in IntelliJ IDEA keymap for the commands SharpRail has: its macOS keymap on macOS, the Windows/Linux default elsewhere.</summary>
    public static KeymapBindings IntelliJ(KeymapPlatform platform)
    {
        var bindings = new KeymapBindings(new JsonObject { ["version"] = Version, ["bindings"] = new JsonObject() });
        foreach (var (id, mac, other) in IntelliJTable)
            bindings.Set(id, (platform == KeymapPlatform.Mac ? mac : other).Select(text => KeyChord.TryParse(text, platform, out var chord) ? chord : throw new InvalidOperationException(text)));
        return bindings;
    }

    // Action, IntelliJ macOS keymap, IntelliJ Default for Windows/Linux. Settings is Cmd+, on macOS (also the macOS convention) and Ctrl+Alt+S
    // elsewhere. Actions IntelliJ has no chord for keep the chord SharpRail already had; where IntelliJ's chord would collide with a terminal
    // (Alt+1) or the desktop (Ctrl+Alt+Arrow) the existing chord stays.
    private static readonly (string Id, string[] Mac, string[] Other)[] IntelliJTable =
    [
        (KeymapActions.OpenProject, ["Mod+O"], ["Mod+O"]),
        (KeymapActions.NewWindow, ["Mod+Shift+N"], ["Mod+Shift+N"]),
        (KeymapActions.SearchWorkspace, ["Mod+Shift+F"], ["Mod+Shift+F"]),
        (KeymapActions.NewWorkspace, ["Mod+N"], ["Mod+N"]),
        (KeymapActions.Settings, ["Mod+Comma"], ["Mod+Alt+S"]),
        (KeymapActions.ToggleLeft, ["Mod+1"], ["Mod+B"]),
        (KeymapActions.ToggleRight, ["Mod+J"], ["Mod+J"]),
        (KeymapActions.ZoomIn, ["Mod+Plus"], ["Mod+Plus"]),
        (KeymapActions.ZoomOut, ["Mod+Minus"], ["Mod+Minus"]),
        (KeymapActions.ZoomReset, ["Mod+0"], ["Mod+0"]),
        (KeymapActions.Refresh, ["Mod+Alt+Y", "F5"], ["Mod+Alt+Y", "F5"]),
        (KeymapActions.Back, ["Mod+OpenBracket"], ["Alt+Left"]),
        (KeymapActions.Forward, ["Mod+CloseBracket"], ["Alt+Right"]),
        (KeymapActions.UndoClose, ["Mod+Shift+T", "Mod+Z"], ["Mod+Shift+T", "Mod+Z"]),
        (KeymapActions.RedoClose, ["Mod+Shift+Z"], ["Mod+Shift+Z"]),
    ];
}

/// <summary>The outcome of binding a chord; <see cref="Conflicts"/> and <see cref="Reserved"/> mean nothing changed.</summary>
public sealed record BindResult(KeymapBindings Bindings, IReadOnlyList<string> Conflicts, string? Reserved)
{
    public bool Applied => Conflicts.Count == 0 && Reserved is null;
}

/// <summary>Preset operations and the rules around them, shared by the host (which validates) and the clients.</summary>
public static class Keymaps
{
    /// <summary>The one built-in preset; read-only, and its name belongs to it.</summary>
    public const string BuiltinName = "IntelliJ";

    public static bool IsBuiltin(string name) => string.Equals(name?.Trim(), BuiltinName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a preset name is unusable, or null: non-empty after trimming, not the built-in's, not another preset's (case-insensitively).</summary>
    public static string? ValidateName(string? name, IEnumerable<string> existing, string? except = null)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Contains('\0')) return "Enter a preset name.";
        if (IsBuiltin(trimmed)) return $"{BuiltinName} is the built-in preset; choose another name.";
        if (existing.Any(other => other != except && string.Equals(other, trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"A preset named {trimmed} already exists.";
        return null;
    }

    /// <summary>The bindings of the named preset; the built-in, and any name that is gone, resolve to IntelliJ.</summary>
    public static KeymapBindings Resolve(IEnumerable<KeymapPreset> presets, string? active, KeymapPlatform platform) =>
        !string.IsNullOrEmpty(active) && !IsBuiltin(active) && presets.FirstOrDefault(preset => preset.Name == active) is { } found
            ? KeymapBindings.Parse(found.Bindings) : KeymapBindings.IntelliJ(platform);

    /// <summary>
    /// The chord behind each action for the active preset. An action the preset does not mention keeps IntelliJ's chords, so a preset saved
    /// before an action existed binds it too; an action listed with no chords is deliberately unbound.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> Effective(IEnumerable<KeymapPreset> presets, string? active, KeymapPlatform platform)
    {
        var chosen = Resolve(presets, active, platform);
        var builtin = KeymapBindings.IntelliJ(platform);
        return KeymapActions.All.ToDictionary(action => action.Id,
            action => chosen.Has(action.Id) ? chosen.Chords(action.Id, platform) : builtin.Chords(action.Id, platform));
    }

    /// <summary>The chords the terminal (or the system) needs, which no action may take; the reason, or null.</summary>
    public static string? Reserved(KeyChord chord, KeymapPlatform platform)
    {
        chord = chord.Normalized(platform);
        if (chord.Mods == KeyMods.None && chord.Key is "Escape" or "Enter" or "Tab" or "Backspace" or "Space") return "Terminals and text fields need this key.";
        if (chord.Mods == KeyMods.None && chord.Key.Length == 1) return "A plain character key types text.";
        if (chord.Mods == KeyMods.Shift && chord.Key.Length == 1) return "A plain character key types text.";
        if (platform == KeymapPlatform.Mac)
        {
            if (chord.Mods == KeyMods.Ctrl && chord.Key is "C" or "D" or "R" or "Z" or "L" or "U" or "A" or "E" or "K" or "W" or "B" or "F" or "N" or "P") return "Terminals use this control character.";
            if (chord.Mods == KeyMods.Mod && chord.Key is "C" or "V" or "X" or "A" or "K" or "Q" or "W" or "H" or "M" or "Tab" or "Space") return "The system or terminals use this chord.";
        }
        else
        {
            if (chord.Mods == KeyMods.Mod && chord.Key is "C" or "D" or "R" or "L" or "U" or "A" or "E" or "K") return "Terminals use this control character.";
            if (chord.Mods == (KeyMods.Mod | KeyMods.Shift) && chord.Key is "C" or "V") return "Terminals use this chord to copy and paste.";
            if (chord.Mods == KeyMods.Mod && chord.Key is "Q" or "W" or "V" or "X") return "The system uses this chord.";
            if (chord.Mods == KeyMods.Alt && chord.Key is "F4" or "Tab") return "The system uses this chord.";
        }
        return null;
    }

    /// <summary>The other actions of the bindings that already use the chord.</summary>
    public static IReadOnlyList<string> Conflicts(KeymapBindings bindings, string actionId, KeyChord chord, KeymapPlatform platform) =>
        [.. KeymapActions.All.Select(action => action.Id).Where(id => id != actionId && bindings.Chords(id, platform).Contains(chord.Normalized(platform)))];

    /// <summary>
    /// Sets one of an action's two chord slots (null clears it). A reserved chord is refused; a chord another action uses is refused unless
    /// <paramref name="reassign"/>, which takes it from them. Returns the bindings to store; the input is not modified.
    /// </summary>
    public static BindResult Bind(KeymapBindings bindings, string actionId, int slot, KeyChord? chord, KeymapPlatform platform, bool reassign = false)
    {
        if (KeymapActions.Find(actionId) is null || slot is < 0 or >= KeymapBindings.MaxChords) throw new ArgumentException("Unknown action or slot.");
        var next = bindings.Copy();
        var current = bindings.Has(actionId) ? [.. bindings.Chords(actionId, platform)] : new List<KeyChord>();
        if (chord is not { } wanted)
        {
            if (slot < current.Count) current.RemoveAt(slot);
            next.Set(actionId, current);
            return new(next, [], null);
        }
        wanted = wanted.Normalized(platform);
        if (Reserved(wanted, platform) is { } reason) return new(bindings, [], reason);
        var conflicts = Conflicts(bindings, actionId, wanted, platform);
        if (conflicts.Count > 0 && !reassign) return new(bindings, conflicts, null);
        foreach (var other in conflicts) next.Set(other, next.Chords(other, platform).Where(existing => existing != wanted));
        current.Remove(wanted);
        if (slot < current.Count) current[slot] = wanted; else current.Add(wanted);
        next.Set(actionId, current);
        return new(next, [], null);
    }

    /// <summary>Puts an action back to what <paramref name="source"/> says (or removes its entry when the source is silent).</summary>
    public static KeymapBindings Reset(KeymapBindings bindings, KeymapBindings source, string actionId, KeymapPlatform platform)
    {
        var next = bindings.Copy();
        if (source.Has(actionId)) next.Set(actionId, source.Chords(actionId, platform)); else next.Remove(actionId);
        return next;
    }
}