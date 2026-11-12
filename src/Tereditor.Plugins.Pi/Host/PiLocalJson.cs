using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>Source-generated JSON metadata, with the serializer's default options, for Pi RPC JSON this host turns into chat items.</summary>
[JsonSerializable(typeof(JsonObject))]
internal sealed partial class PiLocalJson : JsonSerializerContext;