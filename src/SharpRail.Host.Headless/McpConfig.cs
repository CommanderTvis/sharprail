using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using SharpRail.Host.Core;

using Tomlyn;

namespace SharpRail.Host.Headless;

/// <summary>A configuration problem. The message names the file, line and key and is printed on stderr before the process exits with 2.</summary>
internal sealed class McpConfigException(string message) : Exception(message);

/// <summary>The tools the headless roster offers and the operations the narrowable ones accept. A headless check keeps these in step with the live tool table.</summary>
internal static class KnownTools
{
    internal static readonly string[] Names =
        ["lsp_diagnostics", "lsp_navigate", "lsp_status", "lsp_code_actions", "lsp_rename", "lsp_format", "spec_grep", "spec_get", "spec_graph", "spec_create", "spec_update", "spec_delete", "spec_validate", "blueprint_check"];

    internal static readonly IReadOnlyDictionary<string, string[]> Operations = new Dictionary<string, string[]>
    {
        ["lsp_navigate"] = ["hover", "definition", "typeDefinition", "implementation", "references", "symbols", "symbolBody", "workspaceSymbols", "source"],
        ["lsp_code_actions"] = ["list", "preview", "apply"],
        ["lsp_rename"] = ["preview", "apply"],
        ["lsp_format"] = ["preview", "apply"],
        ["lsp_status"] = ["status", "servers", "catalog", "configurationSchema", "configuration", "configure", "retry", "restart", "stop", "logs", "projects"]
    };
}

internal sealed record LogSettings(bool Enabled, string Level, int MaxResultChars, int MaxFileKb, string Directory);

/// <summary>What the project's <c>.sharprail/mcp.toml</c>, the selected arm, the environment and the flags add up to.</summary>
internal sealed record McpConfig(string? Arm, string? File, IReadOnlyList<string> Disable, IReadOnlyList<string>? Allow,
    IReadOnlyDictionary<string, ToolRule> Rules, LogSettings Log, IReadOnlyList<string> Warnings)
{
    internal const string FileName = ".sharprail/mcp.toml";

    /// <summary>Whether the tool exists for this run: allowed, not disabled and not switched off by its own table.</summary>
    internal bool Offers(string tool) =>
        (Allow is null || Allow.Contains(tool)) && !Disable.Contains(tool) && !(Rules.TryGetValue(tool, out var rule) && rule.Enabled == false);

    internal string[] Offered(IEnumerable<string> tools) => [.. tools.Where(Offers)];

    internal JsonObject ToJson(IEnumerable<string>? roster = null) => new()
    {
        ["version"] = 1,
        ["file"] = File,
        ["arm"] = Arm,
        ["tools"] = new JsonObject
        {
            ["disable"] = Strings(Disable),
            ["allow"] = Allow is null ? null : Strings(Allow),
            ["rules"] = new JsonObject(Rules.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)new JsonObject
            {
                ["enabled"] = pair.Value.Enabled,
                ["operations"] = pair.Value.Operations is null ? null : Strings(pair.Value.Operations)
            })))
        },
        ["toolsOffered"] = Strings(Offered(roster ?? KnownTools.Names)),
        ["log"] = new JsonObject
        {
            ["enabled"] = Log.Enabled,
            ["level"] = Log.Level,
            ["maxResultChars"] = Log.MaxResultChars,
            ["maxFileKb"] = Log.MaxFileKb,
            ["dir"] = Log.Directory
        },
        ["warnings"] = Strings(Warnings)
    };

    /// <summary>SHA-256 of the effective configuration, so two runs can be told apart or matched.</summary>
    internal string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson().ToJsonString())));

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]);

    /// <summary>Reads the project file and layers the arm, environment and flags over it. Throws <see cref="McpConfigException"/>.</summary>
    internal static McpConfig Load(string workspace, McpFlags flags, Func<string, string?> environment)
    {
        var path = Path.Combine(workspace, ".sharprail", "mcp.toml");
        Layer fromFile = new();
        Dictionary<string, Layer> arms = new(StringComparer.Ordinal);
        var found = System.IO.File.Exists(path);
        if (found) Read(path, fromFile, arms);

        var arm = flags.Arm ?? Env(environment, "SHARPRAIL_ARM");
        Layer? selected = null;
        if (arm is not null && !arms.TryGetValue(arm, out selected))
            throw new McpConfigException($"arm \"{arm}\" is not defined in {FileName} ({(found ? arms.Count == 0 ? "the file has no [arms.*] tables" : "defined: " + string.Join(", ", arms.Keys.Order(StringComparer.Ordinal)) : "the file does not exist")}).");

        var outside = new Layer();
        if (Env(environment, "SHARPRAIL_TOOLS_DISABLE") is { } disable) outside.Disable = Names(disable, "SHARPRAIL_TOOLS_DISABLE");
        if (Env(environment, "SHARPRAIL_TOOLS_ALLOW") is { } allow) outside.Allow = Names(allow, "SHARPRAIL_TOOLS_ALLOW");
        if (Env(environment, "SHARPRAIL_LOG") is { } log)
            outside.LogEnabled = log switch { "0" => false, "1" => true, _ => throw new McpConfigException($"SHARPRAIL_LOG: expected 0 or 1, got \"{log}\".") };
        if (Env(environment, "SHARPRAIL_LOG_LEVEL") is { } level)
            outside.Level = Levels.Contains(level) ? level : throw new McpConfigException($"SHARPRAIL_LOG_LEVEL: expected meta, args or full, got \"{level}\".");
        if (Env(environment, "SHARPRAIL_RUNS_DIR") is { } directory) outside.Directory = directory;
        var fromFlags = new Layer { Disable = flags.Disable is null ? null : Names(flags.Disable, "--disable-tools") };

        var merged = new Layer();
        foreach (var layer in new[] { fromFile, selected ?? new Layer(), outside, fromFlags }) merged.Overlay(layer);
        var warnings = new List<string>();
        foreach (var name in (merged.Disable ?? []).Concat(merged.Allow ?? []).Concat(merged.Tools.Keys).Distinct().Where(name => !KnownTools.Names.Contains(name)))
            warnings.Add($"tool \"{name}\" is not in the headless roster and has no effect.");
        var rules = merged.Tools.ToDictionary(pair => pair.Key, pair => new ToolRule(pair.Value.Enabled, pair.Value.Operations));
        var settings = new LogSettings(merged.LogEnabled ?? false, merged.Level ?? "meta", merged.MaxResultChars ?? 4000, merged.MaxFileKb ?? 51200,
            Path.GetFullPath(merged.Directory ?? Path.Combine(".sharprail", "runs"), workspace));
        return new McpConfig(arm, found ? FileName : null, merged.Disable ?? [], merged.Allow, rules, settings, warnings);
    }

    private static readonly string[] Levels = ["meta", "args", "full"];

    private static string? Env(Func<string, string?> environment, string name) => environment(name) is { Length: > 0 } value ? value : null;

    private static List<string> Names(string commaList, string origin)
    {
        var names = commaList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        return names.Count > 0 ? names : throw new McpConfigException($"{origin}: expected a comma-separated list of tool names.");
    }

    private sealed class ToolLayer
    {
        internal bool? Enabled;
        internal List<string>? Operations;
    }

    /// <summary>Settings from one source. A null field was not set there, so it does not replace a lower layer's value.</summary>
    private sealed class Layer
    {
        internal List<string>? Disable, Allow;
        internal Dictionary<string, ToolLayer> Tools = new(StringComparer.Ordinal);
        internal bool? LogEnabled;
        internal string? Level, Directory;
        internal int? MaxResultChars, MaxFileKb;

        internal void Overlay(Layer top)
        {
            Disable = top.Disable ?? Disable;
            Allow = top.Allow ?? Allow;
            LogEnabled = top.LogEnabled ?? LogEnabled;
            Level = top.Level ?? Level;
            Directory = top.Directory ?? Directory;
            MaxResultChars = top.MaxResultChars ?? MaxResultChars;
            MaxFileKb = top.MaxFileKb ?? MaxFileKb;
            foreach (var (name, tool) in top.Tools)
            {
                var own = Tools.TryGetValue(name, out var existing) ? existing : Tools[name] = new ToolLayer();
                own.Enabled = tool.Enabled ?? own.Enabled;
                own.Operations = tool.Operations ?? own.Operations;
            }
        }
    }

    private static void Read(string path, Layer top, Dictionary<string, Layer> arms)
    {
        var info = new FileInfo(path);
        if (info.Length > ProjectToml.MaximumBytes) throw new McpConfigException($"{FileName}: larger than {ProjectToml.MaximumBytes / 1024} KiB.");
        string text;
        try { text = System.IO.File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new McpConfigException($"{FileName}: cannot be read: {error.Message}"); }
        JsonObject document;
        try { document = ProjectToml.Parse(text); }
        catch (TomlException error) { throw new McpConfigException($"{FileName}: not valid TOML: {error.Message}"); }
        var lines = KeyLines(text);
        McpConfigException Fail(string key, string message) => new($"{FileName}:{LineOf(lines, key)}: {key}: {message}");

        if (ProjectToml.VersionOf(document) != 1) throw Fail("version", "required, and must be the integer 1.");
        foreach (var key in document.Select(pair => pair.Key).Where(key => key is not ("version" or "tools" or "log" or "arms")))
            throw Fail(key, "unknown key. Allowed at the top level: version, tools, log, arms.");
        ReadLayer(document, "", top);
        if (document["arms"] is { } armsNode)
        {
            if (armsNode is not JsonObject armTables) throw Fail("arms", "must be a table of [arms.<name>] tables.");
            foreach (var (name, node) in armTables)
            {
                var key = "arms." + name;
                if (node is not JsonObject table) throw Fail(key, "must be a table.");
                foreach (var unknown in table.Select(pair => pair.Key).Where(unknown => unknown is not ("tools" or "log")))
                    throw Fail(key + "." + unknown, "unknown key. An arm may hold tools and log.");
                var layer = new Layer();
                ReadLayer(table, key + ".", layer);
                arms[name] = layer;
            }
        }

        void ReadLayer(JsonObject table, string prefix, Layer layer)
        {
            if (table["tools"] is { } toolsNode)
            {
                if (toolsNode is not JsonObject tools) throw Fail(prefix + "tools", "must be a table.");
                foreach (var (name, node) in tools)
                {
                    var key = prefix + "tools." + name;
                    if (name == "disable") layer.Disable = StringList(node, key, allowEmpty: true);
                    else if (name == "allow") layer.Allow = StringList(node, key, allowEmpty: false);
                    else if (node is not JsonObject rule) throw Fail(key, "unknown key. Use disable, allow, or a [tools.<tool name>] table.");
                    else
                    {
                        var own = new ToolLayer();
                        foreach (var (field, value) in rule)
                        {
                            var fieldKey = key + "." + field;
                            if (field == "enabled") own.Enabled = Boolean(value, fieldKey);
                            else if (field == "operations" && KnownTools.Operations.TryGetValue(name, out var allowed))
                            {
                                own.Operations = StringList(value, fieldKey, allowEmpty: false);
                                foreach (var operation in own.Operations.Where(operation => !allowed.Contains(operation)))
                                    throw Fail(fieldKey, $"unknown operation \"{operation}\". {name} has: {string.Join(", ", allowed)}.");
                            }
                            else throw Fail(fieldKey, field == "operations" ? $"operations can only narrow {string.Join(" and ", KnownTools.Operations.Keys)}." : "unknown key. A tool table may hold enabled and operations.");
                        }
                        layer.Tools[name] = own;
                    }
                }
            }
            if (table["log"] is { } logNode)
            {
                if (logNode is not JsonObject log) throw Fail(prefix + "log", "must be a table.");
                foreach (var (name, node) in log)
                {
                    var key = prefix + "log." + name;
                    switch (name)
                    {
                        case "enabled": layer.LogEnabled = Boolean(node, key); break;
                        case "level":
                            layer.Level = Text(node, key);
                            if (!Levels.Contains(layer.Level)) throw Fail(key, $"expected meta, args or full, got \"{layer.Level}\".");
                            break;
                        case "dir": layer.Directory = Text(node, key); break;
                        case "max_result_chars": layer.MaxResultChars = Number(node, key, 0, 1_000_000); break;
                        case "max_file_kb": layer.MaxFileKb = Number(node, key, 16, 10_485_760); break;
                        default: throw Fail(key, "unknown key. The log table has enabled, level, dir, max_result_chars and max_file_kb.");
                    }
                }
            }
        }

        string Text(JsonNode? node, string key) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : throw Fail(key, "must be a non-empty string.");

        bool Boolean(JsonNode? node, string key) =>
            node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : throw Fail(key, "must be true or false.");

        int Number(JsonNode? node, string key, int minimum, int maximum) =>
            node is JsonValue value && value.TryGetValue<long>(out var number) && number >= minimum && number <= maximum ? (int)number : throw Fail(key, $"must be an integer from {minimum} to {maximum}.");

        List<string> StringList(JsonNode? node, string key, bool allowEmpty)
        {
            if (node is not JsonArray array || array.Any(item => !(item is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)))
                throw Fail(key, "must be an array of non-empty strings.");
            var items = array.Select(item => item!.GetValue<string>()).Distinct().ToList();
            return items.Count > 0 || allowEmpty ? items : throw Fail(key, "must not be empty. Remove the key to leave the setting alone.");
        }
    }

    /// <summary>The line of each key and table header, by dotted path, found by reading the text line by line. It is only used to point at a problem.</summary>
    private static Dictionary<string, int> KeyLines(string text)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = new List<string>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                if (end < 0) continue;
                table = KeyParts(line[(line[..2] == "[[" ? 2 : 1)..end]);
                lines.TryAdd(string.Join('.', table), number);
                continue;
            }
            var quote = false;
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] == '"') quote = !quote;
                else if (line[index] == '=' && !quote)
                {
                    lines.TryAdd(string.Join('.', table.Concat(KeyParts(line[..index]))), number);
                    break;
                }
            }
        }
        return lines;
    }

    private static List<string> KeyParts(string text)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        var quote = false;
        foreach (var c in text)
        {
            if (c == '"') quote = !quote;
            else if (c == '.' && !quote) { parts.Add(part.ToString().Trim()); part.Clear(); }
            else part.Append(c);
        }
        parts.Add(part.ToString().Trim());
        return parts;
    }

    private static int LineOf(Dictionary<string, int> lines, string key)
    {
        for (var path = key; ;)
        {
            if (lines.TryGetValue(path, out var line)) return line;
            var dot = path.LastIndexOf('.');
            if (dot < 0) return 1;
            path = path[..dot];
        }
    }
}

internal sealed record ToolRule(bool? Enabled, IReadOnlyList<string>? Operations);

/// <summary>The command-line flags that sit above the environment: <c>--arm</c> and <c>--disable-tools</c>.</summary>
internal sealed class McpFlags
{
    internal string? Arm { get; private set; }
    internal string? Disable { get; private set; }

    /// <summary>Takes the flag at <paramref name="index"/> (and its value) when it is one of ours.</summary>
    internal bool TryTake(string[] args, ref int index)
    {
        var argument = args[index];
        foreach (var name in new[] { "--arm", "--disable-tools" })
        {
            string? value = null;
            if (argument == name && index + 1 < args.Length) value = args[++index];
            else if (argument.StartsWith(name + "=", StringComparison.Ordinal)) value = argument[(name.Length + 1)..];
            else continue;
            if (name == "--arm") Arm = value; else Disable = value;
            return true;
        }
        return false;
    }
}