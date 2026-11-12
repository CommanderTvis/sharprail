using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.Agent.UI;

/// <summary>Source-generated JSON metadata for the strings the agent rows quote as JSON.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class AgentUIJson : JsonSerializerContext
{
    /// <summary>The metadata escaping only what JSON requires, as JavaScript's <c>JSON.stringify</c> does.</summary>
    internal static AgentUIJson Relaxed { get; } = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}