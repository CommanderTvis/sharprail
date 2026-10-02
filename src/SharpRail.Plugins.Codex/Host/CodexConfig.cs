using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Tomlyn;
using Tomlyn.Model;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// Codex's layered config.toml (project when trusted, user, system), its AGENTS.md chain, the status hooks in
/// hooks.json, and the in-place edit of one key. See this package's SPEC.md.
/// </summary>
public static partial class CodexConfig
{
    public static readonly IReadOnlyList<string> HookEvents = ["SessionStart", "UserPromptSubmit", "PostToolUse", "PermissionRequest", "Stop", "Interrupt"];

    public const string StatusUrlEnv = "THINKRAIL_CODEX_STATUS_URL";

    // The same command ThinkRail installs, so one hooks.json entry (and the user's one trust review of it) serves both.
    public const string HookCommand = "[ -z \"$" + StatusUrlEnv + "\" ] || curl -fsS -m 2 -H 'content-type: application/json' --data-binary @- \"$" + StatusUrlEnv + "\" >/dev/null 2>&1; true";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Home() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string LayerPath(CodexScope scope, string worktreePath) => scope switch
    {
        CodexScope.Project => Path.Combine(worktreePath, ".codex", "config.toml"),
        CodexScope.User => Path.Combine(Home(), "config.toml"),
        _ => "/etc/codex/config.toml"
    };

    private sealed record TomlRead(bool Exists, JsonObject Table, string? Error = null);

    private static TomlRead ReadToml(string path)
    {
        if (!File.Exists(path)) return new(false, []);
        try { return new(true, Parse(File.ReadAllText(path))); }
        catch (Exception error) when (error is TomlException or IOException or UnauthorizedAccessException) { return new(true, [], error.Message); }
    }

    /// <summary>A TOML document as JSON: tables become objects, dates their text.</summary>
    public static JsonObject Parse(string text) => (JsonObject)ToNode(TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable())!;

    private static JsonNode? ToNode(object? value) => value switch
    {
        TomlTable table => new JsonObject(table.Select(pair => KeyValuePair.Create(pair.Key, ToNode(pair.Value)))),
        TomlTableArray tables => new JsonArray([.. tables.Select(ToNode)]),
        TomlArray array => new JsonArray([.. array.Select(ToNode)]),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long number => JsonValue.Create(number),
        double number when double.IsFinite(number) => JsonValue.Create(number),
        null => null,
        _ => JsonValue.Create(value.ToString())
    };

    private static bool IsTrusted(JsonObject user, string worktreePath) =>
        user["projects"] is JsonObject projects && projects.Any(pair =>
            pair.Value is JsonObject entry && entry["trust_level"]?.GetValueKind() == JsonValueKind.String && (string?)entry["trust_level"] == "trusted" &&
            (worktreePath == pair.Key || worktreePath.StartsWith(pair.Key.EndsWith(Path.DirectorySeparatorChar) ? pair.Key : pair.Key + Path.DirectorySeparatorChar, StringComparison.Ordinal)));

    private static string? FirstNonEmpty(string directory, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            try { if (new FileInfo(path) is { Exists: true, Length: > 0 }) return path; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    // Codex's AGENTS.md discovery: the global file, then one per directory from the nearest repository root down to the
    // CWD; outside a repository only the CWD counts.
    private static List<CodexInstructions> InstructionsOf(string worktreePath, string cwd, IReadOnlyList<string> fallbacks)
    {
        var found = new List<CodexInstructions>();
        if (FirstNonEmpty(Home(), ["AGENTS.override.md", "AGENTS.md"]) is { } global)
            found.Add(new(CodexInstructionsScope.Global, global, new FileInfo(global).Length));
        var directories = new List<string> { Path.GetFullPath(cwd) };
        for (var directory = directories[0]; !Path.Exists(Path.Combine(directory, ".git"));)
        {
            if (Path.GetDirectoryName(directory) is not { } parent) { directories.RemoveRange(1, directories.Count - 1); break; }
            directories.Add(parent);
            directory = parent;
        }
        directories.Reverse();
        foreach (var directory in directories)
        {
            if (FirstNonEmpty(directory, ["AGENTS.override.md", "AGENTS.md", .. fallbacks]) is not { } project) continue;
            var relative = Path.GetRelativePath(worktreePath, project);
            var inside = relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
            found.Add(new(CodexInstructionsScope.Project, project, new FileInfo(project).Length) { RelativePath = inside ? relative : null });
        }
        return found;
    }

    private static string HooksPath() => Path.Combine(Home(), "hooks.json");

    private static JsonObject ReadHooksFile()
    {
        try { return JsonNode.Parse(File.ReadAllText(HooksPath())) as JsonObject ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static bool HasOurHook(JsonNode? groups) =>
        groups is JsonArray array && array.Any(group => group is JsonObject { } entry && entry["hooks"] is JsonArray hooks &&
            hooks.Any(hook => hook is JsonObject command && command["command"]?.GetValueKind() == JsonValueKind.String && (string?)command["command"] == HookCommand));

    public static bool HooksInstalled() => ReadHooksFile()["hooks"] is JsonObject hooks && HookEvents.All(name => HasOurHook(hooks[name]));

    /// <summary>Whether Codex's own trust record names hooks.json; SharpRail never writes it, that review is the user's.</summary>
    public static bool HooksTrusted()
    {
        var state = ReadToml(LayerPath(CodexScope.User, "")).Table["hooks"] is JsonObject hooks && hooks["state"] is JsonObject trusted ? trusted : [];
        return state.Any(pair => pair.Key.StartsWith(HooksPath() + ":", StringComparison.Ordinal));
    }

    private const string InstructionsStarter = "# Instructions for Codex\n\n";

    /// <summary>Creates the target's instructions with a one-line starter (Codex skips an empty file); never overwrites.</summary>
    public static CodexCreatedInstructions CreateInstructions(string worktreePath, CodexInstructionsTarget target)
    {
        var path = target == CodexInstructionsTarget.Global
            ? Path.Combine(Home(), "AGENTS.md")
            : Path.Combine(worktreePath, target == CodexInstructionsTarget.Project ? "AGENTS.md" : "AGENTS.override.md");
        if (new FileInfo(path) is not { Exists: true, Length: > 0 }) WriteAtomic(path, InstructionsStarter);
        return target == CodexInstructionsTarget.Global ? new(path) : new(path) { RelativePath = Path.GetRelativePath(worktreePath, path) };
    }

    /// <summary>Appends this plugin's command to each event in hooks.json, keeping everything already there.</summary>
    public static void InstallHooks()
    {
        var file = ReadHooksFile();
        if (file["hooks"] is not JsonObject hooks) file["hooks"] = hooks = [];
        foreach (var name in HookEvents)
        {
            if (hooks[name] is not JsonArray groups) hooks[name] = groups = [];
            if (!HasOurHook(groups)) groups.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = HookCommand }) });
        }
        WriteAtomic(HooksPath(), file.ToJsonString(new JsonSerializerOptions(Json) { WriteIndented = true, IndentSize = 2 }) + "\n");
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.sharprail-{Environment.ProcessId}";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private static readonly string[][] HiddenTables = [["hooks", "state"], ["projects"]];

    private static IEnumerable<(string[] KeyPath, JsonNode? Value)> Flatten(JsonNode? value, string[] keyPath)
    {
        if (HiddenTables.Any(hidden => hidden.SequenceEqual(keyPath))) yield break;
        if (value is not JsonObject table)
        {
            yield return (keyPath, value);
            yield break;
        }
        foreach (var (key, child) in table)
            foreach (var leaf in Flatten(child, [.. keyPath, key])) yield return leaf;
    }

    private static JsonElement Element(JsonNode? node) => JsonSerializer.SerializeToElement(node);

    /// <summary>
    /// The configuration a Codex session in <paramref name="worktreePath"/> sees, with instructions discovered from
    /// <paramref name="cwd"/> (the worktree by default) and <paramref name="promptFiles"/> listed as launch instructions.
    /// </summary>
    public static CodexConfigSnapshot Resolve(string worktreePath, string? cwd = null, IReadOnlyList<string>? promptFiles = null)
    {
        cwd ??= worktreePath;
        var system = ReadToml(LayerPath(CodexScope.System, worktreePath));
        var user = ReadToml(LayerPath(CodexScope.User, worktreePath));
        var project = ReadToml(LayerPath(CodexScope.Project, worktreePath));
        var projectTrusted = IsTrusted(user.Table, worktreePath);

        (CodexScope Scope, TomlRead Read, bool Ignored)[] ordered =
            [(CodexScope.Project, project, !projectTrusted), (CodexScope.User, user, false), (CodexScope.System, system, false)];
        var layers = ordered.Select(layer => new CodexLayer(layer.Scope, LayerPath(layer.Scope, worktreePath), layer.Read.Exists, layer.Read.Exists && layer.Ignored)
        { Error = layer.Read.Error }).ToArray();

        var settings = new Dictionary<string, (CodexSetting Setting, List<CodexShadowedValue> Shadowed)>();
        var mcpServers = new Dictionary<string, CodexMcpServer>();
        foreach (var (scope, read, _) in ordered.Where(layer => !layer.Ignored))
        {
            var path = LayerPath(scope, worktreePath);
            foreach (var (key, value) in read.Table)
            {
                if (key == "mcp_servers" && value is JsonObject servers)
                {
                    foreach (var (name, server) in servers)
                    {
                        if (mcpServers.ContainsKey(name) || server is not JsonObject entry) continue;
                        var target = entry["url"]?.GetValueKind() == JsonValueKind.String ? (string)entry["url"]! :
                            string.Join(" ", new[] { entry["command"]?.ToString() ?? "" }.Concat(entry["args"] is JsonArray args ? args.Select(arg => arg?.ToString() ?? "") : []));
                        mcpServers[name] = new(name, scope, path, target);
                    }
                    continue;
                }
                foreach (var (keyPath, leaf) in Flatten(value, [key]))
                {
                    var dotted = string.Join(".", keyPath.Select(FormatKeySegment));
                    if (settings.TryGetValue(dotted, out var existing)) existing.Shadowed.Add(new(Element(leaf), scope, path));
                    else
                    {
                        var shadowed = new List<CodexShadowedValue>();
                        settings[dotted] = (new(dotted, keyPath, Element(leaf), scope, path, shadowed), shadowed);
                    }
                }
            }
        }

        var fallbacks = settings.TryGetValue("project_doc_fallback_filenames", out var fallback) && fallback.Setting.Value.ValueKind == JsonValueKind.Array
            ? fallback.Setting.Value.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!).ToArray() : [];
        var launched = (promptFiles ?? []).Where(File.Exists).Select(path => new CodexInstructions(CodexInstructionsScope.Launch, path, new FileInfo(path).Length));
        return new(cwd, Home(), layers,
            [.. settings.Values.Select(entry => entry.Setting).OrderBy(setting => setting.Key, StringComparer.Ordinal)],
            [.. mcpServers.Values], [.. InstructionsOf(worktreePath, cwd, fallbacks), .. launched], projectTrusted, HooksInstalled(), HooksTrusted());
    }

    public static string FormatKeySegment(string segment) => BareKey().IsMatch(segment) ? segment : JsonSerializer.Serialize(segment, Json);

    private static List<string>? ParseKeySegments(string text)
    {
        var segments = new List<string>();
        var at = 0;
        while (at < text.Length)
        {
            var match = KeySegment().Match(text, at);
            if (!match.Success || match.Index != at) return null;
            segments.Add(match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? JsonSerializer.Deserialize<string>("\"" + match.Groups[2].Value + "\"")!
                : match.Groups[3].Value);
            at = match.Index + match.Length;
            if (match.Groups[4].Value.Length == 0) break;
        }
        return segments;
    }

    // A [table] header's key path, "array" for an [[array of tables]], or null for any other line.
    private static object? HeaderOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("[[", StringComparison.Ordinal)) return "array";
        var match = TableHeader().Match(trimmed);
        return match.Success ? ParseKeySegments(match.Groups[1].Value) : null;
    }

    private static List<string>? LeafKeyOf(string line)
    {
        var cut = line.IndexOf('=');
        if (cut == -1 || CommentOrHeader().IsMatch(line)) return null;
        return ParseKeySegments(line[..cut]);
    }

    private static void WithValue(JsonObject table, IReadOnlyList<string> keyPath, JsonNode? value, bool remove)
    {
        var head = keyPath[0];
        if (keyPath.Count == 1)
        {
            if (remove) table.Remove(head);
            else table[head] = value;
            return;
        }
        if (table[head] is not JsonObject child) table[head] = child = [];
        WithValue(child, keyPath.Skip(1).ToArray(), value, remove);
    }

    /// <summary>
    /// Rewrites one <c>key = value</c> line inside the key's <c>[table]</c> section (inserting at the section's end, or
    /// appending the section), so comments and Codex's own bookkeeping survive byte for byte. The result is parsed
    /// again and refused unless exactly that key changed. A null <paramref name="value"/> removes the key.
    /// </summary>
    public static string SetKeyPath(string text, IReadOnlyList<string> keyPath, JsonElement? value)
    {
        var table = keyPath.Take(keyPath.Count - 1).ToArray();
        var leaf = keyPath[^1];
        var lines = text.Split('\n').ToList();

        var start = table.Length == 0 ? 0 : -1;
        var end = lines.Count;
        for (var index = 0; index < lines.Count; index++)
        {
            var header = HeaderOf(lines[index]);
            if (header is null) continue;
            if (start != -1 && index >= start)
            {
                end = index;
                break;
            }
            if (header is List<string> path && path.SequenceEqual(table)) start = index + 1;
        }

        var line = value is { } given ? $"{FormatKeySegment(leaf)} = {JsonSerializer.Serialize(given, Json)}" : null;
        string next;
        if (start == -1)
        {
            if (line is null) return text;
            var body = text.TrimEnd('\n');
            next = $"{body}{(body.Length > 0 ? "\n\n" : "")}[{string.Join(".", table.Select(FormatKeySegment))}]\n{line}\n";
        }
        else
        {
            var at = lines.FindIndex(start, end - start, candidate => LeafKeyOf(candidate) is { } key && key.SequenceEqual([leaf]));
            if (at != -1)
            {
                if (line is null) lines.RemoveAt(at);
                else lines[at] = line;
            }
            else if (line is not null)
            {
                var insertAt = end;
                while (insertAt > start && lines[insertAt - 1].Trim().Length == 0) insertAt--;
                lines.Insert(insertAt, line);
            }
            next = string.Join("\n", lines);
        }

        var expected = Parse(text);
        WithValue(expected, keyPath, value is { } inserted ? JsonNode.Parse(inserted.GetRawText()) : null, value is null);
        JsonObject? after = null;
        try { after = Parse(next); }
        catch (TomlException) { }
        if (after is null || !SameValue(after, expected))
            throw new InvalidOperationException($"Couldn't edit {string.Join(".", keyPath)} in place — edit the file by hand");
        return next;
    }

    // Deep equality where numbers compare by value, since TOML integers and JSON numbers spell 2 and 2.0 alike.
    private static bool SameValue(JsonNode? left, JsonNode? right) => (left, right) switch
    {
        (null, null) => true,
        (JsonObject a, JsonObject b) => a.Count == b.Count && a.All(pair => b.TryGetPropertyValue(pair.Key, out var other) && SameValue(pair.Value, other)),
        (JsonArray a, JsonArray b) => a.Count == b.Count && a.Zip(b).All(pair => SameValue(pair.First, pair.Second)),
        (JsonValue a, JsonValue b) when a.GetValueKind() == JsonValueKind.Number && b.GetValueKind() == JsonValueKind.Number =>
            double.Parse(a.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) == double.Parse(b.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture),
        (JsonValue a, JsonValue b) => a.ToJsonString() == b.ToJsonString(),
        _ => false
    };

    /// <summary>Writes one key into the user or project file; an enum key refuses any value the reference does not list.</summary>
    public static void WriteValue(string worktreePath, CodexWritableScope scope, IReadOnlyList<string> keyPath, JsonElement? value)
    {
        if (keyPath.Count == 0 || keyPath.Any(segment => segment.Length == 0)) throw new ArgumentException("A key path needs at least one non-empty segment.");
        if (value is { } given && !IsCodexValue(given)) throw new ArgumentException("A value is a string, number, boolean or list of strings.");
        var key = string.Join(".", keyPath);
        if (CodexConfigDocs.EnumValues(key) is { } allowed && value is { } chosen && (chosen.ValueKind != JsonValueKind.String || !allowed.Contains(chosen.GetString()!)))
            throw new ArgumentException($"{key} must be one of {string.Join(", ", allowed)}");
        var path = LayerPath(scope == CodexWritableScope.Project ? CodexScope.Project : CodexScope.User, worktreePath);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        WriteAtomic(path, SetKeyPath(text, keyPath, value));
    }

    private static bool IsCodexValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
        JsonValueKind.Array => value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
        _ => false
    };

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex BareKey();

    [GeneratedRegex(@"\G\s*(?:([A-Za-z0-9_-]+)|""((?:[^""\\]|\\.)*)""|'([^']*)')\s*(\.|\z)")]
    private static partial Regex KeySegment();

    [GeneratedRegex(@"^\[(.*)\]\s*(#.*)?$")]
    private static partial Regex TableHeader();

    [GeneratedRegex(@"^\s*(#|\[)")]
    private static partial Regex CommentOrHeader();
}