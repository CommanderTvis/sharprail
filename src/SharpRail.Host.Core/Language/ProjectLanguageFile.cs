using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Tomlyn;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// The project's portable language-server choices, <c>.sharprail/language.toml</c> under the project root
/// (<see cref="Version"/> 1). Only the portable options are read from it; host-specific ones stay in host state.
/// Reading never throws: a malformed file or entry becomes a diagnostic and the affected entries are ignored.
/// </summary>
internal static class ProjectLanguageFile
{
    internal const int Version = 1;
    internal const int MaximumBytes = ProjectToml.MaximumBytes;
    internal static readonly string[] Portable = ["autoStart", "arguments", "buildTool", "compilationDatabasePath", "compilerFlags", "includePaths", "initializationOptions", "settings"];
    internal static readonly string[] Local = ["installationPath", "projectJdk", "useForJavaAndKotlin"];

    /// <summary>The validated portable keys of every usable server entry, plus what could not be used.</summary>
    internal sealed record Snapshot(string Path, bool Exists, string Stamp, IReadOnlyDictionary<string, JsonObject> Servers, IReadOnlyList<string> Diagnostics)
    {
        internal static Snapshot Empty(string path) => new(path, false, "", new Dictionary<string, JsonObject>(), []);

        /// <summary>The shared-state form: no option values, only what clients show and compare.</summary>
        internal string ToJson() => new JsonObject
        {
            ["path"] = Path,
            ["exists"] = Exists,
            ["stamp"] = Stamp,
            ["servers"] = new JsonArray(Servers.Keys.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["diagnostics"] = new JsonArray(Diagnostics.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray())
        }.ToJsonString();
    }

    internal static string PathOf(string root) => System.IO.Path.Combine(root, ".sharprail", "language.toml");

    internal static Snapshot Read(string root)
    {
        var path = PathOf(root);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Snapshot.Empty(path);
            var stamp = info.Length + "-" + info.LastWriteTimeUtc.Ticks;
            var newer = Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, "language_v*.toml").Select(System.IO.Path.GetFileName).Order().ToArray();
            var note = newer.Length > 0 ? $"{string.Join(", ", newer)} was written by a newer SharpRail; this version reads and writes only language.toml." : null;
            var snapshot = ReadCurrent(path, stamp);
            return note is null ? snapshot : snapshot with { Diagnostics = snapshot.Diagnostics.Append(note).ToArray() };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Failed(path, "unreadable", $"{path} could not be read: {error.Message}");
        }
    }

    private static Snapshot ReadCurrent(string path, string stamp)
    {
        var info = new FileInfo(path);
        {
            if (info.Length > MaximumBytes) return Failed(path, stamp, $"{path} is larger than {MaximumBytes / 1024} KiB; it is ignored.");
            return Parse(File.ReadAllText(path, Encoding.UTF8), path, stamp);
        }
    }

    private static Snapshot Failed(string path, string stamp, string message) => new(path, true, stamp, new Dictionary<string, JsonObject>(), [message]);

    internal static Snapshot Parse(string text, string path, string stamp)
    {
        JsonObject document;
        try { document = ProjectToml.Parse(text); }
        catch (TomlException error) { return Failed(path, stamp, $"{path} is not valid TOML and is ignored: {error.Message}"); }
        if (ProjectToml.VersionOf(document) is not { } versionNumber || versionNumber is < 0 or > int.MaxValue) return Failed(path, stamp, $"{path} needs version = {Version} and is ignored.");
        var number = (int)versionNumber;
        if (number != Version)
            return Failed(path, stamp, $"{path} uses version {number}; this SharpRail reads version {Version} and ignores the file.");
        var diagnostics = new List<string>();
        var servers = new Dictionary<string, JsonObject>();
        if (document["servers"] is { } entries and not JsonObject) diagnostics.Add("\"servers\" must be an object; it is ignored.");
        else if (document["servers"] is JsonObject map)
            foreach (var (id, entry) in map)
            {
                var problem = Validate(id, entry, out var portable);
                if (problem is not null) diagnostics.Add($"servers.{id}: {problem}");
                else servers[id] = portable!;
                if (entry is JsonObject supplied)
                    foreach (var key in Local.Where(supplied.ContainsKey))
                        diagnostics.Add($"servers.{id}.{key} is host-specific and ignored here; set it in Settings > Code Intelligence.");
            }
        return new(path, true, stamp, servers, diagnostics);
    }

    private static string? Validate(string id, JsonNode? entry, out JsonObject? portable)
    {
        portable = null;
        if (LanguageServers.Catalog.All(server => server.Id != id)) return "unknown language server; the entry is ignored.";
        if (entry is not JsonObject supplied) return "the entry must be an object.";
        var picked = new JsonObject();
        foreach (var key in Portable)
            if (supplied[key] is { } value) picked[key] = value.DeepClone();
        try
        {
            var options = WorkspaceLanguageConfiguration.Parse(picked.ToJsonString(), id);
            if (TypedProblem(id, options) is { } problem) return problem;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        {
            return error.Message + " The entry is ignored.";
        }
        portable = picked;
        return null;
    }

    /// <summary>Checks provider options against the server's typed option catalog; unlisted options pass through.</summary>
    private static string? TypedProblem(string id, WorkspaceLanguageOptions options)
    {
        var fields = JsonNode.Parse(LanguageConfigurationCatalog.Read(id).Configuration)?["fields"]?.AsArray();
        if (fields is null) return null;
        foreach (var field in fields)
        {
            var section = field?["section"]?.GetValue<string>();
            var source = section == "settings" ? options.Settings : section == "initializationOptions" ? options.InitializationOptions : null;
            var fieldPath = field?["path"]?.GetValue<string>();
            if (source is not { ValueKind: JsonValueKind.Object } root || fieldPath is null) continue;
            if (!Find(root, fieldPath.Split('.'), 0, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            var label = field!["label"]?.GetValue<string>() ?? fieldPath;
            var type = field["type"]?.GetValue<string>() ?? "string";
            var ok = type switch
            {
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "lines" => value.ValueKind == JsonValueKind.Array,
                "features" => value.ValueKind is JsonValueKind.String or JsonValueKind.Array,
                _ => value.ValueKind == JsonValueKind.String
            };
            if (!ok) return $"{label} ({fieldPath}) must be of type {type}.";
            if (type == "integer" && field["minimum"] is { } minimum && value.GetInt64() < minimum.GetValue<decimal>())
                return $"{label} ({fieldPath}) must be at least {minimum}.";
            if (type == "string" && field["choices"] is JsonArray choices && field["allowCustomChoices"]?.GetValue<bool>() != true &&
                !choices.Any(choice => choice?.GetValue<string>() == value.GetString()))
                return $"{label} ({fieldPath}) must be one of {string.Join(", ", choices)}.";
        }
        return null;
    }

    // Catalog paths are dotted; a key may be nested objects or a literal key containing dots.
    private static bool Find(JsonElement node, string[] parts, int from, out JsonElement value)
    {
        for (var end = from; end < parts.Length; end++)
            if (node.TryGetProperty(string.Join('.', parts[from..(end + 1)]), out var next))
            {
                if (end == parts.Length - 1) { value = next; return true; }
                if (next.ValueKind == JsonValueKind.Object && Find(next, parts, end + 1, out value)) return true;
            }
        value = default;
        return false;
    }

    /// <summary>
    /// The effective options of one server: every key stored locally wins, every other portable key comes from the
    /// file, host-specific options come from host state only. A legacy local entry holds every key and so keeps
    /// winning entirely, which is what makes existing host-state configuration keep working unchanged.
    /// </summary>
    internal static string Merge(string local, JsonObject portable)
    {
        var stored = JsonNode.Parse(string.IsNullOrWhiteSpace(local) ? "{}" : local) as JsonObject ?? new JsonObject();
        var result = new JsonObject();
        foreach (var key in Local.Concat(Portable))
        {
            if (stored[key] is { } own) result[key] = own.DeepClone();
            else if (Portable.Contains(key) && portable[key] is { } shared) result[key] = shared.DeepClone();
        }
        return result.ToJsonString();
    }

    /// <summary>The keys of <paramref name="full"/> worth storing locally: those the file does not already provide, or that were already local.</summary>
    internal static string Localize(string previousLocal, string full, JsonObject portable)
    {
        var before = JsonNode.Parse(string.IsNullOrWhiteSpace(previousLocal) ? "{}" : previousLocal) as JsonObject ?? new JsonObject();
        var wanted = JsonNode.Parse(full)!.AsObject();
        var defaults = JsonNode.Parse(WorkspaceLanguageConfiguration.Serialize(new()))!.AsObject();
        var result = new JsonObject();
        foreach (var key in Local.Concat(Portable))
        {
            if (wanted[key] is not { } value) continue;
            var inherited = Portable.Contains(key) && portable[key] is { } shared ? shared : defaults[key];
            if (before.ContainsKey(key) || !JsonNode.DeepEquals(value, inherited)) result[key] = value.DeepClone();
        }
        return result.ToJsonString();
    }

    /// <summary>The portable keys of <paramref name="options"/> that differ from the defaults.</summary>
    internal static JsonObject PortableOf(WorkspaceLanguageOptions options)
    {
        var full = JsonNode.Parse(WorkspaceLanguageConfiguration.Serialize(options))!.AsObject();
        var defaults = JsonNode.Parse(WorkspaceLanguageConfiguration.Serialize(new()))!.AsObject();
        var result = new JsonObject();
        foreach (var key in Portable)
            if (full[key] is { } value && !JsonNode.DeepEquals(value, defaults[key])) result[key] = value.DeepClone();
        return result;
    }

    /// <summary>
    /// Writes one server's portable options by editing the file's syntax tree: comments, layout, ordering and keys this
    /// version does not know survive, only the managed options of that server are replaced and a new server is appended.
    /// The result is re-read and verified before the atomic replace; a file that cannot be parsed is never overwritten.
    /// </summary>
    internal static void Save(string root, string server, JsonObject portable)
    {
        var path = PathOf(root);
        var text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : $"version = {Version}\n";
        if (!text.EndsWith('\n')) text += "\n";
        JsonObject before;
        try { before = ProjectToml.Parse(text); }
        catch (TomlException error) { throw new InvalidOperationException($"{path} is not valid TOML; fix or remove it before saving here: {error.Message}"); }
        if (ProjectToml.VersionOf(before) != Version)
            throw new InvalidOperationException($"{path} needs version = {Version}; this SharpRail writes version {Version}.");
        var document = SyntaxParser.Parse(text);
        if (document.HasErrors) throw new InvalidOperationException($"{path} is not valid TOML; fix or remove it before saving here.");

        string[] entry = ["servers", server];
        foreach (var key in Portable)
        {
            string[] unit = [.. entry, key];
            foreach (var item in document.KeyValues.Where(item => ProjectToml.Under(ProjectToml.Parts(item.Key!), unit)).ToArray()) document.KeyValues.RemoveChild(item);
            foreach (var table in document.Tables.ToArray())
            {
                var name = ProjectToml.Parts(table.Name!);
                if (ProjectToml.Under(name, unit)) { document.Tables.RemoveChild(table); continue; }
                foreach (var item in table.Items.Where(item => ProjectToml.Under([.. name, .. ProjectToml.Parts(item.Key!)], unit)).ToArray()) table.Items.RemoveChild(item);
            }
        }

        var snippet = new StringBuilder();
        snippet.Append('[').Append(string.Join('.', entry.Select(ProjectToml.KeyText))).Append("]\n");
        foreach (var key in Portable)
            if (portable[key] is { } value and not JsonObject) snippet.Append(key).Append(" = ").Append(ProjectToml.Inline(value)).Append('\n');
        foreach (var key in Portable)
            if (portable[key] is JsonObject table) ProjectToml.Sections(snippet, [.. entry, key], table);
        var addition = SyntaxParser.Parse(snippet.ToString());
        if (addition.HasErrors) throw new InvalidOperationException("The options could not be written as TOML.");
        var additions = addition.Tables.ToArray();
        foreach (var table in additions) addition.Tables.RemoveChild(table);
        var existing = document.Tables.FirstOrDefault(table => table is TableSyntax && ProjectToml.Parts(table.Name!).SequenceEqual(entry));
        for (var i = 0; i < additions.Length; i++)
        {
            if (i == 0 && existing is not null)
                foreach (var item in additions[0].Items.ToArray()) { additions[0].Items.RemoveChild(item); existing.Items.Add(item); }
            else document.Tables.Add(additions[i]);
        }

        var result = document.ToString();
        JsonObject after;
        try { after = ProjectToml.Parse(result); }
        catch (TomlException error) { throw new InvalidOperationException($"{path} has a layout this editor cannot extend; edit it by hand: {error.Message}"); }
        var expected = before.DeepClone().AsObject();
        if (expected["servers"] is not JsonObject servers) expected["servers"] = servers = [];
        if (servers[server] is not JsonObject target) servers[server] = target = [];
        foreach (var key in Portable) target.Remove(key);
        foreach (var (key, value) in portable) target[key] = value?.DeepClone();
        if (!JsonNode.DeepEquals(expected, after))
            throw new InvalidOperationException($"{path} has a layout this editor cannot extend without changing other settings; edit it by hand.");
        if (Encoding.UTF8.GetByteCount(result) > MaximumBytes) throw new InvalidOperationException($"The configuration would exceed {MaximumBytes / 1024} KiB.");
        ProjectToml.WriteAtomic(path, result);
    }
}