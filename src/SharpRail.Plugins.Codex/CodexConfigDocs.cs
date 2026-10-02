using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Codex;

public enum CodexValueShape
{
    Text,
    Number,
    Switch,
    List
}

/// <summary>What the reference says about a key: its documentation link, its enum choices, the editor its type implies.</summary>
public static partial class CodexConfigDocs
{
    private static readonly Dictionary<string, string> Types = CodexConfigKeys.All.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static string? DocumentedAncestor(string key)
    {
        var candidate = key;
        while (!Types.ContainsKey(candidate))
        {
            var cut = candidate.LastIndexOf('.');
            if (cut == -1) return null;
            candidate = candidate[..cut];
        }
        return candidate;
    }

    /// <summary>The key's reference row through a text fragment, since the rows carry no anchors; a nested key links its nearest documented ancestor.</summary>
    public static string? DocsUrl(string key)
    {
        if (DocumentedAncestor(key) is not { } documented) return null;
        var type = TypeCut().Split(Types[documented])[0];
        var suffix = type.Length > 0 ? ",-" + Uri.EscapeDataString(type) : "";
        return $"{CodexConfigKeys.DocsUrl}#:~:text={Uri.EscapeDataString(documented)}{suffix}";
    }

    /// <summary>A type written as <c>a | b | c</c> makes the key a picker; null for anything else.</summary>
    public static IReadOnlyList<string>? EnumValues(string key)
    {
        if (!Types.TryGetValue(key, out var type)) return null;
        var values = type.Split(" | ").Select(part => part.Trim())
            .Where(part => EnumWord().IsMatch(part) && part is not "string" and not "boolean").ToArray();
        return values.Length >= 2 ? values : null;
    }

    public static CodexValueShape? ValueShape(string key)
    {
        if (!Types.TryGetValue(key, out var type) || EnumValues(key) is not null) return null;
        if (type.StartsWith("boolean", StringComparison.Ordinal)) return CodexValueShape.Switch;
        if (type.StartsWith("number", StringComparison.Ordinal) || type.StartsWith("integer", StringComparison.Ordinal)) return CodexValueShape.Number;
        if (type.StartsWith("array<string>", StringComparison.Ordinal)) return CodexValueShape.List;
        if (type.StartsWith("string", StringComparison.Ordinal)) return CodexValueShape.Text;
        return null;
    }

    /// <summary>The documented, editable leaf keys "Add a setting" offers, in the reference's order.</summary>
    public static readonly IReadOnlyList<string> AddableKeys =
        [.. CodexConfigKeys.All.Select(pair => pair.Key).Where(key => !key.Contains('<') && (EnumValues(key) is not null || ValueShape(key) is not null))];

    [GeneratedRegex(@"[\s|<(]")]
    private static partial Regex TypeCut();

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex EnumWord();
}