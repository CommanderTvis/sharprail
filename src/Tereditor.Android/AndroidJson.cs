using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpRail.UI.Android;

/// <summary>Source-generated JSON metadata, with the serializer's default options, for the saved endpoint and the embedded node's status.</summary>
[JsonSerializable(typeof(HostEndpoint))]
[JsonSerializable(typeof(EmbeddedTailscaleStatus))]
internal sealed partial class AndroidJson : JsonSerializerContext
{
    /// <summary>The metadata reading names in any case, as the embedded node's status arrives.</summary>
    internal static AndroidJson CaseInsensitive { get; } = new(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}