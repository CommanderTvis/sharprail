using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// Source-generated JSON metadata, with the serializer's default options, for the JSON this host handles outside plugin
/// payloads: the app-server and IDE bridge protocols, Codex's configuration, rollouts and the skills stamp file. Protocol
/// messages are built as JSON nodes, which write the bytes the reflection serializer wrote for the same shapes.
/// </summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class CodexHostJson : JsonSerializerContext
{
    /// <summary>The metadata escaping only what JSON requires, as JavaScript's <c>JSON.stringify</c> does.</summary>
    internal static CodexHostJson Relaxed { get; } = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>A node as an element; a missing node is JSON <c>null</c>.</summary>
    internal static JsonElement Element(JsonNode? node) => node is null ? System.Text.Json.JsonElement.Parse("null") : JsonSerializer.SerializeToElement(node, Default.JsonNode);
}