using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>The shape questions every file in this module asks of parsed JSON, and writing it back the way the file already reads.</summary>
internal static class Json
{
    public static JsonObject? ReadObject(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static IReadOnlyList<string> StringList(JsonNode? value) =>
        value is JsonArray array ? [.. array.OfType<JsonValue>().Where(item => item.GetValueKind() == JsonValueKind.String).Select(item => item.GetValue<string>())] : [];

    public static string? String(JsonNode? value) =>
        value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : null;

    public static bool IsTrue(JsonNode? value) => value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.True;

    public static JsonElement Element(JsonNode? value) => value is null ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(value);

    /// <summary>JavaScript's <c>JSON.stringify(value, null, indent)</c>: two-space or the file's own indent, non-ASCII kept.</summary>
    public static string Stringify(JsonNode? value, string indent = "  ")
    {
        var tab = indent.Length > 0 && indent.All(character => character == '\t');
        var spaces = indent.Length > 0 && indent.All(character => character == ' ');
        var options = new JsonWriterOptions
        {
            Indented = true,
            IndentCharacter = tab ? '\t' : ' ',
            IndentSize = tab || spaces ? Math.Min(indent.Length, 127) : 2,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            if (value is null) writer.WriteNullValue();
            else value.WriteTo(writer);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JavaScript's <c>JSON.stringify(value)</c>: one line, non-ASCII kept.</summary>
    public static string Compact(JsonElement value) => JsonSerializer.Serialize(value, CompactOptions);

    /// <summary>A copy of a value, so placing it in a second tree does not detach it from the first.</summary>
    public static JsonNode? Clone(JsonNode? value) => value?.DeepClone();

    /// <summary>JavaScript's <c>===</c> over parsed JSON: primitives by value, containers never equal.</summary>
    public static bool StrictEquals(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is not JsonValue || right is not JsonValue) return ReferenceEquals(left, right);
        var kind = left.GetValueKind();
        if (kind != right.GetValueKind()) return false;
        return kind switch
        {
            JsonValueKind.Number => left.GetValue<double>() == right.GetValue<double>(),
            JsonValueKind.String => left.GetValue<string>() == right.GetValue<string>(),
            _ => true
        };
    }
}