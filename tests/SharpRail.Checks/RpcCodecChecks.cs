using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Protocol;
using SharpRail.Wire;

namespace SharpRail.Checks;

/// <summary>
/// The generated RPC payload codec (scripts/generate-rpc.py) against Fixtures/rpc-codec/golden.jsonl: per record a
/// sample as JSON, the bytes protobuf-net 2.4.8 wrote for it, and what protobuf-net decoded from them, plus
/// hand-built decode-only inputs. Every contract type also round-trips built samples.
/// </summary>
internal static class RpcCodecChecks
{
    private const string Fixture = "Fixtures/rpc-codec/golden.jsonl";

    // Unknown fields of every wire type, a group with a nested field among them, appended to every golden message.
    private static readonly byte[] Unknown = Convert.FromHexString("c03eac02c93e0807060504030201d23e027a7adb3e0805c23e0171dc3eed3e04030201");

    internal static void Run(bool update)
    {
        var contracts = typeof(RpcFrame).Assembly.GetTypes().Where(type => type.GetCustomAttribute<ProtoContractAttribute>() is not null)
            .OrderBy(type => type.Name, StringComparer.Ordinal).ToDictionary(type => type.Name);
        var failures = new List<string>();
        foreach (var service in typeof(RpcFrame).Assembly.GetTypes().Where(type => type.IsInterface && type.Name.EndsWith("Rpc", StringComparison.Ordinal)))
            foreach (var method in service.GetMethods())
                foreach (var carried in new[] { method.ReturnType, method.GetParameters()[0].ParameterType }.Select(type => type.IsGenericType ? type.GetGenericArguments()[0] : type))
                    if (!contracts.ContainsKey(carried.Name)) failures.Add($"{service.Name}.{method.Name} carries {carried.Name}, which has no codec");

        foreach (var type in contracts.Values)
            foreach (var (name, sample) in Samples(type).Append(("large", Rich(type, 0, Large))))
            {
                var encoded = Encode(type, sample);
                var decoded = Decode(type, encoded);
                var settled = Encode(type, decoded);
                if (!settled.AsSpan().SequenceEqual(Encode(type, Decode(type, settled)))) failures.Add($"{type.Name}/{name}: a second round trip changed the bytes");
                if (name is "rich" or "large" && !JsonNode.DeepEquals(Json(type, sample), Json(type, decoded))) failures.Add($"{type.Name}/{name}: the round trip lost a value");
                if ((long)Invoke(nameof(MeasureAs), type, sample) != encoded.Length) failures.Add($"{type.Name}/{name}: measuring disagrees with the encoded length");
                if (!((byte[])Invoke(nameof(StreamAs), type, sample)).AsSpan().SequenceEqual(encoded)) failures.Add($"{type.Name}/{name}: streaming wrote other bytes than encoding");
            }

        var path = Path.Combine(AppContext.BaseDirectory, Fixture);
        var records = File.ReadLines(path).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        var covered = new HashSet<string>();
        var refreshed = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var label = $"{record["type"]}/{record["case"]}";
            if (!contracts.TryGetValue((string)record["type"]!, out var type)) { failures.Add(label + ": no such contract type"); continue; }
            covered.Add(type.Name);
            if (Stale(type, record))
            {
                if (!update) { failures.Add(label + ": the contract changed after this record was captured; run --rpc-codec --update and review the fixture diff"); continue; }
                records[i] = record = Refresh(type, record);
                refreshed++;
            }
            failures.AddRange(Compare(type, record).Select(failure => $"{label}: {failure}"));
        }
        foreach (var type in contracts.Values.Where(type => !covered.Contains(type.Name)))
        {
            if (!update) { failures.Add($"{type.Name}: no golden records; run --rpc-codec --update and review the fixture diff"); continue; }
            foreach (var (name, sample) in Samples(type)) records.Add(Refresh(type, new JsonObject { ["type"] = type.Name, ["case"] = name, ["sample"] = Json(type, sample) }));
            refreshed++;
        }
        if (update && refreshed > 0)
        {
            var source = Path.Combine(BoundaryChecks.Checkout() ?? throw new InvalidOperationException("--update needs a source checkout."), "tests/SharpRail.Checks", Fixture);
            File.WriteAllLines(source, records.Select(record => record.ToJsonString()));
            Console.WriteLine($"Refreshed {refreshed} golden records or types from the generated codec in {source}");
        }
        if (failures.Count > 0) throw new InvalidOperationException("RPC codec checks failed:\n" + string.Join("\n", failures.Take(40)));
        Console.WriteLine($"PASS rpc codec: {contracts.Count} contract types round-trip; {records.Count} golden records match protobuf-net 2.4.8's bytes and decodes");
    }

    private static IEnumerable<string> Compare(Type type, JsonObject record)
    {
        var hex = record["bytes"]?.GetValue<string>();
        if (record["sample"] is { } sampleJson)
        {
            var sample = JsonSerializer.Deserialize(sampleJson.ToJsonString(), type)!;
            var actual = Try(() => Convert.ToHexStringLower(Encode(type, sample)));
            if (record["error"] is not null) return actual is null ? [] : ["encoded a message protobuf-net refused"];
            if (actual != hex) return [$"encoded {actual ?? "nothing (it threw)"}, protobuf-net wrote {hex}"];
        }
        var bytes = Convert.FromHexString(hex!);
        var decoded = Try(() => Decode(type, bytes));
        if (record["decodeError"] is not null) return decoded is null ? [] : ["decoded bytes protobuf-net refused: " + record["decodeError"]];
        if (decoded is null) return ["could not decode bytes protobuf-net decoded"];
        var json = Json(type, decoded);
        var expected = record["decoded"] ?? record["sample"];
        if (!JsonNode.DeepEquals(json, expected)) return [$"decoded {json.ToJsonString()}, protobuf-net {expected!.ToJsonString()}"];
        var reencoded = Convert.ToHexStringLower(Encode(type, decoded));
        var expectedHex = record["reencoded"]?.GetValue<string>() ?? hex;
        if (reencoded != expectedHex) return [$"re-encoded {reencoded}, protobuf-net {expectedHex}"];
        if (!JsonNode.DeepEquals(Json(type, Decode(type, [.. bytes, .. Unknown])), json)) return ["unknown fields changed the decoded message"];
        return [];
    }

    private static T? Try<T>(Func<T> action) where T : class
    {
        try { return action(); }
        catch (Exception) { return null; }
    }

    /// <summary>A record whose JSON no longer matches its type's members was captured before the contract changed.</summary>
    private static bool Stale(Type type, JsonObject record) =>
        record[record["sample"] is not null ? "sample" : "decoded"] is { } json && !JsonNode.DeepEquals(Json(type, JsonSerializer.Deserialize(json.ToJsonString(), type)!), json);

    private static JsonObject Refresh(Type type, JsonObject record)
    {
        var fresh = new JsonObject { ["type"] = type.Name, ["case"] = (string)record["case"]! };
        byte[] bytes;
        if (record["sample"] is { } sampleJson)
        {
            var sample = JsonSerializer.Deserialize(sampleJson.ToJsonString(), type)!;
            fresh["sample"] = Json(type, sample);
            try { bytes = Encode(type, sample); }
            catch (Exception error) { fresh["error"] = error.GetType().Name + ": " + error.Message; return fresh; }
        }
        else bytes = Convert.FromHexString(record["bytes"]!.GetValue<string>());
        fresh["bytes"] = Convert.ToHexStringLower(bytes);
        try
        {
            var decoded = Decode(type, bytes);
            var json = Json(type, decoded);
            if (!JsonNode.DeepEquals(json, fresh["sample"])) fresh["decoded"] = json;
            var reencoded = Convert.ToHexStringLower(Encode(type, decoded));
            if (reencoded != fresh["bytes"]!.GetValue<string>()) fresh["reencoded"] = reencoded;
        }
        catch (Exception error) { fresh["decodeError"] = error.GetType().Name + ": " + error.Message; }
        return fresh;
    }

    private static JsonNode Json(Type type, object value) => JsonNode.Parse(JsonSerializer.Serialize(value, type))!;

    private static byte[] Encode(Type type, object value) => (byte[])Invoke(nameof(EncodeAs), type, value);

    private static object Decode(Type type, byte[] bytes) => Invoke(nameof(DecodeAs), type, bytes);

    private static object Invoke(string name, Type type, object argument)
    {
        try { return typeof(RpcCodecChecks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type).Invoke(null, [argument])!; }
        catch (TargetInvocationException error) when (error.InnerException is { } inner) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }

    private static byte[] EncodeAs<T>(T value) => RpcWire.Encode(value);

    private static T DecodeAs<T>(byte[] bytes) => RpcWire.Decode<T>(bytes);

    private static long MeasureAs<T>(T value) => RpcWire.Measure(value);

    private static byte[] StreamAs<T>(T value)
    {
        var bytes = new List<byte>();
        RpcWire.WriteTo(value, chunk => bytes.AddRange(chunk));
        return [.. bytes];
    }

    // Repeats rich strings and byte arrays past the streaming writer's chunk, so they cross chunk boundaries mid-character.
    private const int Large = 600;

    // The samples golden.jsonl was captured with: defaults, every member set, zeros and empties, and negatives with nulls.
    private static IEnumerable<(string Name, object Sample)> Samples(Type type) =>
        [("default", Activator.CreateInstance(type)!), ("rich", Rich(type, 0)), ("zero", Zero(type)), ("negative-null", Edge(type))];

    private static IEnumerable<(PropertyInfo Property, ProtoMemberAttribute Member, object? Default)> Members(Type type) =>
        type.GetProperties().Select(property => (property, property.GetCustomAttribute<ProtoMemberAttribute>(), property.GetCustomAttribute<DefaultValueAttribute>()?.Value))
            .Where(member => member.Item2 is not null).OrderBy(member => member.Item2!.Tag).Select(member => (member.property, member.Item2!, member.Value));

    private static bool IsMessage(Type type) => type.GetCustomAttribute<ProtoContractAttribute>() is not null;

    private static object Rich(Type type, int depth, int scale = 1)
    {
        var value = Activator.CreateInstance(type)!;
        foreach (var (property, member, defaultValue) in Members(type))
            property.SetValue(value, RichValue(property.PropertyType, property.Name, member.Tag, defaultValue, depth, scale));
        return value;
    }

    private static object RichValue(Type type, string name, int tag, object? defaultValue, int depth, int scale)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return string.Concat(Enumerable.Repeat($"{name}·{tag}✓", scale));
        if (underlying == typeof(int)) return tag * 1000 + 7;
        if (underlying == typeof(long)) return (1L << 40) + tag;
        if (underlying == typeof(bool)) return defaultValue is not true;
        if (underlying.IsEnum) return Enum.GetValues(underlying).Cast<object>().Max()!;
        if (underlying == typeof(byte[])) return Enumerable.Range(0, (tag + 3) * scale).Select(i => (byte)(i * 37)).ToArray();
        if (IsMessage(underlying)) return depth < 2 ? Rich(underlying, depth + 1, scale) : Activator.CreateInstance(underlying)!;
        if (underlying == typeof(Dictionary<string, string>)) return new Dictionary<string, string> { ["k"] = "v", ["empty-value"] = "", [""] = "empty-key" };
        if (underlying == typeof(List<string>)) return new List<string> { "first", "", "ü" };
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            var element = underlying.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(underlying)!;
            if (depth < 2) list.Add(Rich(element, depth + 1, scale));
            list.Add(Activator.CreateInstance(element));
            return list;
        }
        throw new NotSupportedException(type.FullName);
    }

    private static object Zero(Type type)
    {
        var value = Activator.CreateInstance(type)!;
        foreach (var (property, _, _) in Members(type))
        {
            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            property.SetValue(value, underlying == typeof(string) ? "" : underlying == typeof(byte[]) ? Array.Empty<byte>() : Activator.CreateInstance(underlying));
        }
        return value;
    }

    private static object Edge(Type type)
    {
        var value = Activator.CreateInstance(type)!;
        foreach (var (property, member, defaultValue) in Members(type))
        {
            var t = property.PropertyType;
            object? edge;
            if (Nullable.GetUnderlyingType(t) is not null || !t.IsValueType) edge = null;
            else if (t == typeof(int)) edge = member.Tag % 2 == 0 ? int.MinValue : -1;
            else if (t == typeof(long)) edge = member.Tag % 2 == 0 ? long.MinValue : -1L;
            else if (t == typeof(bool)) edge = defaultValue is true;
            else if (t.IsEnum) edge = Enum.GetValues(t).GetValue(0);
            else throw new NotSupportedException(t.FullName);
            property.SetValue(value, edge);
        }
        return value;
    }
}