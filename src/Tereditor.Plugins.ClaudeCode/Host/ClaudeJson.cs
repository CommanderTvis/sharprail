using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.ClaudeCode.Host;

/// <summary>
/// Source-generated JSON metadata, with the serializer's default options, for the JSON this host handles outside plugin
/// payloads: Claude Code's stream-json, its settings files and hook reports. Messages to Claude Code are built as JSON
/// nodes, which write the bytes the reflection serializer wrote for the same shapes.
/// </summary>
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class ClaudeJson : JsonSerializerContext
{
    /// <summary>The same metadata escaping only what JSON requires, as JavaScript's <c>JSON.stringify</c> does.</summary>
    internal static ClaudeJson Relaxed { get; } = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>A new empty JSON object.</summary>
    internal static JsonElement EmptyObject() => System.Text.Json.JsonElement.Parse("{}");

    /// <summary>A new empty JSON array.</summary>
    internal static JsonElement EmptyArray() => System.Text.Json.JsonElement.Parse("[]");

    /// <summary>
    /// An element as a node that writes the element's own bytes, as the reflection serializer wrote an element member;
    /// <see cref="JsonValue.Create(JsonElement, JsonNodeOptions?)"/> refuses objects and arrays.
    /// </summary>
    internal static JsonNode? Node(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonObject.Create(element),
        JsonValueKind.Array => JsonArray.Create(element),
        _ => JsonValue.Create(element)
    };

    /// <summary>A node as an element; a missing node is JSON <c>null</c>.</summary>
    internal static JsonElement Element(JsonNode? node) => node is null ? System.Text.Json.JsonElement.Parse("null") : JsonSerializer.SerializeToElement(node, Default.JsonNode);
}