using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Docking;
using SharpRail.UI.Panels;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.UI;

/// <summary>
/// Source-generated JSON metadata for everything the app persists or compares as JSON. Its
/// <see cref="JsonSerializerContext.Default"/> carries the serializer's default options; a site with other options
/// keeps them and resolves its types from this context (<see cref="Resolving"/>), so the bytes stay those the reflection
/// serializer wrote.
/// </summary>
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(DockState))]
[JsonSerializable(typeof(PreviewGroupSignature[]))]
[JsonSerializable(typeof(PreviewTerminalSignature[]))]
[JsonSerializable(typeof(DrawerSignature))]
[JsonSerializable(typeof(GroupSignature))]
[JsonSerializable(typeof(LanguageConfigurationView.LanguageConfigurationDraft))]
[JsonSerializable(typeof(LanguageServerOptions))]
[JsonSerializable(typeof(Dictionary<string, LanguageServerOptions>))]
[JsonSerializable(typeof(TerminalRelay.Connection))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class AppJson : JsonSerializerContext
{
    /// <summary>The web defaults (camelCase, case-insensitive reading, numbers from strings), as <see cref="JsonSerializerOptions.Web"/>.</summary>
    internal static JsonSerializerOptions Web => field ??= Resolving(new(JsonSerializerDefaults.Web));

    /// <summary>Makes <paramref name="options"/> resolve every type from this context and read-only.</summary>
    internal static JsonSerializerOptions Resolving(JsonSerializerOptions options)
    {
        options.TypeInfoResolver = Default;
        options.MakeReadOnly();
        return options;
    }

    /// <summary>The metadata <paramref name="options"/> hold for <typeparamref name="T"/>.</summary>
    internal static JsonTypeInfo<T> Info<T>(JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}