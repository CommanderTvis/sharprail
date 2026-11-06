using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharpRail.Plugins.Acp.Host;

/// <summary>
/// Source-generated JSON metadata, with the serializer's default options, for the JSON this host keeps outside plugin
/// payloads: the registry and icon cache records, and agent tool input taken from ACP messages.
/// </summary>
[JsonSerializable(typeof(AcpIconMeta))]
[JsonSerializable(typeof(AcpRegistryMeta))]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class AcpLocalJson : JsonSerializerContext
{
    /// <summary>A node as an element; a missing node is an empty object, as tool input with none.</summary>
    internal static JsonElement Input(JsonNode? node) => node is null ? JsonElement.Parse("{}") : JsonSerializer.SerializeToElement(node, Default.JsonNode);
}