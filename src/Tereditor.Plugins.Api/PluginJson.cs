using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tereditor.Plugins.Api;

/// <summary>
/// The one JSON shape of everything a plugin exchanges: manifests, method params and results, channel payloads,
/// settings namespaces and state files. Every type is serialized from source-generated metadata: a plugin supplies
/// a <see cref="JsonSerializerContext"/> naming its types (see <see cref="PluginContract.Json"/>), and
/// <see cref="Options"/> combines every registered context. A type no context names fails with a
/// <see cref="NotSupportedException"/> naming it; nothing falls back to reflection.
/// </summary>
public static class PluginJson
{
    private static readonly Lock Gate = new();
    private static readonly List<IJsonTypeInfoResolver> Resolvers = [PluginApiJson.Default];
    private static JsonSerializerOptions options = CreateOptions(Resolvers);

    /// <summary>
    /// Strict serializer options: camelCase names and enum values, no unmapped members, required constructor
    /// parameters and nullable annotations respected, nulls omitted. Their resolver is every registered context, so
    /// the instance is replaced when a context is registered; read it at the call rather than keeping it. A
    /// deserialization failure is a <see cref="JsonException"/> whose <see cref="JsonException.Path"/> names the
    /// offending member.
    /// </summary>
    public static JsonSerializerOptions Options => Volatile.Read(ref options);

    /// <summary>
    /// Makes a context's types serializable with <see cref="Options"/>, along with the
    /// <see cref="EnumConverter{TEnum}"/>s its <see cref="JsonSourceGenerationOptionsAttribute.Converters"/> name.
    /// Creating a <see cref="PluginContract"/> registers the contract's contexts, and the host runtime registers a
    /// host half's own; a plugin calls this only for a context neither carries. Registering the same resolver again
    /// does nothing.
    /// </summary>
    /// <param name="resolver">A source-generated context. Register contexts one by one rather than a
    /// <c>JsonTypeInfoResolver.Combine</c> of them, whose enum converters cannot be read.</param>
    public static void Register(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (Gate)
        {
            if (Resolvers.Contains(resolver)) return;
            Resolvers.Add(resolver);
            Volatile.Write(ref options, CreateOptions(Resolvers));
        }
    }

    /// <summary>The metadata <see cref="Options"/> holds for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A type a registered context names.</typeparam>
    /// <returns>The type's metadata.</returns>
    /// <exception cref="NotSupportedException">No registered context names <typeparamref name="T"/>.</exception>
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    /// <summary>The metadata <see cref="Options"/> holds for <paramref name="type"/>.</summary>
    /// <param name="type">A type a registered context names.</param>
    /// <returns>The type's metadata.</returns>
    /// <exception cref="NotSupportedException">No registered context names <paramref name="type"/>.</exception>
    public static JsonTypeInfo TypeInfo(Type type) => Options.GetTypeInfo(type);

    /// <summary>Writes <paramref name="value"/> as JSON text.</summary>
    /// <typeparam name="T">The declared type, which a registered context names.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());

    /// <summary>Writes <paramref name="value"/> as UTF-8 JSON.</summary>
    /// <typeparam name="T">The declared type, which a registered context names.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The UTF-8 JSON bytes.</returns>
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());

    /// <summary>Writes <paramref name="value"/> as a JSON element.</summary>
    /// <typeparam name="T">The declared type, which a registered context names.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The JSON element.</returns>
    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());

    /// <summary>Reads JSON text as a <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type, which a registered context names.</typeparam>
    /// <param name="json">The JSON text.</param>
    /// <returns>The value, or the type's default for a JSON <c>null</c>.</returns>
    /// <exception cref="JsonException">The text does not fit <typeparamref name="T"/>.</exception>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

    /// <summary>Reads a JSON element as a <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type, which a registered context names.</typeparam>
    /// <param name="element">The JSON element.</param>
    /// <returns>The value, or the type's default for a JSON <c>null</c>.</returns>
    /// <exception cref="JsonException">The element does not fit <typeparamref name="T"/>.</exception>
    public static T? Deserialize<T>(JsonElement element) => element.Deserialize(TypeInfo<T>());

    /// <summary>
    /// Writes <paramref name="value"/> as a JSON element by its runtime type, for a value whose declared type is
    /// <see cref="object"/>; <see langword="null"/> is a JSON <c>null</c> and a <see cref="JsonElement"/> is returned
    /// as is.
    /// </summary>
    /// <param name="value">The value, whose runtime type a registered context names.</param>
    /// <returns>The JSON element.</returns>
    /// <exception cref="NotSupportedException">No registered context names the value's type.</exception>
    public static JsonElement ToElement(object? value) => value switch
    {
        JsonElement json => json,
        null => JsonSerializer.SerializeToElement(null, PluginApiJson.Default.NullableJsonElement),
        _ => JsonSerializer.SerializeToElement(value, TypeInfo(value.GetType()))
    };

    /// <summary>
    /// Returns <paramref name="value"/> as a <typeparamref name="T"/>. A plugin payload crosses a process
    /// boundary as JSON and stays an object in process; a value already of type <typeparamref name="T"/> is
    /// returned as is, and anything else (a <see cref="JsonElement"/>, or an equivalent type) is converted through
    /// JSON with <see cref="Options"/>, written as its runtime type (<see cref="ToElement"/>).
    /// </summary>
    /// <typeparam name="T">The expected type, which a registered context names.</typeparam>
    /// <param name="value">A typed value or a JSON element.</param>
    /// <returns>The value as <typeparamref name="T"/>.</returns>
    /// <exception cref="JsonException">The value does not fit <typeparamref name="T"/>.</exception>
    /// <exception cref="NotSupportedException">No registered context names <typeparamref name="T"/> or the value's type.</exception>
    public static T Convert<T>(object? value) =>
        value is T typed ? typed : ToElement(value).Deserialize(TypeInfo<T>()) ?? throw new JsonException($"Expected a {typeof(T).Name}, not null.");

    private static JsonSerializerOptions CreateOptions(List<IJsonTypeInfoResolver> resolvers)
    {
        var created = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new EnumGuard(JsonTypeInfoResolver.Combine([.. resolvers]))
        };
        foreach (var resolver in resolvers)
            if (resolver is JsonSerializerContext context)
                foreach (var converter in context.Options.Converters)
                    if (converter is IEnumConverter && !created.Converters.Any(existing => existing.GetType() == converter.GetType())) created.Converters.Add(converter);
        created.MakeReadOnly();
        return created;
    }

    /// <summary>
    /// The converter that writes <typeparamref name="TEnum"/> as a camelCase string and refuses numbers, as every
    /// enum in a plugin payload is written. A plugin's context names one per enum its types reach, in
    /// <see cref="JsonSourceGenerationOptionsAttribute.Converters"/>; an enum without one fails with a
    /// <see cref="NotSupportedException"/> naming it rather than being written as a number.
    /// </summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <example>
    /// <code>
    /// [JsonSourceGenerationOptions(Converters = [typeof(PluginJson.EnumConverter&lt;TodoState&gt;)])]
    /// [JsonSerializable(typeof(TodoSettings))]
    /// internal sealed partial class TodoJson : JsonSerializerContext;
    /// </code>
    /// </example>
    public sealed class EnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false), IEnumConverter
        where TEnum : struct, Enum;

    private interface IEnumConverter;

    // An enum with no string converter would silently be written as a number, unlike every persisted file.
    private sealed class EnumGuard(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type.IsEnum && !options.Converters.Any(converter => converter is IEnumConverter && converter.CanConvert(type)))
                throw new NotSupportedException($"The enum {type.FullName} has no PluginJson.EnumConverter; name one in its plugin's JsonSerializerContext Converters.");
            return inner.GetTypeInfo(type, options);
        }
    }
}

[JsonSourceGenerationOptions(Converters = [
    typeof(PluginJson.EnumConverter<PluginChannelKind>), typeof(PluginJson.EnumConverter<PluginCallError>),
    typeof(PluginJson.EnumConverter<PluginToolSide>), typeof(PluginJson.EnumConverter<PluginFileRead>),
    typeof(PluginJson.EnumConverter<PluginOrigin>), typeof(PluginJson.EnumConverter<PluginStatus>),
    typeof(PluginJson.EnumConverter<HostPlatform>), typeof(PluginJson.EnumConverter<HostKind>), typeof(PluginJson.EnumConverter<HeadlessSupport>)])]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginRosterEntry[]))]
[JsonSerializable(typeof(IReadOnlyList<PluginRosterEntry>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class PluginApiJson : JsonSerializerContext;