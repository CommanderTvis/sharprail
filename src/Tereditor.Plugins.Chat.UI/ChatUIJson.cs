using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>
/// Source-generated JSON metadata, with the serializer's default options, for the chat view's client-local preferences
/// and the tool input it shows or edits as JSON.
/// </summary>
[JsonSerializable(typeof(ChatView.ComposerPreferences))]
[JsonSerializable(typeof(ModelPreferences))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
internal sealed partial class ChatUIJson : JsonSerializerContext
{
    /// <summary>The metadata writing indented JSON, for input shown to the user.</summary>
    internal static ChatUIJson Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}