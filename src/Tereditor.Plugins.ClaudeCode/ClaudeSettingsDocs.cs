namespace Tereditor.Plugins.ClaudeCode;

/// <summary>One key of Claude Code's settings reference, as <c>scripts/GenerateSettingsReference.cs</c> reads it.</summary>
/// <param name="Anchor">The id of the key's heading on the reference page, taken from the page rather than derived.</param>
/// <param name="Summary">The reference index's one-line summary.</param>
/// <param name="Scope">The files it may go in, in the reference's words: "Any file", "User or managed", "Managed", "Global config"...</param>
/// <param name="Kind">boolean, number, string, array (a list of text) or object (anything structured, edited as a file); empty for an entry that is not a key of its own, or a removed one.</param>
/// <param name="Type">The first clause of the documented type.</param>
/// <param name="Default">The first clause of the documented default, or null when none is given.</param>
/// <param name="Values">The closed set of strings it accepts, or null when its set is open.</param>
public sealed record ClaudeDocumentedSetting(string Key, string Anchor, string Summary, string Scope, string Kind, string Type, string? Default, IReadOnlyList<string>? Values);

public static partial class ClaudeSettingsReference
{
    // Built on first use: the order of static initializers across the two halves of this partial class is unspecified.
    private static Dictionary<string, ClaudeDocumentedSetting>? byKey;
    private static IReadOnlyList<string>? keys;

    /// <summary>Every documented key, the list "Add a setting" offers.</summary>
    public static IReadOnlyList<string> Keys => keys ??= [.. All.Select(entry => entry.Key)];

    public static ClaudeDocumentedSetting? Find(string key) =>
        (byKey ??= All.ToDictionary(entry => entry.Key, StringComparer.Ordinal)).GetValueOrDefault(key);

    /// <summary>
    /// The key's entry on the reference page, or its nearest documented ancestor's: a nested key is explained by the
    /// section above it. The anchor is the page's own heading id, never a slug guessed from the key.
    /// </summary>
    public static string? Url(string key)
    {
        for (var candidate = key; ;)
        {
            if (Find(candidate) is { } entry) return $"{DocsUrl}#{entry.Anchor}";
            var cut = candidate.LastIndexOf('.');
            if (cut == -1) return null;
            candidate = candidate[..cut];
        }
    }

    /// <summary>
    /// The settings files the reference lets the key go in, among those the pane writes. Empty for keys only managed
    /// settings or <c>~/.claude.json</c> read, and for entries that are not keys of their own.
    /// </summary>
    public static IReadOnlyList<ClaudeWritableScope> WritableScopes(ClaudeDocumentedSetting entry)
    {
        if (entry.Kind.Length == 0) return [];
        if (entry.Scope == "Any file") return ClaudeValues.WritableScopes;
        var words = entry.Scope.ToLowerInvariant().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        return [.. ClaudeValues.WritableScopes.Where(scope => words.Contains(scope.Name()))];
    }

    /// <summary>
    /// The documented keys whose name contains <paramref name="query"/> and that no file sets, in the reference's
    /// order. A key above or below a set one is left out: <c>permissions</c> is not unset while <c>permissions.allow</c>
    /// is, and setting it would replace that value.
    /// </summary>
    public static IReadOnlyList<ClaudeDocumentedSetting> Unset(IEnumerable<string> setKeys, string query)
    {
        var set = setKeys.ToArray();
        return [.. All.Where(entry => entry.Kind.Length > 0 && entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !set.Any(key => key == entry.Key || key.StartsWith(entry.Key + ".", StringComparison.Ordinal) || entry.Key.StartsWith(key + ".", StringComparison.Ordinal)))];
    }
}