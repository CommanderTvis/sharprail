namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

/// <summary>
/// Heights guessed from a row's content before it has been laid out, after ThinkRail's rowHeightEstimates: lines of prose at a
/// column count, code at a line height, a gap per block, clamped. The list panel measures what it realizes; these guesses are for what it
/// cannot yet (scrolling to a far row, and the drift check that holds the guess near the measured height).
/// </summary>
internal static class ChatRowEstimates
{
    private const double Min = 20, Max = 20_000, ProseLine = 20, CodeLine = 18, Gap = 8;
    private const double Pad = 24;

    public static double Estimate(ChatRow row, double width)
    {
        // A row that draws nothing takes no room, so replacing it never moves the rows around it.
        if (ChatRowLayout.IsEmpty(row.Item)) return 0;
        var columns = Math.Max(20, (width - 2 * ChatRowLayout.Pad - row.Depth * ChatRowLayout.Indent) / 7.5);
        double height = row.Item switch
        {
            ChatUserItem user => ChatRowLayout.IsEmpty(user) ? 0 : 16 + Lines(user.Text, columns - 4, row.Expanded ? int.MaxValue : 3) * ProseLine + (user.Queued ? 16 : 0) + (user.Attachments is { Count: > 0 } ? 18 : 0),
            ChatAssistantItem assistant => Markdown(assistant.Text, columns),
            ChatThinkingItem thinking => row.Expanded ? 24 + Lines(thinking.Text, columns, int.MaxValue) * 17 : 26,
            ChatToolItem tool => 28 + (row.Expanded ? 8 + Lines(tool.Output ?? "", columns, 40) * CodeLine : 0),
            ChatActivityItem activity => 28 + (row.Expanded ? activity.Steps.Count * 17 : 0),
            ChatPermissionItem => 150,
            ChatNoticeItem notice => 8 + Lines(notice.Text, columns, int.MaxValue) * 17,
            _ => 30
        };
        return Math.Clamp(Math.Ceiling(height), Min, Max);
    }

    private static int Lines(string text, double columns, int cap)
    {
        var lines = 0;
        foreach (var line in text.Split('\n')) lines += Math.Max(1, (int)Math.Ceiling(line.Length / columns));
        return Math.Min(lines, cap);
    }

    // Prose is wrapped lines; a fenced block is its lines at the code height; each block adds a gap.
    private static double Markdown(string text, double columns)
    {
        double height = 8;
        var code = false;
        var codeLines = 0;
        var blank = true;
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (code) { height += Math.Max(1, codeLines) * CodeLine + 20; codeLines = 0; }
                code = !code;
                blank = false;
                continue;
            }
            if (code) { codeLines++; continue; }
            if (line.Trim().Length == 0) { blank = true; continue; }
            if (blank && height > 8) height += Gap;
            blank = false;
            height += Math.Max(1, (int)Math.Ceiling(line.Length / columns)) * ProseLine;
        }
        if (code) height += Math.Max(1, codeLines) * CodeLine + 20;
        return height + Pad / 2;
    }
}