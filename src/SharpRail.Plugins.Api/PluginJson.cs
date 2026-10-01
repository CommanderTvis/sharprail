using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpRail.Plugins.Api;

/// <summary>
/// The one JSON shape of everything a plugin exchanges: manifests, method params and results, channel payloads,
/// settings namespaces and state files.
/// </summary>
public static class PluginJson
{
    /// <summary>
    /// Strict serializer options: camelCase names and enum values, no unmapped members, required constructor
    /// parameters and nullable annotations respected. A deserialization failure is a
    /// <see cref="JsonException"/> whose <see cref="JsonException.Path"/> names the offending member.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Returns <paramref name="value"/> as a <typeparamref name="T"/>. A plugin payload crosses a process
    /// boundary as JSON and stays an object in process; a value already of type <typeparamref name="T"/> is
    /// returned as is, and anything else (a <see cref="JsonElement"/>, or an equivalent type from another load
    /// context) is converted through JSON with <see cref="Options"/>.
    /// </summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="value">A typed value or a JSON element.</param>
    /// <returns>The value as <typeparamref name="T"/>.</returns>
    /// <exception cref="JsonException">The value does not fit <typeparamref name="T"/>.</exception>
    public static T Convert<T>(object? value)
    {
        if (value is T typed) return typed;
        var element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value, Options);
        return element.Deserialize<T>(Options) ?? throw new JsonException($"Expected a {typeof(T).Name}, not null.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}