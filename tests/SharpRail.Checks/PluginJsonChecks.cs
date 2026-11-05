using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;

namespace SharpRail.Checks;

/// <summary>
/// Plugin JSON comes from source-generated metadata only: every builtin plugin's declared methods, channels and
/// settings, and its tools' parameters, resolve through <see cref="PluginJson.Options"/> down to their leaves, with
/// every enum written as a camelCase string.
/// </summary>
internal static class PluginJsonChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static async Task Run(string root)
    {
        var missing = new List<string>();
        foreach (var (manifest, host) in BuiltinPlugins.All)
        {
            if (host is null) continue;
            if (host.Json is { } json) PluginJson.Register(json);
            var seen = new HashSet<Type>();
            foreach (var type in PluginRegistry.DeclaredTypes(host.Contract).Concat(host.Contract.Json.Append(host.Json).SelectMany(Roots)))
                Walk(manifest.Id, type, missing, seen);
        }
        Walk("api", typeof(PluginManifest), missing, []);
        var uiContracts = SharpRail.UI.Plugins.BuiltinPlugins.Contracts.Select(contract => contract.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (manifest, _) in SharpRail.UI.Plugins.BuiltinPlugins.All)
            if (!uiContracts.Contains(manifest.Id)) missing.Add($"{manifest.Id}: the app's builtin contracts do not list its contract");
        Require(missing.Count == 0, "Every builtin plugin type has source-generated JSON metadata:\n" + string.Join('\n', missing));

        // Tools register at activation, which refuses a parameters type without metadata; the schema walks it to its leaves.
        var state = new HostStateStore(Path.Combine(root, "plugin-json-state"));
        var builtins = BuiltinPlugins.All.Where(entry => entry.Manifest.Id is "spec-dialect" or "visualize" or "blueprint" or "processes" or "air-context").ToArray();
        await state.ChangeAsync([.. builtins.Select(entry => HostStateChange.PluginEnabled(entry.Manifest.Id, true))]);
        await using var runtime = new PluginRuntime(new() { StateDirectory = null, State = state, Builtins = builtins });
        await runtime.Start();
        var roster = await runtime.ListAsync();
        Require(roster.All(entry => entry.Status == PluginStatus.Active), "Every plugin with tools activates: " + string.Join(", ", roster.Where(entry => entry.Status != PluginStatus.Active).Select(entry => $"{entry.Id} {entry.Status} {entry.Reason}")));
        var tools = runtime.McpTools(null, root);
        Require(tools.Count == 15 && tools.All(tool => tool.InputSchema["type"]?.GetValue<string>() == "object"), "Every builtin tool has an object input schema: " + tools.Count);
        var create = tools.Single(tool => tool.Name == "spec_create").InputSchema;
        Require(create["properties"]?["type"]?["enum"]?.ToJsonString() == """["goal-and-requirements","architecture-design","module-design","submodule-design","task-spec"]""" &&
            create["properties"]?["path"]?["description"] is not null,
            "A tool schema carries enum member names and [Description] text: " + create.ToJsonString());

        // The bytes persisted files and the wire carry: camelCase names and enum values, nulls omitted, numbers refused for enums.
        var settings = PluginJson.Serialize(new SharpRail.Plugins.ClaudeCode.ClaudeCodeSettings());
        Require(settings.Contains("\"mode\":\"terminal\"", StringComparison.Ordinal), "Settings enums are camelCase strings: " + settings);
        var status = PluginJson.Serialize(new SharpRail.Plugins.Discord.DiscordStatus(SharpRail.Plugins.Discord.DiscordConnectionState.Unconfigured));
        Require(status == """{"state":"unconfigured"}""", "Null members are omitted: " + status);
        var refused = false;
        try { PluginJson.Deserialize<SharpRail.Plugins.Discord.DiscordStatus>("""{"state":0}"""); }
        catch (JsonException) { refused = true; }
        Require(refused, "An enum given as a number is refused.");
        Console.WriteLine("PASS plugin JSON: every builtin plugin's declared types, settings and tools have source-generated metadata");
    }

    // The types a context was generated for: the type arguments of its JsonTypeInfo<T> properties.
    private static IEnumerable<Type> Roots(IJsonTypeInfoResolver? context) =>
        context is null ? [] : context.GetType().GetProperties()
            .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
            .Select(property => property.PropertyType.GetGenericArguments()[0]);

    // Resolves a type and everything its metadata reaches: properties, elements, dictionary keys and derived types.
    private static void Walk(string plugin, Type type, List<string> missing, HashSet<Type> seen)
    {
        if (!seen.Add(type)) return;
        JsonTypeInfo info;
        try { info = PluginJson.TypeInfo(type); }
        catch (NotSupportedException error)
        {
            missing.Add($"{plugin}: {type.Name}: {error.Message}");
            return;
        }
        if (LostDefault(info) is { } lost) missing.Add($"{plugin}: {type.Name}.{lost}: an init-only property with an initializer loses it when the member is absent; make it settable");
        foreach (var property in info.Properties) Walk(plugin, property.PropertyType, missing, seen);
        if (info.ElementType is { } element) Walk(plugin, element, missing, seen);
        if (info.KeyType is { } key) Walk(plugin, key, missing, seen);
        if (info.PolymorphismOptions is { } polymorphism)
            foreach (var derived in polymorphism.DerivedTypes) Walk(plugin, derived.DerivedType, missing, seen);
        if (Nullable.GetUnderlyingType(type) is { } underlying) Walk(plugin, underlying, missing, seen);
    }

    // Source generation assigns every init-only property from the JSON, its type's default when the member is
    // absent, so an initializer on one never applies; a settable property keeps it.
    private static string? LostDefault(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || info.Type.IsAbstract) return null;
        var constructor = info.Type.GetConstructors().OrderBy(candidate => candidate.GetParameters().Length).FirstOrDefault();
        if (constructor is null) return null;
        var instance = constructor.Invoke([.. constructor.GetParameters().Select(parameter => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null)]);
        foreach (var property in info.Properties)
        {
            if (property.AssociatedParameter is not { IsMemberInitializer: true } || property.Get is not { } get) continue;
            var value = get(instance);
            var none = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
            if (!Equals(value, none)) return property.Name;
        }
        return null;
    }
}