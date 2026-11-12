using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.Codex.UI;

/// <summary>Source-generated JSON metadata for the values the Codex panes show as JSON text.</summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CodexUIJson : JsonSerializerContext
{
    /// <summary>The metadata escaping only what JSON requires, as JavaScript's <c>JSON.stringify</c> does.</summary>
    internal static CodexUIJson Relaxed { get; } = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}