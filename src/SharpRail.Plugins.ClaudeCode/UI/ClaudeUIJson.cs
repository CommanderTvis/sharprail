using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>Source-generated JSON metadata for the values the Claude Code panes show as JSON text.</summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ClaudeUIJson : JsonSerializerContext
{
    /// <summary>The metadata escaping only what JSON requires, as JavaScript's <c>JSON.stringify</c> does.</summary>
    internal static ClaudeUIJson Relaxed { get; } = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}