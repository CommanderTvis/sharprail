using System.Text.Json;
using System.Text.RegularExpressions;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>
/// What a tool call's card says, by tool, after the extension's tool table (spec 3.4 and 3.5): the one-line header, the secondary line under it,
/// the status dot, and the IN and OUT text. Pure functions of the item, so the drawing and the checks read the same words.
/// </summary>
internal static class ToolCards
{
    /// <summary>A header: the bold name and the monospace secondary text after it.</summary>
    internal readonly record struct Head(string Name, string Secondary);

    internal enum Dot { Progress, Failure, Success, Denied }

    /// <summary>The tools whose card is never drawn.</summary>
    internal static bool Hidden(string name) => name == "ToolSearch";

    internal static string Str(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int? Number(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    internal static string Basename(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>"Server Name" from "server_name" or "server-name".</summary>
    internal static string Humanise(string name) =>
        string.Join(" ", name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private static string Cut(string text, int length) => text.Length > length ? text[..length] + "…" : text;

    private static string Words(string text) => ChatRowLayout.Preview(text);

    internal static Head Header(ChatToolItem tool)
    {
        var input = tool.Input;
        var summary = tool.Summary ?? "";
        switch (tool.Name)
        {
            case "Bash" or "PowerShell":
                return new(tool.Name, Str(input, "description") is { Length: > 0 } description ? Words(description) : Words(Str(input, "command") is { Length: > 0 } command ? command : summary));
            case "Read":
                {
                    var offset = Number(input, "offset");
                    var limit = Number(input, "limit");
                    var range = offset is { } from && limit is { } count ? $" (lines {from}-{from + count - 1})"
                        : offset is { } start ? $" (from line {start})"
                        : limit is { } only ? $" (lines 1-{only})" : "";
                    return new("Read", Basename(Str(input, "file_path")) + range);
                }
            case "Edit" or "Write":
                return new(tool.Name, Basename(tool.FilePath ?? Str(input, "file_path")));
            case "Glob": return new("Glob", $"pattern: \"{Str(input, "pattern")}\"");
            case "Search": return new("Search", $"pattern: \"{Str(input, "pattern")}\"");
            case "Grep":
                {
                    var options = new List<string>();
                    if (Str(input, "path") is { Length: > 0 } path) options.Add("in " + path);
                    if (Str(input, "glob") is { Length: > 0 } glob) options.Add("glob: " + glob);
                    if (Str(input, "type") is { Length: > 0 } type) options.Add("type: " + type);
                    return new("Grep", $"\"{Str(input, "pattern")}\"" + (options.Count > 0 ? $" ({string.Join(", ", options)})" : ""));
                }
            case "Task" or "Agent": return new("Agent:", Str(input, "description"));
            case "Workflow": return new("Workflow", WorkflowName(tool));
            case "Monitor": return new("Monitor", Words(Str(input, "description") is { Length: > 0 } watched ? watched : Str(input, "command") is { Length: > 0 } watch ? watch : summary));
            case "Skill": return new(Str(input, "skill").TrimStart('/') + " skill", "");
            case "TodoWrite": return new("Update Todos", "");
            case "WebFetch": return new("Web Fetch", Str(input, "url"));
            case "WebSearch": return new("Web Search", Str(input, "query"));
            case "NotebookEdit":
                return new("Edit Notebook Cell", Basename(Str(input, "notebook_path")) + (Str(input, "cell_id") is { Length: > 0 } cell ? ":" + cell : ""));
            case "Artifact": return new("Artifact", Str(input, "file_path"));
            case "SendUserFile": return new("Sent", string.Join(", ", Files(input).Select(Basename)));
            case "ExitPlanMode": return new("Claude’s Plan", Str(input, "planFilePath") is { Length: > 0 } plan ? Basename(plan) : "");
            case var name when name.StartsWith("mcp__", StringComparison.Ordinal): return Mcp(name, input);
            default: return new(tool.Name, summary);
        }
    }

    private static IEnumerable<string> Files(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) yield break;
        if (input.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            foreach (var file in files.EnumerateArray())
            {
                var path = file.ValueKind == JsonValueKind.String ? file.GetString() ?? "" : Str(file, "file_path") is { Length: > 0 } named ? named : Str(file, "path");
                if (path.Length > 0) yield return path;
            }
        else if (Str(input, "file_path") is { Length: > 0 } single) yield return single;
        else if (Str(input, "path") is { Length: > 0 } alternative) yield return alternative;
    }

    private static readonly string[] McpArguments = ["query", "message", "channel", "repo", "url", "path", "title", "search", "text"];

    private static Head Mcp(string name, JsonElement input)
    {
        var parts = name.Split("__", 3);
        var server = parts.Length > 1 ? parts[1] : name;
        var tool = parts.Length > 2 ? parts[2] : "";
        if (IsTereditor(server)) return new("tereditor [" + TereditorTool(tool) + "]", TereditorSecondary(tool, input));
        var title = server == "claude-in-chrome" ? "Claude in Chrome" : Humanise(server);
        var shown = server == "claude-in-chrome" ? Humanise(tool) : tool;
        var secondary = "";
        foreach (var key in McpArguments)
        {
            var value = Str(input, key);
            if (value.Length == 0) continue;
            secondary = key switch
            {
                "channel" => Cut(value.StartsWith('#') ? value : "#" + value, 40),
                "url" => Cut(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : value, 40),
                "text" => Cut(value, 30),
                _ => Cut(value, 40)
            };
            break;
        }
        return new(title + " [" + shown + "]", secondary);
    }

    /// <summary>tereditor's own MCP server, as Claude names it (plugin_tereditor_tereditor, plugin:tereditor:tereditor) or plainly.</summary>
    private static bool IsTereditor(string server) => server is "tereditor" or "plugin_tereditor_tereditor" or "plugin:tereditor:tereditor";

    /// <summary>"LSP navigate" from "lsp_navigate": first word capitalised, acronyms upper-case.</summary>
    internal static string TereditorTool(string tool)
    {
        var words = tool.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select((word, index) => word == "lsp" ? "LSP" : index == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word));
    }

    private static string TereditorSecondary(string tool, JsonElement input)
    {
        string Pick(params string[] keys) => keys.Select(key => Str(input, key)).FirstOrDefault(value => value.Length > 0) ?? "";
        var text = tool switch
        {
            "lsp_navigate" => string.Join(" ", new[] { Str(input, "operation"), Pick("query", "path") }.Where(part => part.Length > 0)),
            "lsp_diagnostics" => Pick("path") is { Length: > 0 } one ? one
                : input.ValueKind == JsonValueKind.Object && input.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array && paths.GetArrayLength() > 0
                    ? (paths[0].GetString() ?? "") + (paths.GetArrayLength() > 1 ? $" (+{paths.GetArrayLength() - 1} more)" : "")
                    : Str(input, "scope"),
            "context_search" => Pick("query"),
            "workspace_create" => Pick("description", "name"),
            "set_title" => Pick("title"),
            "visualize" => Pick("title", "type"),
            _ when tool.StartsWith("spec_", StringComparison.Ordinal) => Pick("id", "query", "title"),
            _ when tool.StartsWith("owned_process_", StringComparison.Ordinal) => Pick("description", "pid"),
            _ => Pick("query", "title", "path")
        };
        if (text.Length == 0 && input.ValueKind == JsonValueKind.Object && tool.StartsWith("owned_process_", StringComparison.Ordinal) && input.TryGetProperty("pid", out var pid)) text = pid.ToString();
        return Cut(Words(text), 60);
    }

    /// <summary>Lines of a text: its line breaks plus one, a final break not counted; zero for none.</summary>
    internal static int Lines(string text)
    {
        if (text.Length == 0) return 0;
        var count = 1;
        for (var i = 0; i < text.Length - 1; i++) if (text[i] == '\n') count++;
        return count;
    }

    /// <summary>"1 line", "N lines".</summary>
    private static string Plural(int count, string suffix = "") => $"{count} {(count == 1 ? "line" : "lines")}{suffix}";

    /// <summary>What a workflow script's literal <c>export const meta</c> block says; fields it does not give are empty.</summary>
    internal readonly record struct WorkflowMeta(string Name, string Description, IReadOnlyList<string> Phases);

    private static readonly Regex Quoted = new("""(['"`])(?<v>(?:\\.|(?!\1).)*)\1""", RegexOptions.Singleline);

    /// <summary>The script's meta block as written (a plain object literal with name, description and optional phases), read without running anything.</summary>
    internal static WorkflowMeta MetaOf(JsonElement input)
    {
        var script = Str(input, "script");
        var at = script.IndexOf("meta", StringComparison.Ordinal);
        if (at < 0) return new("", "", []);
        var end = script.IndexOf("\n}", at, StringComparison.Ordinal);
        var block = script[at..(end < 0 ? Math.Min(script.Length, at + 2000) : end)];
        string Field(string key) => Regex.Match(block, @"\b" + key + @"\s*:\s*" + Quoted.ToString()) is { Success: true } match ? Regex.Unescape(match.Groups["v"].Value) : "";
        var phases = new List<string>();
        if (Regex.Match(block, @"\bphases\s*:\s*\[(?<b>.*?)\]\s*,?\s*(?:\}|\w+\s*:|$)", RegexOptions.Singleline) is { Success: true } list)
        {
            var body = list.Groups["b"].Value;
            var keyed = Regex.Matches(body, @"\b(?:title|name)\s*:\s*" + Quoted.ToString());
            foreach (Match match in keyed.Count > 0 ? keyed : Quoted.Matches(body)) phases.Add(Regex.Unescape(match.Groups["v"].Value));
        }
        return new(Field("name"), Field("description"), phases);
    }

    /// <summary>The workflow's name: the call's <c>name</c>, else the script's meta name, else empty (the header says "Workflow").</summary>
    internal static string WorkflowName(ChatToolItem tool) => Str(tool.Input, "name") is { Length: > 0 } name ? name : MetaOf(tool.Input).Name;

    /// <summary>The lines inside an open Workflow card, above the script: description, phases, args, resume and the result summary.</summary>
    internal static IReadOnlyList<string> WorkflowLines(ChatToolItem tool)
    {
        var lines = new List<string>();
        var meta = MetaOf(tool.Input);
        if (meta.Description.Length > 0) lines.Add(meta.Description);
        if (meta.Phases.Count > 0 && tool.WorkflowAgents is not { Count: > 0 }) lines.Add("Phases: " + string.Join(" \u203A ", meta.Phases));
        if (tool.Input.ValueKind == JsonValueKind.Object && tool.Input.TryGetProperty("args", out var args) && args.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            lines.Add("Args: " + Cut(Words(args.ValueKind == JsonValueKind.String ? args.GetString() ?? "" : args.GetRawText()), 200));
        if (Str(tool.Input, "resumeFromRunId") is { Length: > 0 } run) lines.Add("Resumes run " + run);
        if (Str(tool.Input, "scriptPath") is { Length: > 0 } path) lines.Add("Script: " + path);
        if (tool.TaskSummary is { Length: > 0 } summary) lines.Add(summary);
        return lines;
    }

    /// <summary>The compact line of a workflow with agent progress: how many agents are done, any that failed, and the running ones by label. Null without agents.</summary>
    internal static string? WorkflowProgress(ChatToolItem tool)
    {
        if (tool.WorkflowAgents is not { Count: > 0 } agents) return null;
        var done = agents.Count(agent => agent.State == ChatAgentState.Done);
        var failed = agents.Count(agent => agent.State == ChatAgentState.Failed);
        var line = $"{done} of {agents.Count} agents done" + (failed > 0 ? $", {failed} failed" : "");
        var running = agents.Where(agent => agent.State == ChatAgentState.Running).Select(agent => Cut(Words(agent.Label), 28)).ToList();
        if (running.Count > 0 && tool.TaskStatus is null or ChatTaskStatus.Running)
            line += " \u00B7 running: " + string.Join(", ", running.Take(3)) + (running.Count > 3 ? $" +{running.Count - 3}" : "");
        return line;
    }

    /// <summary>The status mark and text of one agent row: running, done, failed or waiting.</summary>
    internal static (string Mark, string Text) AgentRow(ChatWorkflowAgent agent, bool live)
    {
        var mark = agent.State switch { ChatAgentState.Done => "\u2713", ChatAgentState.Failed => "\u2717", ChatAgentState.Queued => "\u25CB", _ => live ? "\u25D0" : "\u25CB" };
        var detail = agent.State switch
        {
            ChatAgentState.Failed => agent.Error ?? agent.Result,
            ChatAgentState.Done => agent.Result,
            ChatAgentState.Queued => "waiting",
            _ => agent.Activity
        };
        var time = agent.DurationMs is { } ms ? " (" + (ms < 1000 ? Units.Pair(ms, "ms") : ms < 60_000 ? Units.Pair((ms / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), "s") : $"{Units.Pair(ms / 60_000, "min")} {Units.Pair(ms / 1000 % 60, "s")}") + ")" : "";
        return (mark, Cut(Words(agent.Label), 60) + (detail is { Length: > 0 } ? ": " + Cut(Words(detail), 120) : "") + time);
    }

    private static bool Background(string name) => name is "Workflow" or "Monitor";

    private static string StatusWord(ChatTaskStatus status) => status switch
    {
        ChatTaskStatus.Running => "Running in background",
        ChatTaskStatus.Done => "Completed",
        ChatTaskStatus.Failed => "Failed",
        _ => "Stopped"
    };

    /// <summary>The line under the header that says what the call did, or null when the tool has none (or it has not happened yet).</summary>
    internal static string? Secondary(ChatToolItem tool)
    {
        var input = tool.Input;
        switch (tool.Name)
        {
            case "Edit":
                {
                    if (tool.State == ChatToolState.Failed) return "Edit failed";
                    var added = Lines(Str(input, "new_string"));
                    var removed = Lines(Str(input, "old_string"));
                    if (added > 0 && removed == 0) return "Added " + Plural(added);
                    if (removed > 0 && added == 0) return "Removed " + Plural(removed);
                    if (added == removed) return "Modified";
                    return $"Added {Plural(added)}, removed {Plural(removed)}";
                }
            case "Write":
                return tool.State switch
                {
                    ChatToolState.Failed => "Write failed",
                    ChatToolState.Done => "Write succeeded",
                    _ => Plural(Lines(Str(input, "content")))
                };
            case "Grep" or "Glob" or "Search":
                {
                    if (tool.State != ChatToolState.Done) return null;
                    var count = (tool.Output ?? "").Split('\n').Count(line => line.Trim() is { Length: > 0 } text && text is not ("No matches found" or "No files found"));
                    return count == 0 ? "No matches found" : count == 1 ? $"{Units.Count(1, "line")} of output" : $"{Units.Count(count, "line")} of output";
                }
            case "Workflow" or "Monitor" when tool.State != ChatToolState.Failed && tool.State != ChatToolState.Denied:
                {
                    var word = tool.TaskStatus is { } status ? StatusWord(status) : tool.State == ChatToolState.Done ? "Started in background" : "Starting";
                    var progress = tool.Name == "Workflow" ? WorkflowProgress(tool) : null;
                    // While it runs the progress line says more than "Running in background"; once it ends the outcome leads.
                    if (progress is not null) word = tool.TaskStatus is null or ChatTaskStatus.Running ? progress : word + " \u00B7 " + progress;
                    return word + (tool.TaskId is { Length: > 0 } id ? " \u00B7 task " + id : "");
                }
            case "NotebookEdit":
                return tool.State switch { ChatToolState.Done => "Success", ChatToolState.Failed => "Failed", _ => "Pending" };
            case "WebFetch":
                return tool.State == ChatToolState.Done && Str(input, "url") is { Length: > 0 } url ? "Fetched from " + url : null;
            case "WebSearch":
                {
                    var allowed = Names(input, "allowed_domains");
                    var blocked = Names(input, "blocked_domains");
                    var parts = new List<string>();
                    if (allowed.Count > 0) parts.Add("Allowed: " + string.Join(", ", allowed));
                    if (blocked.Count > 0) parts.Add("Blocked: " + string.Join(", ", blocked));
                    return parts.Count == 0 ? null : string.Join(" · ", parts);
                }
            default: return null;
        }
    }

    private static List<string> Names(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "")] : [];

    /// <summary>The status dot: blinking while the call is pending, failure for an error or an interrupted call, success otherwise.</summary>
    internal static Dot StatusOf(ChatToolItem tool) => Background(tool.Name) && tool.State is ChatToolState.Done or ChatToolState.Running && tool.TaskStatus is { } task
        ? task switch { ChatTaskStatus.Running => Dot.Progress, ChatTaskStatus.Done => Dot.Success, ChatTaskStatus.Failed => Dot.Failure, _ => Dot.Denied }
        : tool.State switch
        {
            ChatToolState.Pending or ChatToolState.Running => Dot.Progress,
            ChatToolState.Failed => Dot.Failure,
            ChatToolState.Denied => Dot.Denied,
            _ => Dot.Success
        };

    /// <summary>Whether a card shows an IN row, an OUT row, both or neither, by tool.</summary>
    internal static (bool In, bool Out) Rows(ChatToolItem tool) => tool.Name switch
    {
        "Read" or "Skill" or "TodoWrite" or "ExitPlanMode" or "Edit" or "Write" or "NotebookEdit" or "Grep" or "Glob" or "Search" or "WebFetch" or "WebSearch" or "Artifact" or "SendUserFile" => (false, false),
        "Task" or "Agent" => (true, true),
        "Workflow" or "Monitor" => (true, false),
        _ => (true, true)
    };

    /// <summary>The IN row: a command as it is, anything else as indented JSON.</summary>
    internal static string InText(ChatToolItem tool) => tool.Name is "Bash" or "PowerShell" && Str(tool.Input, "command") is { Length: > 0 } command ? command
        : tool.Name is "Task" or "Agent" ? Str(tool.Input, "prompt")
        : tool.Name == "Workflow" ? (Str(tool.Input, "script") is { Length: > 0 } script ? script : Str(tool.Input, "scriptPath"))
        : tool.Name == "Monitor" && Str(tool.Input, "command") is { Length: > 0 } watch ? watch
        : tool.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? ""
        : tool.Name.StartsWith("mcp__", StringComparison.Ordinal) && IsTereditor(tool.Name.Split("__", 3)[1]) && tool.Input.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Serialize(tool.Input.EnumerateObject().Where(property => property.Name != "workspace").ToDictionary(property => property.Name, property => property.Value), ChatUIJson.Indented.DictionaryStringJsonElement)
        : JsonSerializer.Serialize(tool.Input, ChatUIJson.Indented.JsonElement);

    /// <summary>A text longer than 250 characters or over three lines is clipped to 60 pixels and opens in full on a click.</summary>
    internal static bool Openable(string text) => text.Length > 250 || Lines(text) > 3;

    /// <summary>A thinking block's summary: "Thinking..." with the running token count while it streams, "Thought for Ns" after (rounded seconds), "Thinking" when no duration is known.</summary>
    internal static string ThinkingLabel(ChatThinkingItem thinking) =>
        thinking.Streaming ? "Thinking..." + (thinking.Tokens is { } tokens ? $" · {tokens} tokens" : "") : thinking.DurationMs is { } took ? $"Thought for {ChatDuration.Format((long)Math.Max(1, Math.Round(took / 1000.0, MidpointRounding.AwayFromZero)))}" : "Thinking";
}