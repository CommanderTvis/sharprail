using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Isolation;

/// <summary>
/// The plugin process's end of the generated proxies: how a proxy reaches the trusted side. <c>target</c> is the handle of
/// the context the call is on, one per activation and client, so one process can serve several clients' UI contexts.
/// </summary>
public interface IIsolatedRuntime
{
    /// <summary>Calls a member on the trusted side and returns its JSON result.</summary>
    ValueTask<string> CallAsync(int member, long target, string payload, CancellationToken cancellationToken);

    /// <summary>Sends a one-way message: a <see langword="void"/> member or a registration.</summary>
    void OneWay(int member, long target, string payload);

    /// <summary>Lends an object the plugin passed (a callback) and answers its handle.</summary>
    long Lend(object value);

    /// <summary>Reads a mirrored member's local copy for these arguments.</summary>
    T ReadMirror<T>(int member, long target, string payload);
}

/// <summary>The trusted side's way back into the plugin process, used by the generated dispatchers' callback proxies.</summary>
public interface IIsolatedCallbacks
{
    /// <summary>Calls an asynchronous callback the plugin lent.</summary>
    ValueTask<string> CallAsync(int member, long handle, string payload, CancellationToken cancellationToken);

    /// <summary>Calls a <see langword="void"/> callback the plugin lent.</summary>
    void OneWay(int member, long handle, string payload);

    /// <summary>
    /// Calls a synchronous callback the plugin lent, as its crossing class says (<c>Resolver</c>, <c>Live</c> or
    /// <c>UnderLock</c>): within a deadline, from a cache, or with <see langword="default"/> for a miss.
    /// </summary>
    T Sync<T>(int member, long handle, string payload, string crossing, Func<string, T> read);

    /// <summary>Calls a factory the plugin lent and answers the remote surface that shows its control.</summary>
    object Surface(int member, long handle, string payload);
}

/// <summary>What a generated proxy throws for a member the isolated runtime does not implement yet.</summary>
public static class IsolationPending
{
    public static NotSupportedException Member(string name) => new($"{name} is not available to isolated plugins yet.");
}

/// <summary>
/// <see cref="PluginJson.Options"/> as the channel uses them: the same contexts and strictness, but nulls are written, so
/// a record's nullable constructor parameter arrives as <c>null</c> rather than missing.
/// </summary>
internal static class IsoJson
{
    private sealed record Derived(JsonSerializerOptions Source, JsonSerializerOptions Options);

    private static Derived? derived;

    public static JsonTypeInfo<T> TypeInfo<T>()
    {
        var current = PluginJson.Options;
        var cached = Volatile.Read(ref derived);
        if (cached is null || !ReferenceEquals(cached.Source, current))
        {
            cached = new(current, new JsonSerializerOptions(current) { DefaultIgnoreCondition = JsonIgnoreCondition.Never });
            Volatile.Write(ref derived, cached);
        }
        return (JsonTypeInfo<T>)cached.Options.GetTypeInfo(typeof(T));
    }
}

/// <summary>Writes a generated call's payload: a JSON array of its arguments, in <see cref="IsoJson"/>'s options.</summary>
public sealed class IsoPayload
{
    private readonly ArrayBufferWriter<byte> buffer = new();
    private readonly Utf8JsonWriter writer;

    public IsoPayload()
    {
        writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
    }

    public IsoPayload Value<T>(T value)
    {
        JsonSerializer.Serialize(writer, value, IsoJson.TypeInfo<T>());
        return this;
    }

    /// <summary>A lent object's handle, or <see langword="null"/>.</summary>
    public IsoPayload Handle(long? handle)
    {
        if (handle is { } value) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
        return this;
    }

    /// <summary>Another payload as one element: the arguments of the member a handle was reached through.</summary>
    public IsoPayload Raw(string payload)
    {
        writer.WriteRawValue(payload, skipInputValidation: true);
        return this;
    }

    public IsoPayload Object()
    {
        writer.WriteStartObject();
        return this;
    }

    public IsoPayload Name(string name)
    {
        writer.WritePropertyName(name);
        return this;
    }

    public IsoPayload End()
    {
        writer.WriteEndObject();
        return this;
    }

    public string Finish()
    {
        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>A result: one JSON value.</summary>
    public static string Result<T>(T value) => JsonSerializer.Serialize(value, IsoJson.TypeInfo<T>());

    /// <summary>Reads a result.</summary>
    public static T Read<T>(string json)
    {
        try { return JsonSerializer.Deserialize(json, IsoJson.TypeInfo<T>())!; }
        catch (JsonException error) { throw new IsoCallException(IsoError.InvalidParams, "The result is not valid: " + error.Message); }
    }
}

/// <summary>Reads a generated call's payload. A missing or mistyped argument is <see cref="IsoError.InvalidParams"/>.</summary>
public readonly struct IsoArgs
{
    private readonly JsonElement array;

    private IsoArgs(JsonElement array) => this.array = array;

    public static IsoArgs Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw Invalid("the arguments are not an array");
            return new(document.RootElement.Clone());
        }
        catch (JsonException error) { throw Invalid(error.Message); }
    }

    public T Value<T>(int index) => IsoObject.Read<T>(At(index), "argument " + index);

    /// <summary>
    /// A value of the plugin's own type, which the trusted side replays as a JSON object: null members are dropped at
    /// every depth, as <see cref="PluginJson.Options"/> writes a typed value, and anything but an object is refused.
    /// </summary>
    public JsonObject Node(int index)
    {
        var node = At(index) is { ValueKind: JsonValueKind.Object } value ? JsonObject.Create(value)! : throw Invalid($"argument {index} is not an object");
        DropNulls(node);
        return node;
    }

    private static void DropNulls(JsonNode? node)
    {
        if (node is JsonObject members)
        {
            foreach (var name in members.Where(member => member.Value is null).Select(member => member.Key).ToList()) members.Remove(name);
            foreach (var member in members) DropNulls(member.Value);
        }
        else if (node is JsonArray items)
            foreach (var item in items) DropNulls(item);
    }

    public long? Handle(int index) => IsoObject.ReadHandle(At(index), "argument " + index);

    /// <summary>The arguments of the member a handle was reached through.</summary>
    public IsoArgs Nested(int index) => At(index) is { ValueKind: JsonValueKind.Array } nested ? new(nested) : throw Invalid($"argument {index} is not an array");

    public IsoObject Object(int index) => At(index) is { ValueKind: JsonValueKind.Object } value ? new(value) : throw Invalid($"argument {index} is not an object");

    private JsonElement At(int index) => index < array.GetArrayLength() ? array[index] : throw Invalid($"argument {index} is missing");

    internal static IsoCallException Invalid(string detail) => new(IsoError.InvalidParams, "Invalid arguments: " + detail);
}

/// <summary>A record argument split by the generator: its data as JSON members, its callbacks as handles.</summary>
public readonly struct IsoObject
{
    private readonly JsonElement value;

    internal IsoObject(JsonElement value) => this.value = value;

    public T Value<T>(string name) => Read<T>(Property(name), name);

    public long? Handle(string name) => ReadHandle(Property(name), name);

    private JsonElement Property(string name) => value.TryGetProperty(name, out var property) ? property : throw IsoArgs.Invalid(name + " is missing");

    internal static T Read<T>(JsonElement element, string what)
    {
        try { return element.Deserialize(IsoJson.TypeInfo<T>())!; }
        catch (JsonException error) { throw IsoArgs.Invalid($"{what}: {error.Message}"); }
    }

    internal static long? ReadHandle(JsonElement element, string what) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number when element.TryGetInt64(out var handle) => handle,
        _ => throw IsoArgs.Invalid(what + " is not a handle"),
    };
}