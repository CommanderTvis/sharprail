using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

internal sealed record ScopedDocument(ClaudeConfigOrigin Origin, JsonObject Data);

internal sealed record FlatValue(JsonNode? Value, IReadOnlyList<string> KeyPath);

/// <summary>Resolves settings across scopes: the highest-precedence file wins, list-valued permission keys union.</summary>
internal static partial class SettingsMerge
{
    public static Dictionary<string, FlatValue> Flatten(JsonObject data, IReadOnlyList<string>? prefix = null, Dictionary<string, FlatValue>? output = null)
    {
        output ??= [];
        foreach (var (key, value) in data)
        {
            IReadOnlyList<string> keyPath = [.. prefix ?? [], key];
            if (value is JsonObject nested) Flatten(nested, keyPath, output);
            else output[string.Join('.', keyPath)] = new(value, keyPath);
        }
        return output;
    }

    [GeneratedRegex(@"^(permissions\.(allow|deny|ask|additionalDirectories)|deniedMcpServers|.*\.(allowedDomains|deniedDomains))$")]
    private static partial Regex Unioned();

    public static bool IsUnionKey(string key) => Unioned().IsMatch(key);

    /// <param name="documents">Highest precedence first.</param>
    public static IReadOnlyList<ClaudeSettingValue> Resolve(IReadOnlyList<ScopedDocument> documents)
    {
        var flattened = documents.Select(document => (document.Origin, Values: Flatten(document.Data))).ToArray();
        var keys = new SortedSet<string>(flattened.SelectMany(document => document.Values.Keys), StringComparer.Ordinal);

        static ClaudeConfigOrigin Located((ClaudeConfigOrigin Origin, Dictionary<string, FlatValue> Values) document, string key) =>
            document.Values.TryGetValue(key, out var value) ? document.Origin with { KeyPath = value.KeyPath } : document.Origin;

        var resolved = new List<ClaudeSettingValue>();
        foreach (var key in keys)
        {
            var present = flattened.Where(document => document.Values.ContainsKey(key)).ToArray();
            if (present.Length == 0) continue;
            var first = present[0];
            IReadOnlyList<ClaudeShadowedValue> shadowed = [.. present.Skip(1).Select(document => new ClaudeShadowedValue(Json.Element(document.Values[key].Value), Located(document, key)))];

            if (IsUnionKey(key) && present.All(document => document.Values[key].Value is JsonArray))
            {
                var merged = new JsonArray();
                foreach (var document in present)
                    foreach (var entry in (JsonArray)document.Values[key].Value!)
                        if (!merged.Any(seen => Json.StrictEquals(seen, entry))) merged.Add(Json.Clone(entry));
                resolved.Add(new(key, Json.Element(merged), Located(first, key), shadowed));
                continue;
            }
            resolved.Add(new(key, Json.Element(first.Values[key].Value), Located(first, key), shadowed));
        }
        return resolved;
    }
}