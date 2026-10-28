using System.Text.Json;

namespace SharpRail.Plugins.Chat.UI;

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
                    return count == 0 ? "No matches found" : count == 1 ? "1 line of output" : $"{count} lines of output";
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
    internal static Dot StatusOf(ChatToolItem tool) => tool.State switch
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
        "Task" or "Agent" => (true, false),
        _ => (true, true)
    };

    /// <summary>The IN row: a command as it is, anything else as indented JSON.</summary>
    internal static string InText(ChatToolItem tool) => tool.Name is "Bash" or "PowerShell" && Str(tool.Input, "command") is { Length: > 0 } command ? command
        : tool.Name is "Task" or "Agent" ? Str(tool.Input, "prompt")
        : tool.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : JsonSerializer.Serialize(tool.Input, Indented);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A text longer than 250 characters or over three lines is clipped to 60 pixels and opens in full on a click.</summary>
    internal static bool Openable(string text) => text.Length > 250 || Lines(text) > 3;

    /// <summary>A thinking block's summary: "Thinking..." with the running token count while it streams, "Thought for Ns" after (rounded seconds), "Thinking" when no duration is known.</summary>
    internal static string ThinkingLabel(ChatThinkingItem thinking) =>
        thinking.Streaming ? "Thinking..." + (thinking.Tokens is { } tokens ? $" · {tokens} tokens" : "") : thinking.DurationMs is { } took ? $"Thought for {Math.Max(1, Math.Round(took / 1000.0, MidpointRounding.AwayFromZero))}s" : "Thinking";
}