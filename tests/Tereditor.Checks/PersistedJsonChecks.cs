using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.UI.State;

namespace Tereditor.Checks;

/// <summary>
/// The files the host and the app persist keep their bytes: a representative <c>state.json</c> (with the language
/// metadata it writes beside it) and profiles, legacy and current, are loaded and written again, and every written file
/// must equal its golden copy in <c>Fixtures/persisted-json</c>. The goldens were captured from the reflection-based
/// serializer; <c>--persisted-json --update</c> rewrites them after a deliberate format change (review the diff).
/// </summary>
internal static class PersistedJsonChecks
{
    private const string Fixtures = "Fixtures/persisted-json";
    private const string Root = "$ROOT";

    internal static async Task Run(string root, bool update)
    {
        var lost = new List<string>();
        foreach (var context in new System.Text.Json.Serialization.JsonSerializerContext[] { HostJson.Default, Tereditor.UI.AppJson.Default, Tereditor.Plugins.UI.Kit.KitJson.Default, Tereditor.Host.Protocol.LocalBrowserApprovalJson.Default })
        {
            var seen = new HashSet<Type>();
            foreach (var generated in context.GetType().GetProperties().Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>)))
                Walk(context, generated.PropertyType.GetGenericArguments()[0], seen, lost);
        }
        Require(lost.Count == 0, "Every type the host and app contexts reach keeps its initializers:\n" + string.Join('\n', lost));

        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await State(Path.Combine(root, "persisted-state"), outputs);
        LegacyProfile(Path.Combine(root, "persisted-legacy-profile"), outputs);
        CurrentProfile(Path.Combine(root, "persisted-current-profile"), outputs);

        var failures = new List<string>();
        var source = update ? Path.Combine(BoundaryChecks.Checkout() ?? throw new InvalidOperationException("--update needs a source checkout."), "tests/Tereditor.Checks", Fixtures, "golden") : null;
        foreach (var (name, text) in outputs)
        {
            if (source is not null) { Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, name), text); continue; }
            var golden = Path.Combine(AppContext.BaseDirectory, Fixtures, "golden", name);
            if (!File.Exists(golden)) { failures.Add($"{name}: no golden copy; run --persisted-json --update and review the fixture diff"); continue; }
            var expected = Stable(File.ReadAllText(golden));
            var written = Stable(text);
            if (expected != written) failures.Add($"{name}: written bytes differ from the golden copy at offset {FirstDifference(expected, written)}\n--- expected\n{expected}\n--- written\n{written}");
        }
        if (failures.Count > 0) throw new InvalidOperationException("Persisted JSON checks failed:\n" + string.Join("\n", failures));
        Console.WriteLine(source is not null ? $"Wrote {outputs.Count} golden files to {source}" : $"PASS persisted json: {outputs.Count} written files match their golden copies byte for byte");
    }

    private static async Task State(string directory, IDictionary<string, string> outputs)
    {
        var root = Prepare(directory);
        File.WriteAllText(Path.Combine(directory, HostStateStore.FileName), Input("state.input.json", root));
        var store = new HostStateStore(directory);
        Require(store.LastError is null, "The representative state.json must load: " + store.LastError);
        await store.ChangeAsync([HostStateChange.Label(Path.Combine(root, "project"), "Labelled ✳ <again>")]);
        Require(store.LastError is null, "The representative state.json must save: " + store.LastError);
        outputs["state.json"] = Output(File.ReadAllText(Path.Combine(directory, HostStateStore.FileName)), root);
        var metadata = Directory.EnumerateFiles(Path.Combine(directory, "language", "workspaces"), "*.json", SearchOption.AllDirectories).Single();
        outputs["state.language-metadata.json"] = Output(File.ReadAllText(metadata), root);
    }

    private static void LegacyProfile(string directory, IDictionary<string, string> outputs)
    {
        var root = Prepare(directory);
        var written = new List<string>();
        var profile = new ProfileStore(directory, () => Input("profile-legacy.input.json", root), written.Add);
        Require(profile.LastError is null, "The legacy profile must load: " + profile.LastError);
        profile.Save();
        outputs["profile-legacy.json"] = Output(written.Last(), root);
        var state = profile.OpenState();
        Require(state.LastError is null && profile.Data.StateMigrated, "The legacy profile must migrate into state.json: " + state.LastError);
        outputs["profile-legacy.migrated.json"] = Output(written.Last(), root);
        outputs["profile-legacy.state.json"] = Output(File.ReadAllText(Path.Combine(directory, HostStateStore.FileName)), root);
    }

    private static void CurrentProfile(string directory, IDictionary<string, string> outputs)
    {
        var root = Prepare(directory);
        var written = new List<string>();
        var profile = new ProfileStore(directory, () => Input("profile-current.input.json", root), written.Add);
        Require(profile.LastError is null, "The current profile must load: " + profile.LastError);
        profile.Save();
        outputs["profile-current.json"] = Output(written.Last(), root);
    }

    // Generated deserialization assigns an init-only property its type's default when the member is absent, so a type
    // whose init-only property has an initializer would read older files differently from the reflection serializer.
    private static void Walk(System.Text.Json.Serialization.JsonSerializerContext context, Type type, HashSet<Type> seen, List<string> lost)
    {
        if (!seen.Add(type) || context.GetTypeInfo(type) is not { } info) return;
        if (PluginJsonChecks.LostDefault(info) is { } property) lost.Add($"{context.GetType().Name}: {type.FullName}.{property}");
        foreach (var member in info.Properties) Walk(context, member.PropertyType, seen, lost);
        if (info.ElementType is { } element) Walk(context, element, seen, lost);
        if (Nullable.GetUnderlyingType(type) is { } underlying) Walk(context, underlying, seen, lost);
    }

    // The fixture's paths live under a placeholder root whose folders exist, since the stores drop records of folders that are gone.
    private static string Prepare(string directory)
    {
        Directory.CreateDirectory(directory);
        var root = Path.Combine(directory, "fs");
        foreach (var folder in new[] { "project", "project-ws", "older", "plugins" }) Directory.CreateDirectory(Path.Combine(root, folder));
        return root;
    }

    private static string Input(string name, string root) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, Fixtures, name)).Replace(Root, root, StringComparison.Ordinal);

    private static string Output(string text, string root) => text.Replace(root, Root, StringComparison.Ordinal).ReplaceLineEndings("\n");

    // Identities the stores mint (a project record for a migrated path, a fresh layout's group ids) are random and
    // time-stamped, so they are compared as placeholders.
    private static string Stable(string text)
    {
        text = Regex.Replace(text, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "$$UUID");
        text = Regex.Replace(text, "(\"Id\": \"\\$UUID\",\\s+\"Path\": \"[^\"]*\",\\s+\"Slug\": \"[^\"]*\",\\s+\"LastOpened\": )\\d+", "${1}0");
        return Regex.Replace(text, "(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])", "$$HEX");
    }

    private static int FirstDifference(string expected, string written)
    {
        var index = 0;
        while (index < expected.Length && index < written.Length && expected[index] == written[index]) index++;
        return index;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}