using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace SharpRail.Host.Core;

/// <summary>
/// The small TOML toolkit behind the project's <c>.sharprail</c> files (<c>workspace.toml</c>, <c>language.toml</c>, <c>instructions.toml</c>): a
/// tolerant parse into JSON nodes (unknown keys stay, callers read only what they know), the shared size limit, helpers for
/// editing the syntax tree so comments and layout survive, and an atomic replace.
/// </summary>
internal static class ProjectToml
{
    /// <summary>The largest project TOML file read or written.</summary>
    internal const int MaximumBytes = 64 * 1024;

    /// <summary>The top-level <c>version</c> when it is an integer, else null.</summary>
    internal static long? VersionOf(JsonObject document) => document["version"] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    internal static JsonObject Parse(string text) => (JsonObject)Convert(TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable())!;

    private static JsonNode? Convert(object? value) => value switch
    {
        TomlTable table => new JsonObject(table.Select(pair => KeyValuePair.Create(pair.Key, Convert(pair.Value)))),
        TomlTableArray tables => new JsonArray([.. tables.Select(Convert)]),
        TomlArray array => new JsonArray([.. array.Select(Convert)]),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long integer => JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        null => null,
        _ => JsonValue.Create(value.ToString())
    };

    internal static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
            builder.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' or '\u007f' => $"\\u{(int)c:X4}", _ => c.ToString() });
        return builder.Append('"').ToString();
    }

    internal static string KeyText(string key) => key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : Quote(key);

    internal static string Inline(JsonNode? node) => node switch
    {
        JsonObject table => "{ " + string.Join(", ", table.Where(pair => pair.Value is not null).Select(pair => $"{KeyText(pair.Key)} = {Inline(pair.Value)}")) + " }",
        JsonArray array => "[" + string.Join(", ", array.Select(Inline)) + "]",
        JsonValue value when value.GetValueKind() == JsonValueKind.String => Quote(value.GetValue<string>()),
        JsonValue value => value.ToJsonString(),
        _ => throw new InvalidOperationException("TOML cannot represent null; remove null options.")
    };

    internal static void Sections(StringBuilder text, string[] path, JsonObject table)
    {
        text.Append('[').Append(string.Join('.', path.Select(KeyText))).Append("]\n");
        foreach (var (key, value) in table)
            if (value is not (null or JsonObject)) text.Append(KeyText(key)).Append(" = ").Append(Inline(value)).Append('\n');
        foreach (var (key, value) in table)
            if (value is JsonObject child) Sections(text, [.. path, key], child);
    }

    internal static string[] Parts(KeySyntax key)
    {
        static string Text(BareKeyOrStringValueSyntax part) => part is BareKeySyntax bare ? bare.Key!.Text! : ((StringValueSyntax)part).Value!;
        return [Text(key.Key!), .. key.DotKeys.Select(item => Text(item.Key!))];
    }

    internal static bool Under(string[] path, string[] unit) => path.Length >= unit.Length && path.Take(unit.Length).SequenceEqual(unit);

    /// <summary>Replaces <paramref name="path"/> atomically (temporary sibling, then move), creating its directory.</summary>
    internal static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}