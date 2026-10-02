using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>Plans a configuration edit as a diff against the file it lands in, and applies exactly the plan the user approved.</summary>
internal static partial class ClaudeEdits
{
    // A server the user rejects is recorded here; the key applies in every scope, unlike the mcpjson pair.
    private const string DeniedKey = "deniedMcpServers";
    private const string PluginsKey = "enabledPlugins";
    private const string SkillsKey = "skillOverrides";
    private const string HooksKey = "hooks";
    private const string MarketplacesKey = "extraKnownMarketplaces";

    private static (string File, string Body, string What) Template(ClaudeFileTemplate template, string root) => template switch
    {
        ClaudeFileTemplate.ProjectLocalInstructions => (Path.Combine(root, "CLAUDE.local.md"),
            "# Local notes\n\nInstructions for this project that only apply on this machine: the toolchain installed here,\nlocal ports, scratch paths.\n",
            "personal notes for this project that are not shared"),
        _ => (Path.Combine(root, "CLAUDE.md"), "# Project instructions\n\nHow Claude should work in this repository.\n",
            "shared instructions for everyone on this project")
    };

    private static string ReadIfPresent(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string SettingsPathFor(string root, ClaudeWritableScope scope) =>
        ClaudePaths.SettingsPaths(root).FirstOrDefault(entry => entry.Scope.ToString() == scope.ToString())?.Path
        ?? throw new InvalidOperationException($"No settings file for scope: {scope.Name()}");

    private static string TargetPath(string root, ClaudeWritableScope scope, ClaudeEdit edit) => edit switch
    {
        FileEdit file => Template(file.Template, root).File,
        // A server is never declared in settings.json: project servers live in .mcp.json, the rest in ~/.claude.json.
        McpAddEdit => scope == ClaudeWritableScope.Project ? Path.Combine(root, ".mcp.json") : ClaudePaths.ClaudeStatePath(),
        SkillCreateEdit skill => Path.Combine(scope == ClaudeWritableScope.User ? ClaudePaths.ClaudeHome() : Path.Combine(root, ".claude"),
            "skills", SkillDirectoryName(skill.Name), "SKILL.md"),
        _ => SettingsPathFor(root, scope)
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    // Claude Code addresses a skill by its directory, and reads its name from the frontmatter.
    private static string SkillDirectoryName(string name)
    {
        var slug = NotSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 0 ? slug : throw new InvalidOperationException("A skill needs a name");
    }

    private static string SkillBody(string name, string description)
    {
        if (description.Trim().Length == 0) throw new InvalidOperationException("A skill needs a description — it is how Claude finds it");
        return $"---\nname: {SkillDirectoryName(name)}\ndescription: {description.Trim()}\n---\n\n# {name.Trim()}\n\nWhat to do, step by step.\n";
    }

    [GeneratedRegex(@"^[^/\s]+/[^/\s]+$")]
    private static partial Regex OwnerRepo();

    private static JsonObject MarketplaceEntry(ClaudeMarketplaceSource source) => source switch
    {
        GithubMarketplaceSource github => OwnerRepo().IsMatch(github.Repo.Trim())
            ? new JsonObject { ["source"] = new JsonObject { ["source"] = "github", ["repo"] = github.Repo.Trim() } }
            : throw new InvalidOperationException("A GitHub marketplace is owner/repo"),
        DirectoryMarketplaceSource directory => directory.Path.Trim().Length > 0
            ? new JsonObject { ["source"] = new JsonObject { ["source"] = "directory", ["path"] = directory.Path.Trim() } }
            : throw new InvalidOperationException("A directory marketplace needs a path"),
        _ => throw new InvalidOperationException("Unknown marketplace source")
    };

    private static string HookEventName(ClaudeHookEvent @event) => @event.ToString();

    // Hooks nest event → matcher group → commands; a second hook on the same matcher joins that group rather than
    // opening a rival one, since Claude Code runs every group that matches.
    private static void AddHook(JsonObject root, string @event, string matcher, string command)
    {
        if (command.Trim().Length == 0) throw new InvalidOperationException("A hook needs a command to run");
        var hooks = Branch(root, HooksKey);
        var groups = hooks[@event] as JsonArray ?? [];
        hooks[@event] = groups;
        var entry = new JsonObject { ["type"] = "command", ["command"] = command.Trim() };
        var target = groups.OfType<JsonObject>().FirstOrDefault(group => (Json.String(group["matcher"]) ?? "") == matcher);
        if (target is not null)
        {
            var existing = target["hooks"] as JsonArray ?? [];
            target["hooks"] = existing;
            existing.Add(entry);
        }
        else
        {
            var group = new JsonObject();
            if (matcher.Length > 0) group["matcher"] = matcher;
            group["hooks"] = new JsonArray(entry);
            groups.Add(group);
        }
    }

    private static JsonObject ParseObject(string raw)
    {
        if (raw.Trim().Length == 0) return [];
        return JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException("That settings file is not a JSON object");
    }

    private static JsonObject Branch(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static void Prune(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject { Count: 0 } or JsonArray { Count: 0 }) parent.Remove(key);
    }

    private static void SetDotted(JsonObject root, string key, JsonElement? value)
    {
        var parts = key.Split('.');
        var cursor = root;
        foreach (var part in parts[..^1])
        {
            if (cursor[part] is not JsonObject next) cursor[part] = next = new JsonObject();
            cursor = next;
        }
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) cursor.Remove(parts[^1]);
        else cursor[parts[^1]] = JsonNode.Parse(value.Value.GetRawText());
    }

    private static JsonObject ServerEntry(ClaudeMcpServerDraft draft)
    {
        var entry = new JsonObject { ["type"] = draft.Transport.ToString().ToLowerInvariant() };
        if (draft.Transport == ClaudeMcpTransport.Stdio)
        {
            if (string.IsNullOrWhiteSpace(draft.Command)) throw new InvalidOperationException("A stdio server needs a command to run");
            entry["command"] = draft.Command.Trim();
            if (draft.Args is { Count: > 0 } args) entry["args"] = new JsonArray([.. args.Select(argument => (JsonNode?)argument)]);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(draft.Url)) throw new InvalidOperationException($"A {draft.Transport.ToString().ToLowerInvariant()} server needs a URL");
            entry["url"] = draft.Url.Trim();
            if (draft.Headers is { Count: > 0 } headers) entry["headers"] = Map(headers);
        }
        if (draft.Env is { Count: > 0 } env) entry["env"] = Map(env);
        return entry;

        static JsonObject Map(IReadOnlyDictionary<string, string> pairs)
        {
            var map = new JsonObject();
            foreach (var (name, value) in pairs) map[name] = value;
            return map;
        }
    }

    // Where a server of a given scope is declared inside its file: ~/.claude.json nests the local ones per project.
    private static JsonObject ServerHome(JsonObject root, ClaudeWritableScope scope, string projectRoot) =>
        scope == ClaudeWritableScope.Local ? Branch(Branch(Branch(root, "projects"), projectRoot), "mcpServers") : Branch(root, "mcpServers");

    private static string NextContent(string existing, ClaudeEdit edit, ClaudeWritableScope scope, string root)
    {
        if (edit is FileEdit file) return existing.Length == 0 ? Template(file.Template, root).Body : existing;
        // A skill that already exists is someone's work; the pane offers to create one, never to replace one.
        if (edit is SkillCreateEdit create) return existing.Length == 0 ? SkillBody(create.Name, create.Description) : existing;
        var document = ParseObject(existing);
        switch (edit)
        {
            case SettingEdit setting:
                SetDotted(document, setting.Key, setting.Value);
                break;
            case McpEdit mcp:
                var denied = Json.StringList(document[DeniedKey]).Where(name => name != mcp.Server).ToList();
                if (!mcp.Allowed) denied.Add(mcp.Server);
                if (denied.Count > 0) document[DeniedKey] = new JsonArray([.. denied.Select(name => (JsonNode?)name)]);
                else document.Remove(DeniedKey);
                break;
            case McpAddEdit add:
                if (add.Server.Trim().Length == 0) throw new InvalidOperationException("A server needs a name");
                ServerHome(document, scope, root)[add.Server] = ServerEntry(add.Draft);
                break;
            case PluginEdit plugin:
                Branch(document, PluginsKey)[plugin.Name] = plugin.Enabled;
                break;
            case PluginAddEdit pluginAdd:
                if (pluginAdd.Marketplace.Trim().Length == 0 || pluginAdd.Plugin.Trim().Length == 0)
                    throw new InvalidOperationException("A plugin needs a marketplace and a name");
                Branch(document, MarketplacesKey)[pluginAdd.Marketplace.Trim()] = MarketplaceEntry(pluginAdd.Source);
                Branch(document, PluginsKey)[$"{pluginAdd.Plugin.Trim()}@{pluginAdd.Marketplace.Trim()}"] = true;
                break;
            case HookEdit hook:
                AddHook(document, HookEventName(hook.Event), hook.Matcher.Trim(), hook.Command);
                break;
            case SkillEdit skill:
                // An override is only ever a switch-off: re-enabling is the absence of one, not "on" written back.
                if (skill.Enabled)
                {
                    Branch(document, SkillsKey).Remove(skill.Name);
                    Prune(document, SkillsKey);
                }
                else Branch(document, SkillsKey)[skill.Name] = "off";
                break;
        }
        return LineDiff.FormatJson(existing, document);
    }

    private static string Describe(ClaudeEdit edit, ClaudeWritableScope scope, string root)
    {
        var who = ClaudeValues.ScopeWording(scope);
        return edit switch
        {
            McpEdit mcp => mcp.Allowed ? $"Stop denying the MCP server \"{mcp.Server}\" — affects {who}." : $"Deny the MCP server \"{mcp.Server}\" — affects {who}.",
            McpAddEdit add => $"Add the MCP server \"{add.Server}\" — affects {who}.",
            PluginEdit plugin => plugin.Enabled ? $"Turn on the plugin \"{plugin.Name}\" — affects {who}." : $"Turn off the plugin \"{plugin.Name}\" — affects {who}.",
            PluginAddEdit add => $"Add the plugin \"{add.Plugin}\" from \"{add.Marketplace}\" — affects {who}.",
            HookEdit hook => $"Run `{hook.Command}` on {HookEventName(hook.Event)}{(hook.Matcher.Length > 0 ? $" for {hook.Matcher}" : "")} — affects {who}.",
            SkillCreateEdit create => $"Create the skill \"{create.Name}\" — affects {who}.",
            SkillEdit skill => skill.Enabled ? $"Stop overriding the skill \"{skill.Name}\" — affects {who}." : $"Turn off the skill \"{skill.Name}\" — affects {who}.",
            SettingEdit setting => setting.Value is not { ValueKind: not JsonValueKind.Null } value
                ? $"Remove \"{setting.Key}\" — affects {who}."
                : $"Set \"{setting.Key}\" to {Json.Compact(value)} — affects {who}.",
            FileEdit file => $"Create {Template(file.Template, root).What} — affects {who}.",
            _ => ""
        };
    }

    // The resolved settings key an edit competes on, or null for an edit precedence does not arbitrate.
    private static string? ContestedKey(ClaudeEdit edit) => edit switch
    {
        SettingEdit setting => setting.Key,
        McpEdit => DeniedKey,
        PluginEdit plugin => $"{PluginsKey}.{plugin.Name}",
        SkillEdit skill => $"{SkillsKey}.{skill.Name}",
        _ => null
    };

    private static readonly ClaudeConfigScope[] Order = [ClaudeConfigScope.Managed, ClaudeConfigScope.Local, ClaudeConfigScope.Project, ClaudeConfigScope.User, ClaudeConfigScope.Default];

    // What a higher-precedence file already decides, so the user is told before writing something inert. Warn, never
    // refuse: a losing file can still be the one worth editing.
    private static IReadOnlyList<string> ConflictWarnings(string workspaceId, string root, ClaudeWritableScope scope, ClaudeEdit edit)
    {
        var snapshot = ClaudeResolver.Resolve(workspaceId, root);
        if (edit is McpAddEdit add)
            return snapshot.Capabilities.FirstOrDefault(item => item.Kind == ClaudeCapabilityKind.Mcp && item.Name == add.Server) is { } clash
                ? [$"A server called \"{add.Server}\" is already declared in {clash.Origin.Scope.Name()} scope — Claude Code will use one of the two, and which one is not something this pane can promise."]
                : [];
        if (ContestedKey(edit) is not { } key) return [];
        var resolved = snapshot.Settings.FirstOrDefault(entry => entry.Key == key);
        if (resolved is null || resolved.Origin.Scope.Name() == scope.Name()) return [];
        var wins = Array.IndexOf(Order, resolved.Origin.Scope) < Array.IndexOf(Order, Enum.Parse<ClaudeConfigScope>(scope.ToString()));
        return wins
            ? [$"\"{key}\" is already set in {resolved.Origin.Scope.Name()} settings, which wins over {scope.Name()} — this change will have no effect until that one changes."]
            : [];
    }

    private static void RequireScope(ClaudeWritableScope scope, ClaudeEdit edit)
    {
        if (!ClaudeValues.EditScopes(edit).Contains(scope))
            throw new InvalidOperationException($"That change cannot be written to {scope.Name()} settings");
    }

    public static ClaudeEditPlan Plan(string workspaceId, ClaudeWritableScope scope, ClaudeEdit edit, string root)
    {
        RequireScope(scope, edit);
        var path = TargetPath(root, scope, edit);
        var existing = ReadIfPresent(path);
        var updated = NextContent(existing, edit, scope, root);
        return new(path, File.Exists(path), Describe(edit, scope, root), LineDiff.Lines(existing, updated),
            ConflictWarnings(workspaceId, root, scope, edit), TextFile.ContentHash(existing), updated != existing);
    }

    public static ClaudeEditPlan Apply(string workspaceId, ClaudeWritableScope scope, ClaudeEdit edit, string baseHash, string root)
    {
        RequireScope(scope, edit);
        var path = TargetPath(root, scope, edit);
        var existing = ReadIfPresent(path);
        // The approval was given for a diff against this exact content; if the file moved since, the diff the user
        // approved is not the change that would land.
        if (TextFile.ContentHash(existing) != baseHash) throw new InvalidOperationException("That file changed since the diff was shown — review it again");
        var updated = NextContent(existing, edit, scope, root);
        if (updated != existing)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, updated);
        }
        return Plan(workspaceId, scope, edit, root);
    }
}