using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>Resolves the configuration a Claude session started in a directory loads, with the provenance of every part.</summary>
internal static partial class ClaudeResolver
{
    private const int MaxImportDepth = 4;

    [GeneratedRegex(@"^\s*@([^\s`]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    [GeneratedRegex(@"^---\r?\n([\s\S]*?)\r?\n---")]
    private static partial Regex FrontmatterBlock();

    [GeneratedRegex(@"^paths:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex PathsLine();

    private static long? SizeOf(string path)
    {
        try { return new FileInfo(path) is { Exists: true } file ? file.Length : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IReadOnlyList<string> ListFiles(string directory, string extension)
    {
        try { return [.. Directory.GetFiles(directory).Where(path => path.EndsWith(extension, StringComparison.Ordinal)).Order(StringComparer.Ordinal)]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IReadOnlyList<string> ListDirectories(string directory)
    {
        try { return [.. Directory.GetDirectories(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)]; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IReadOnlyList<string>? FrontmatterGlobs(string path)
    {
        try
        {
            var head = File.ReadAllText(path);
            if (head.Length > 2048) head = head[..2048];
            if (FrontmatterBlock().Match(head) is not { Success: true } block) return null;
            if (PathsLine().Match(block.Groups[1].Value) is not { Success: true } paths) return null;
            return [.. paths.Groups[1].Value.Trim().TrimStart('[').TrimEnd(']').Split(',')
                .Select(glob => glob.Trim().Trim('"', '\''))
                .Where(glob => glob.Length > 0)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<ClaudeContextLayer> ExpandImports(string path, ClaudeConfigScope scope, int depth, HashSet<string> seen)
    {
        if (depth >= MaxImportDepth) yield break;
        string body;
        try { body = File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (Match match in ImportLine().Matches(body))
        {
            var raw = match.Groups[1].Value;
            var target = raw.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(ClaudeEnvironment.Home(), raw[2..])
                : raw.StartsWith('/') ? raw : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, raw));
            if (!seen.Add(target)) continue;
            if (SizeOf(target) is not { } bytes) continue;
            yield return new(ClaudeContextKind.Import, Path.GetFileName(target), target, new(scope, target), bytes) { Depth = depth + 1 };
            foreach (var nested in ExpandImports(target, scope, depth + 1, seen)) yield return nested;
        }
    }

    private static ClaudeContextLayer? Layer(ClaudeContextKind kind, string label, ScopedPath entry) =>
        SizeOf(entry.Path) is { } bytes ? new(kind, label, entry.Path, new(entry.Scope, entry.Path), bytes) : null;

    private static List<ClaudeContextLayer> CollectContext(string root, IReadOnlyList<string> promptFiles)
    {
        var layers = new List<ClaudeContextLayer>();
        foreach (var entry in ClaudePaths.InstructionPaths(root))
        {
            var kind = entry.Path.EndsWith("CLAUDE.local.md", StringComparison.Ordinal) ? ClaudeContextKind.LocalInstructions : ClaudeContextKind.Instructions;
            if (Layer(kind, Path.GetFileName(entry.Path), entry) is not { } layer) continue;
            layers.Add(layer);
            layers.AddRange(ExpandImports(entry.Path, entry.Scope, 0, [entry.Path]));
        }
        foreach (var directory in ClaudePaths.RulesDirectories(root))
            foreach (var path in ListFiles(directory.Path, ".md"))
            {
                if (Layer(ClaudeContextKind.Rules, Path.GetFileName(path), new(directory.Scope, path)) is not { } layer) continue;
                // A rule with paths: only enters context when Claude reads a matching file, so it is not always-on weight.
                layers.Add(FrontmatterGlobs(path) is { Count: > 0 } globs ? layer with { PathGlobs = globs, Lazy = true } : layer);
            }
        if (Layer(ClaudeContextKind.Memory, "MEMORY.md", new(ClaudeConfigScope.User, ClaudePaths.MemoryIndexPath(root))) is { } memory) layers.Add(memory);
        foreach (var path in promptFiles)
            if (Layer(ClaudeContextKind.SystemPrompt, Path.GetFileName(path), new(ClaudeConfigScope.Local, path)) is { } prompt) layers.Add(prompt);
        return layers;
    }

    // The first scope that switches something off; documents arrive highest precedence first.
    private static ClaudeConfigOrigin? SwitchedOffBy(IReadOnlyList<ScopedDocument> documents, string key, string name)
    {
        foreach (var document in documents)
            if (Json.StringList(document.Data[key]).Contains(name)) return document.Origin with { KeyPath = [key] };
        return null;
    }

    private static ClaudeConfigOrigin? McpDenial(IReadOnlyList<ScopedDocument> documents, string name, bool fromProjectFile)
    {
        var denied = SwitchedOffBy(documents, "deniedMcpServers", name);
        // .mcp.json servers answer to a second switch that no other server does.
        return denied is not null || !fromProjectFile ? denied : SwitchedOffBy(documents, "disabledMcpjsonServers", name);
    }

    private static ClaudeConfigOrigin? SkillOverride(IReadOnlyList<ScopedDocument> documents, string name)
    {
        foreach (var document in documents)
            if (document.Data["skillOverrides"] is JsonObject overrides && Json.String(overrides[name]) == "off")
                return document.Origin with { KeyPath = ["skillOverrides", name] };
        return null;
    }

    private static ClaudeCapability WithState(ClaudeCapability capability, ClaudeConfigOrigin? disabledBy) =>
        disabledBy is null ? capability : capability with { Enabled = false, DisabledBy = disabledBy };

    private static List<ClaudeCapability> CollectCapabilities(string root, IReadOnlyList<ScopedDocument> documents)
    {
        var capabilities = new List<ClaudeCapability>();
        foreach (var entry in ClaudePaths.McpPaths(root))
        {
            if (Json.ReadObject(entry.Path)?["mcpServers"] is not JsonObject servers) continue;
            foreach (var (name, config) in servers)
            {
                var transport = config is JsonObject server && server["type"] is { } type ? type.ToString() : "stdio";
                capabilities.Add(WithState(new(ClaudeCapabilityKind.Mcp, name, new(entry.Scope, entry.Path) { KeyPath = ["mcpServers", name] }, true) { Detail = transport },
                    McpDenial(documents, name, true)));
            }
        }

        // User-scope MCP servers live in ~/.claude.json, not in settings.json: the sharpest trap in the whole surface,
        // so the pane sources them from the file that actually holds them.
        var statePath = ClaudePaths.ClaudeStatePath();
        var state = Json.ReadObject(statePath);
        if (state?["mcpServers"] is JsonObject userServers)
            foreach (var (name, _) in userServers)
                capabilities.Add(WithState(new(ClaudeCapabilityKind.Mcp, name, new(ClaudeConfigScope.User, statePath) { KeyPath = ["mcpServers", name] }, true) { Detail = "user scope" },
                    McpDenial(documents, name, false)));

        // The same file's per-project block is where Claude Code's "local" servers live.
        if (state?["projects"] is JsonObject projects && projects[root] is JsonObject project && project["mcpServers"] is JsonObject localServers)
            foreach (var (name, _) in localServers)
                capabilities.Add(WithState(new(ClaudeCapabilityKind.Mcp, name, new(ClaudeConfigScope.Local, statePath) { KeyPath = ["projects", root, "mcpServers", name] }, true)
                { Detail = "this project only" }, McpDenial(documents, name, false)));

        foreach (var directories in new[] { ClaudePaths.SkillDirectories(root), ClaudePaths.AgentDirectories(root) })
            foreach (var directory in directories)
            {
                var isSkill = directory.Path.EndsWith("skills", StringComparison.Ordinal);
                var names = isSkill ? ListDirectories(directory.Path) : ListFiles(directory.Path, ".md").Select(path => Path.GetFileName(path)).ToArray();
                foreach (var name in names)
                {
                    var label = name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
                    capabilities.Add(WithState(new(isSkill ? ClaudeCapabilityKind.Skill : ClaudeCapabilityKind.Agent, label,
                        new(directory.Scope, Path.Combine(directory.Path, name)), true), isSkill ? SkillOverride(documents, label) : null));
                }
            }
        return capabilities;
    }

    // Hooks are the sharpest thing in this file (a matched event runs a shell command), so every one is listed with the
    // command it runs and the file it came from.
    private static List<ClaudeCapability> CollectHooks(IReadOnlyList<ScopedDocument> documents)
    {
        var off = documents.FirstOrDefault(document => Json.IsTrue(document.Data["disableAllHooks"]));
        var capabilities = new List<ClaudeCapability>();
        foreach (var document in documents)
        {
            if (document.Data["hooks"] is not JsonObject hooks) continue;
            foreach (var (@event, groups) in hooks)
            {
                if (groups is not JsonArray groupList) continue;
                foreach (var group in groupList.OfType<JsonObject>())
                {
                    var matcher = Json.String(group["matcher"]) ?? "";
                    foreach (var entry in group["hooks"] as JsonArray ?? [])
                    {
                        var command = entry is JsonObject hook ? Json.String(hook["command"]) ?? "" : "";
                        capabilities.Add(WithState(new(ClaudeCapabilityKind.Hook, matcher.Length == 0 ? @event : $"{@event} · {matcher}",
                            document.Origin with { KeyPath = ["hooks", @event] }, true)
                        { Detail = command.Length == 0 ? null : command },
                            off is null ? null : off.Origin with { KeyPath = ["disableAllHooks"] }));
                    }
                }
            }
        }
        return capabilities;
    }

    private static string? MarketplaceDetail(JsonNode? entry)
    {
        if (entry is not JsonObject record) return null;
        if (Json.String(record["source"]) is { } text) return text;
        if (record["source"] is not JsonObject source) return null;
        foreach (var key in new[] { "repo", "url", "path", "source" })
            if (Json.String(source[key]) is { Length: > 0 } value) return value;
        return null;
    }

    // The catalogs plugins come from, as first-class rows instead of flattened settings keys.
    private static List<ClaudeCapability> CollectMarketplaces(IReadOnlyList<ScopedDocument> documents)
    {
        var marketplaces = new List<ClaudeCapability>();
        var seen = new HashSet<string>();
        foreach (var document in documents)
        {
            if (document.Data["extraKnownMarketplaces"] is not JsonObject known) continue;
            foreach (var (name, entry) in known)
            {
                if (!seen.Add(name)) continue;
                marketplaces.Add(new(ClaudeCapabilityKind.Marketplace, name, document.Origin with { KeyPath = ["extraKnownMarketplaces", name] }, true)
                { Detail = MarketplaceDetail(entry) });
            }
        }
        return marketplaces;
    }

    private static List<ClaudeCapability> CollectPlugins(IReadOnlyList<ScopedDocument> documents)
    {
        var plugins = new List<ClaudeCapability>();
        var seen = new HashSet<string>();
        foreach (var document in documents)
        {
            if (document.Data["enabledPlugins"] is not JsonObject enabled) continue;
            foreach (var (name, on) in enabled)
            {
                if (!seen.Add(name)) continue;
                var marketplace = name.Split('@') is { Length: > 1 } parts && parts[1].Length > 0 ? parts[1] : null;
                var origin = document.Origin with { KeyPath = ["enabledPlugins", name] };
                var isOn = Json.IsTrue(on);
                plugins.Add(new(ClaudeCapabilityKind.Plugin, name, origin, isOn) { Detail = marketplace, DisabledBy = isOn ? null : origin });
            }
        }
        return plugins;
    }

    private static List<ClaudeConfigProblem> DetectProblems(IReadOnlyList<ClaudeInspectedFile> inspected) =>
        [.. inspected.Where(entry => entry.Exists && entry.Path.EndsWith(".json", StringComparison.Ordinal) && Json.ReadObject(entry.Path) is null)
            .Select(entry => new ClaudeConfigProblem(ClaudeProblemSeverity.Warning, "Unreadable settings file",
                "This file is not valid JSON, so Claude Code cannot apply anything in it.") { Path = entry.Path })];

    /// <summary>Every file the snapshot names: the allowlist for reading and writing configuration through the plugin.</summary>
    public static IReadOnlyList<string> FilePaths(string workspaceId, string root, IReadOnlyList<string>? promptFiles = null)
    {
        var snapshot = Resolve(workspaceId, root, promptFiles);
        var allowed = new List<string>();
        void Add(string? path) { if (path is not null && !allowed.Contains(path)) allowed.Add(path); }
        foreach (var entry in snapshot.Inspected) Add(entry.Path);
        foreach (var layer in snapshot.Context) Add(layer.Path);
        foreach (var capability in snapshot.Capabilities) Add(capability.Origin.Path);
        foreach (var setting in snapshot.Settings)
        {
            Add(setting.Origin.Path);
            foreach (var shadow in setting.Shadowed) Add(shadow.Origin.Path);
        }
        return allowed;
    }

    private static string AllowedPath(string workspaceId, string root, string path, IReadOnlyList<string>? promptFiles) =>
        FilePaths(workspaceId, root, promptFiles).Contains(path) ? path : throw new InvalidOperationException("Not a file this workspace's Claude configuration reports");

    public static FileContent ReadFile(string workspaceId, string root, string path, IReadOnlyList<string>? promptFiles = null) =>
        TextFile.Read(AllowedPath(workspaceId, root, path, promptFiles));

    public static FileWriteResult WriteFile(string workspaceId, string root, string path, string content, string baseHash, IReadOnlyList<string>? promptFiles = null) =>
        TextFile.Write(AllowedPath(workspaceId, root, path, promptFiles), content, baseHash);

    public static ClaudeConfigSnapshot Resolve(string workspaceId, string root, IReadOnlyList<string>? promptFiles = null)
    {
        var scoped = ClaudePaths.SettingsPaths(root);
        IReadOnlyList<ClaudeInspectedFile> inspected = [.. scoped.Select(entry => new ClaudeInspectedFile(entry.Path, entry.Scope, File.Exists(entry.Path)))];
        var documents = new List<ScopedDocument>();
        foreach (var entry in scoped)
            if (Json.ReadObject(entry.Path) is { } data) documents.Add(new(new(entry.Scope, entry.Path), data));
        return new(workspaceId, root,
            CollectContext(root, promptFiles ?? []),
            [.. SettingsMerge.Resolve(documents).Select(entry => SettingsDocs.Url(entry.Key) is { } url ? entry with { DocsUrl = url } : entry)],
            [.. CollectCapabilities(root, documents), .. CollectPlugins(documents), .. CollectMarketplaces(documents), .. CollectHooks(documents)],
            DetectProblems(inspected),
            inspected,
            SettingsDocs.Keys);
    }
}