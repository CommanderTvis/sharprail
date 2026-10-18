using System.Text.RegularExpressions;

using SharpRail.Plugins.Agent.UI;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>How the terminal accessory drives Codex: what it types, how long it waits, and what the screen shows.</summary>
public interface ICodexTerminalIo
{
    void Write(string data);
    Task DelayAsync(TimeSpan delay);
    /// <summary>The screen's tail; <paramref name="omitFaint"/> drops a placeholder or suggestion, drawn faint.</summary>
    IReadOnlyList<string> ReadLines(bool omitFaint = false);
    /// <summary>Holds the user's keys out of the terminal until disposed, so a driven entry is not interleaved with them.</summary>
    IDisposable HoldInput();
}

public enum CodexModelPickerOutcome
{
    Switched,
    NoPicker,
    NotFound,
    NoSessionKey,
    Draft
}

/// <summary>
/// Submits slash commands and drives Codex's <c>/model</c> picker for this session only: a row is taken with <c>s</c>,
/// never Enter, which would save the choice as the default in config.toml. See this package's SPEC.md.
/// </summary>
public static partial class CodexModelPicker
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);
    private const int OpenPolls = 24;
    private const int MovePolls = 8;
    private const int MaxSteps = 16;
    private const string AllModels = "All models";
    // What Codex's empty composer shows (its example prompts and the follow-up line), drawn faint. A renderer that sees
    // cell styling never reads them; the rest recognise them by wording, so an unlisted new example is the one gap.
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ask Codex to do anything", "Ask a follow-up question", "Implement {feature}", "Find and fix a bug in @filename",
        "Explain this codebase", "Summarize recent commits", "Use /skills to list available skills", "Write tests for @filename",
        "Improve documentation in @filename", "Run /review on my current changes"
    };

    /// <summary>Writes the command, waits for Codex's paste-burst handling to settle, then writes Enter separately.</summary>
    public static async Task SubmitAsync(ICodexTerminalIo io, string command)
    {
        using var hold = io.HoldInput();
        io.Write(command);
        await io.DelayAsync(TimeSpan.FromMilliseconds(250));
        io.Write("\r");
    }

    /// <summary>What the user has typed at Codex's composer; null when it is empty or showing a placeholder or hint.</summary>
    public static string? ComposerDraft(IReadOnlyList<string> lines) =>
        PromptComposer.Draft(lines, "›", HighlightedRow(), text => Placeholders.Contains(text.TrimEnd('…', '.')) || Placeholders.Contains(text));

    public static string? PickerHighlight(IReadOnlyList<string> lines)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
            if (HighlightedRow().Match(lines[index]) is { Success: true } match) return match.Groups[1].Value.Trim();
        return null;
    }

    /// <summary>Codex shows a model's display name (<c>GPT-5.6 Luna</c>); its id is the slug.</summary>
    public static bool HighlightNamesModel(string? label, string model) =>
        label is not null && Whitespace().Replace(label.ToLowerInvariant(), "-") == model.ToLowerInvariant();

    private static async Task<string?> SettleAsync(ICodexTerminalIo io, string? before)
    {
        var label = before;
        for (var poll = 0; poll < MovePolls && label == before; poll++)
        {
            await io.DelayAsync(Poll);
            label = PickerHighlight(io.ReadLines());
        }
        return label;
    }

    private static async Task<bool> ClosedAsync(ICodexTerminalIo io)
    {
        for (var poll = 0; poll < MovePolls; poll++)
        {
            await io.DelayAsync(Poll);
            if (PickerHighlight(io.ReadLines()) is null) return true;
        }
        return false;
    }

    private static async Task<bool> EffortOpenedAsync(ICodexTerminalIo io)
    {
        for (var poll = 0; poll < MovePolls; poll++)
        {
            await io.DelayAsync(Poll);
            if (io.ReadLines().Any(line => line.Contains("Select Reasoning Level", StringComparison.Ordinal))) return true;
        }
        return false;
    }

    // A model with a choice of efforts has no session action on its own row: Enter opens its effort list, where the
    // highlighted (current or default) effort takes the s.
    private static async Task<bool> AcceptForSessionAsync(ICodexTerminalIo io)
    {
        io.Write("s");
        if (await ClosedAsync(io)) return true;
        io.Write("\r");
        if (!await EffortOpenedAsync(io)) return false;
        io.Write("s");
        return await ClosedAsync(io);
    }

    public static async Task<CodexModelPickerOutcome> DriveAsync(ICodexTerminalIo io, string model)
    {
        using var hold = io.HoldInput();
        if (ComposerDraft(io.ReadLines(omitFaint: true)) is not null) return CodexModelPickerOutcome.Draft;
        await SubmitAsync(io, "/model");
        string? label = null;
        for (var poll = 0; poll < OpenPolls && label is null; poll++)
        {
            await io.DelayAsync(Poll);
            label = PickerHighlight(io.ReadLines());
        }
        if (label is null)
        {
            io.Write("\x1b");
            return CodexModelPickerOutcome.NoPicker;
        }
        var outcome = CodexModelPickerOutcome.NotFound;
        for (var step = 0; step < MaxSteps; step++)
        {
            if (HighlightNamesModel(label, model))
            {
                if (await AcceptForSessionAsync(io)) return CodexModelPickerOutcome.Switched;
                outcome = CodexModelPickerOutcome.NoSessionKey;
                break;
            }
            var before = label;
            io.Write(label == AllModels ? "\r" : "\x1b[B");
            label = await SettleAsync(io, before);
        }
        for (var depth = 0; depth < 3 && PickerHighlight(io.ReadLines()) is not null; depth++)
        {
            io.Write("\x1b");
            await io.DelayAsync(Poll);
        }
        return outcome;
    }

    [GeneratedRegex(@"^\s*›\s+\d+\.\s+(.+?)(?:\s\((?:current|default)\))?(?:\s{2,}.*)?\s*$")]
    private static partial Regex HighlightedRow();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}