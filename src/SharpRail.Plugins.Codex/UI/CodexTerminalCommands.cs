using System.Text.RegularExpressions;

using SharpRail.Plugins.Agent.UI;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>How the terminal accessory types a slash command into Codex: what it writes, how long it waits, and a hold on the user's keys.</summary>
public interface ICodexTerminalIo
{
    void Write(string data);
    Task DelayAsync(TimeSpan delay);
    /// <summary>Holds the user's keys out of the terminal until disposed, so a typed command is not interleaved with them.</summary>
    IDisposable HoldInput();
}

/// <summary>Submits slash commands to Codex. It never drives a picker: the user chooses inside Codex's own UI.</summary>
public static class CodexTerminalCommands
{
    private static readonly Regex PickerRow = new(@"^\s*›\s+\d+\.\s+", RegexOptions.CultureInvariant);

    // What Codex's empty composer shows (its example prompts and the follow-up line), drawn faint. A renderer that sees
    // cell styling never reads them; the rest recognise them by wording, so an unlisted new example is the one gap.
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ask Codex to do anything", "Ask a follow-up question", "Implement {feature}", "Find and fix a bug in @filename",
        "Explain this codebase", "Summarize recent commits", "Use /skills to list available skills", "Write tests for @filename",
        "Improve documentation in @filename", "Run /review on my current changes"
    };

    /// <summary>What Codex's composer shows on these screen rows (prompt glyph <c>›</c>).</summary>
    public static PromptState Prompt(IReadOnlyList<string> lines) =>
        PromptGuard.Classify(lines, "›", PickerRow, text => Placeholders.Contains(text.TrimEnd('…', '.')) || Placeholders.Contains(text));

    /// <summary>Writes the command, waits for Codex's paste-burst handling to settle, then writes Enter separately.</summary>
    public static async Task SubmitAsync(ICodexTerminalIo io, string command)
    {
        using var hold = io.HoldInput();
        io.Write(command);
        await io.DelayAsync(TimeSpan.FromMilliseconds(250));
        io.Write("\r");
    }
}