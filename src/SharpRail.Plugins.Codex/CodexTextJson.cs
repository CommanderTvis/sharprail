using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpRail.Plugins.Codex;

/// <summary>Source-generated JSON metadata for the strings a launch line quotes as JSON.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class CodexTextJson : JsonSerializerContext
{
    /// <summary>The metadata escaping only what JSON requires, as JavaScript's <c>JSON.stringify</c> does.</summary>
    internal static CodexTextJson Relaxed { get; } = new(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}