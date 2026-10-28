using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

// User-role frames, classified the way the official extension's webview does (see "Chat semantics" in the plugin's SPEC):
// by origin and text shape, not by role. Live frames and transcript lines take this one path.
internal sealed partial class ChatFolder
{
    private const int PreviewLimit = 512;
    private string? pendingCompaction;

    private static readonly string[] PeerLeads =
    [
        "Another Claude session sent a message while you were working:\n",
        "Another Claude session sent a message:\n",
        "A peer session sent a message while you were working:\n"
    ];

    private static readonly string[] PeerTrailers =
    [
        "not typed by your user", "That 'other Claude session'", "This is NOT from your user", "This is from another Claude session, not your user", "untrusted"
    ];

    private static readonly Dictionary<string, string> WrappedLabels = new()
    {
        ["teammate-message"] = "Teammate message",
        ["bash-stdout"] = "Terminal command output",
        ["bash-stderr"] = "Terminal command error output",
        ["tick"] = "Autonomous prompt",
        ["mcp-resource-update"] = "MCP resource update",
        ["mcp-polling-update"] = "MCP polling update",
        ["local-command-caveat"] = "Command caveat"
    };

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F‎‏‪-‮⁦-⁩]")]
    private static partial Regex Controls();

    [GeneratedRegex(@"^<(?<tag>agent-message|cross-session-message|channel)(?<attrs>[^>]*)>\n(?<body>.*)\n</\k<tag>>(?<trailer>.*)$", RegexOptions.Singleline)]
    private static partial Regex PeerBody();

    [GeneratedRegex(@"(?<k>[\w-]+)=""(?<v>[^""]*)""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"^A message arrived from (?<source>[^\n:]*):\n")]
    private static partial Regex ChannelLead();

    [GeneratedRegex(@"^<(?<tag>[a-z-]+)>(?<body>.*)</\k<tag>>\s*$", RegexOptions.Singleline)]
    private static partial Regex Wrapper();

    [GeneratedRegex(@"</?pasted_content[^>]*>")]
    private static partial Regex PastedToken();

    [GeneratedRegex(@"<forked-skill-launch>.*?</forked-skill-launch>", RegexOptions.Singleline)]
    private static partial Regex ForkedSkill();

    /// <summary>Strips ANSI escapes, control characters and bidi marks, the way the extension does before it shows any text.</summary>
    internal static string Sanitise(string text) => Controls().Replace(Ansi().Replace(text, ""), "");

    internal static string Preview(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var collapsed = Regex.Replace(line.Trim(), @"\s+", " ");
            if (collapsed.Length == 0) continue;
            return collapsed.Length > PreviewLimit ? collapsed[..PreviewLimit] + "…" : collapsed;
        }
        return "";
    }

    private static string Tag(string text, string tag)
    {
        var open = text.IndexOf($"<{tag}>", StringComparison.Ordinal);
        if (open < 0) return "";
        var start = open + tag.Length + 2;
        var close = text.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        return close < 0 ? "" : text[start..close].Trim();
    }

    private ChatOrigin? OriginOf(JsonElement message)
    {
        if (!message.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.Object || Str(origin, "kind") is not { } kind) return null;
        return new ChatOrigin(kind) { Name = Str(origin, "name"), SenderTaskId = Str(origin, "senderTaskId") ?? Str(origin, "sender_task_id"), Subkind = Str(origin, "subkind") };
    }

    private ChatNoticeItem PutNotice(string id, ChatNoticeLevel level, string text, string category, string? label = null, string? detail = null, IReadOnlyList<ChatSection>? sections = null, string? parent = null)
    {
        var item = new ChatNoticeItem(id, items.GetValueOrDefault(id)?.Seq ?? Next(), level, text)
        {
            Category = category,
            Label = label,
            Detail = detail,
            Sections = sections ?? [],
            ParentId = parent
        };
        Put(item);
        return item;
    }

    private void User(JsonElement message, string? parent)
    {
        if (!message.TryGetProperty("message", out var body) || !body.TryGetProperty("content", out var content)) return;
        var uuid = Str(message, "uuid");
        var texts = new List<string>();
        var attachments = new List<ChatAttachment>();
        var isString = content.ValueKind == JsonValueKind.String;
        if (isString) texts.Add(content.GetString() ?? "");
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                switch (Str(block, "type"))
                {
                    case "tool_result": ToolResult(block); break;
                    case "text": texts.Add(Str(block, "text") ?? ""); break;
                    case "image": attachments.Add(new ChatAttachment("image", "") { Kind = "image" }); break;
                    case "document": attachments.Add(new ChatAttachment(Str(block, "title") ?? "document", "") { Kind = "document" }); break;
                }
        // Subagent prompts and everything else of a subagent's own conversation stay in the data but are not rows here.
        if (parent is not null || Flag(message, "isSidechain")) return;
        var origin = OriginOf(message);
        var synthetic = message.TryGetProperty("isSynthetic", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.GetBoolean() : Flag(message, "is_meta") && origin is not null || Flag(message, "isMeta") && origin is not null;
        var agentSender = Str(message, "agentSender") ?? Str(message, "agent_sender");
        var cleaned = texts.Select(Sanitise).ToList();

        if (pendingCompaction is not null && synthetic && isString && cleaned.Count == 1 && items.GetValueOrDefault(pendingCompaction) is ChatNoticeItem compaction)
        {
            Put(compaction with { Detail = cleaned[0] });
            pendingCompaction = null;
            return;
        }

        var idBase = uuid ?? (sequence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (synthetic)
        {
            // An injected row survives only when it is a peer message; every other CLI injection is dropped.
            if (cleaned.Count == 1 && ParsePeer(cleaned[0], origin, agentSender) is { } peer) PutPeer($"m:{idBase}", peer);
            return;
        }

        var human = new StringBuilder();
        var emitted = 0;
        for (var index = 0; index < cleaned.Count; index++)
        {
            var text = cleaned[index];
            if (string.IsNullOrWhiteSpace(text)) continue;
            var trimmed = text.Trim();
            var id = $"m:{idBase}:{index}";
            if (trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("<ide_selection>", StringComparison.Ordinal) || trimmed.StartsWith("<ide_opened_file>", StringComparison.Ordinal))
            {
                if (IdeChip(trimmed) is { } chip) attachments.Add(chip);
                continue;
            }
            if (trimmed.StartsWith("<terminal ", StringComparison.Ordinal) || trimmed.StartsWith("<browser", StringComparison.Ordinal) || trimmed.StartsWith("<post-tool-use-hook>", StringComparison.Ordinal) && !trimmed.Contains("<ide_diagnostics>", StringComparison.Ordinal))
                continue;
            if (trimmed == "[Request interrupted by user]") { interruptPending = true; PutNotice(id, ChatNoticeLevel.Info, "Interrupted", ChatNoticeKinds.Interrupt); emitted++; continue; }
            if (trimmed == "[Request interrupted by user for tool use]") { PutNotice(id, ChatNoticeLevel.Info, "Tool interrupted", ChatNoticeKinds.Interrupt); emitted++; continue; }
            if (trimmed.StartsWith("<task-notification>", StringComparison.Ordinal) && TaskNotification(id, trimmed, origin)) { emitted++; continue; }
            if (ParsePeer(trimmed, origin, agentSender) is { } peer) { PutPeer(id, peer); emitted++; continue; }
            if (origin is null && Wrapped(id, trimmed)) { emitted++; continue; }
            if (trimmed.StartsWith("<local-command-stdout>", StringComparison.Ordinal) || trimmed.StartsWith("<local-command-stderr>", StringComparison.Ordinal)) { CommandResult(id, ForkedSkill().Replace(trimmed, "").Trim()); emitted++; continue; }
            if (trimmed.StartsWith("<bash-stdout>", StringComparison.Ordinal) || trimmed.StartsWith("<bash-stderr>", StringComparison.Ordinal)) { BashOutput(id, trimmed); emitted++; continue; }
            if (trimmed.StartsWith("<post-tool-use-hook>", StringComparison.Ordinal)) { PutNotice(id, ChatNoticeLevel.Warning, "IDE diagnostics", ChatNoticeKinds.Diagnostics, detail: Tag(trimmed, "ide_diagnostics")); emitted++; continue; }
            if (trimmed.StartsWith("<command-name>", StringComparison.Ordinal) && CommandRecord(id, trimmed)) { emitted++; continue; }
            if (human.Length > 0) human.Append('\n');
            human.Append(text);
        }

        var humanText = ForkedSkill().Replace(human.ToString(), "").Trim();
        if (origin is null or { Kind: "human" }) humanText = PastedToken().Replace(humanText, "");
        if (humanText.Length == 0 && attachments.Count == 0) return;
        if (humanText.Length == 0 && emitted > 0) return;
        var promptId = "u:" + (uuid ?? idBase);
        var shown = origin is { Kind: not "human" } ? origin : null;
        if (items.GetValueOrDefault(promptId) is ChatUserItem existing)
        {
            // The CLI's echo of a prompt SharpRail sent (or a replayed line): it only releases the held prompt.
            if (existing.Queued) Put(existing with { Queued = false });
            return;
        }
        MarkReplayedAnswers(humanText);
        var user = new ChatUserItem(promptId, Next(), humanText) { Attachments = attachments.Count > 0 ? attachments : null, Uuid = uuid, Origin = shown };
        Put(user);
    }

    // "Answering your earlier question ..." is what a question answered in text after a restart is sent as; it settles the question.
    private void MarkReplayedAnswers(string text)
    {
        if (!text.StartsWith("Answering your earlier question", StringComparison.Ordinal)) return;
        var question = items.Values.OfType<ChatQuestionItem>().LastOrDefault(item => item.Outcome is null);
        if (question is null) return;
        var answers = new Dictionary<string, string>();
        foreach (Match match in Regex.Matches(text, @"""(?<q>[^""]+)"":\s*(?<a>[^\n]+)"))
            answers[match.Groups["q"].Value] = match.Groups["a"].Value.Trim();
        Put(question with { Outcome = "answered", Decision = ChatPermissionDecision.Allowed, Answers = answers.Count > 0 ? answers : null });
    }

    private static ChatAttachment? IdeChip(string text)
    {
        var selection = Regex.Match(text, @"lines? (\d+)(?: to (\d+))? from (\S+?):?\s");
        if (text.StartsWith("<ide_selection>", StringComparison.Ordinal) && selection.Success)
            return new ChatAttachment(System.IO.Path.GetFileName(selection.Groups[3].Value), selection.Groups[3].Value)
            { Kind = "selection", StartLine = int.Parse(selection.Groups[1].Value), EndLine = selection.Groups[2].Success ? int.Parse(selection.Groups[2].Value) : int.Parse(selection.Groups[1].Value) };
        var opened = Regex.Match(text, @"opened the file (\S+)");
        return opened.Success ? new ChatAttachment(System.IO.Path.GetFileName(opened.Groups[1].Value), opened.Groups[1].Value) { Kind = "file" } : null;
    }

    private sealed record PeerMessage(string Label, string Preview, string Body, IReadOnlyList<ChatSection> Sections);

    // The wrapper the CLI puts around a message from another session: a lead-in line, one tag, the body and a fixed trailer.
    private static PeerMessage? ParsePeer(string text, ChatOrigin? origin, string? agentSender)
    {
        string? lead = PeerLeads.FirstOrDefault(candidate => text.StartsWith(candidate, StringComparison.Ordinal));
        var channelLead = lead is null ? ChannelLead().Match(text) : null;
        if (lead is null && channelLead is not { Success: true }) return null;
        var rest = text[(lead?.Length ?? channelLead!.Length)..];
        var match = PeerBody().Match(rest);
        if (!match.Success) return null;
        var tag = match.Groups["tag"].Value;
        var trailer = match.Groups["trailer"].Value.Trim();
        if (trailer.Length == 0 || !PeerTrailers.Any(marker => trailer.Contains(marker, StringComparison.Ordinal))) return null;
        if (match.Groups["body"].Value.Contains($"</{tag}>", StringComparison.Ordinal)) return null;
        var attrs = Attribute().Matches(match.Groups["attrs"].Value).ToDictionary(found => found.Groups["k"].Value, found => found.Groups["v"].Value);
        var channel = tag == "channel";
        var rawName = channel ? attrs.GetValueOrDefault("source") ?? "" : attrs.GetValueOrDefault("from") ?? "";
        var name = SanitiseName(rawName);
        // The recorded origin must agree with the shape; a mismatched one is an ordinary prompt.
        bool agrees = channel ? origin is { Kind: "channel" } : origin is { Kind: "peer" };
        string label;
        if (agentSender is not null) label = $"Message from {agentSender}";
        else if (origin is null) label = "Sender not recorded";
        else if (agrees) label = channel ? $"Message from channel @{(name.Length > 0 ? name : "peer")}" : $"Message from @{(name.Length > 0 ? name : "agent")}";
        else return null;
        var body = match.Groups["body"].Value;
        var sections = new List<ChatSection>();
        if (origin is null && agentSender is null)
        {
            sections.Add(new("Lead", (lead ?? channelLead!.Value).TrimEnd()));
            sections.Add(new("Message", body));
            sections.Add(new("Trailer", trailer));
        }
        else
        {
            if (name != rawName && rawName.Length > 0) sections.Add(new("From", rawName));
            foreach (var (key, value) in attrs.Where(pair => pair.Key is not ("from" or "source"))) sections.Add(new(key, value));
            sections.Add(new("Message", body));
        }
        return new PeerMessage(label, Preview(body), body, sections);
    }

    private static string SanitiseName(string name)
    {
        var kept = new string([.. name.Where(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '@' or ':' or '/' or ' ')]).Trim();
        return kept.Length > 64 ? kept[..63] + "…" : kept;
    }

    private void PutPeer(string id, PeerMessage peer) =>
        PutNotice(id, ChatNoticeLevel.Info, peer.Preview, ChatNoticeKinds.Peer, peer.Label, peer.Body, peer.Sections);

    private bool TaskNotification(string id, string text, ChatOrigin? origin)
    {
        if (text.Split("<task-notification>").Length != 2 || text.Split("</task-notification>").Length != 2) return false;
        var summary = Tag(text, "summary");
        if (summary.Length == 0 || origin is { Subkind: not null }) return false;
        if (origin is not null and not { Kind: "task-notification" }) return false;
        var sections = new List<ChatSection>();
        void Add(string label, string value) { if (value.Length > 0) sections.Add(new(label, value)); }
        Add("Task ID", Tag(text, "task-id"));
        Add("Tool use ID", Tag(text, "tool-use-id"));
        Add("Status", Tag(text, "status"));
        Add("Output file", Tag(text, "output-file"));
        Add("Summary", summary);
        Add("Result", Tag(text, "result"));
        var usage = text.Contains("<usage>", StringComparison.Ordinal) ? Tag(text, "usage") : text;
        Add("Tokens", Tag(usage, "subagent_tokens"));
        Add("Tool uses", Tag(usage, "tool_uses"));
        Add("Duration (ms)", Tag(usage, "duration_ms"));
        PutNotice(id, ChatNoticeLevel.Info, summary, ChatNoticeKinds.TaskNotification, sections: sections);
        return true;
    }

    private bool Wrapped(string id, string text)
    {
        var match = Wrapper().Match(text);
        if (!match.Success || !WrappedLabels.TryGetValue(match.Groups["tag"].Value, out var label)) return false;
        if (text.Split($"</{match.Groups["tag"].Value}>").Length != 2) return false;
        if (match.Groups["tag"].Value is "bash-stdout" or "bash-stderr") return false;
        PutNotice(id, ChatNoticeLevel.Info, label, ChatNoticeKinds.Wrapped, label, match.Groups["body"].Value.Trim());
        return true;
    }

    private void CommandResult(string id, string text)
    {
        var stdout = Tag(text, "local-command-stdout");
        var stderr = Tag(text, "local-command-stderr");
        var error = stderr.Length > 0 && stdout.Length == 0 || stderr.Length > 0;
        var shown = error ? stderr : stdout;
        if (shown.Length == 0) return;
        var block = error || shown.Contains('\n');
        var line = shown.StartsWith("Set model to ", StringComparison.Ordinal) ? "Switched to " + shown["Set model to ".Length..].Trim() : Preview(shown);
        PutNotice(id, error ? ChatNoticeLevel.Error : ChatNoticeLevel.Info, line, ChatNoticeKinds.CommandResult, detail: block ? shown : null);
    }

    private void BashOutput(string id, string text)
    {
        var sections = new List<ChatSection>();
        var stdout = Tag(text, "bash-stdout");
        var stderr = Tag(text, "bash-stderr");
        var exit = Tag(text, "bash-exit-code");
        var persisted = Tag(stdout, "persisted-output");
        if (persisted.Length > 0) stdout = persisted;
        if (stdout.Length > 0) sections.Add(new("Output", stdout));
        if (stderr.Length > 0) sections.Add(new("Error output", stderr));
        if (exit.Length > 0) sections.Add(new("Exit code", exit));
        PutNotice(id, stderr.Length > 0 ? ChatNoticeLevel.Warning : ChatNoticeLevel.Info, "Terminal command output", ChatNoticeKinds.BashOutput, sections: sections);
    }

    // A slash command's record: /name args, with the parts it carried as an expandable detail.
    private bool CommandRecord(string id, string text)
    {
        var name = Tag(text, "command-name");
        if (name.Length == 0) return false;
        var args = Tag(text, "command-args");
        var sections = new List<ChatSection> { new("Command", name) };
        if (Tag(text, "command-message") is { Length: > 0 } message) sections.Add(new("Command message", message));
        if (Tag(text, "command-contents") is { Length: > 0 } contents) sections.Add(new("Command contents", contents));
        PutNotice(id, ChatNoticeLevel.Info, args.Length > 0 ? $"{name} {args}" : name, ChatNoticeKinds.Command, sections: sections.Count > 1 ? sections : null);
        return true;
    }
}