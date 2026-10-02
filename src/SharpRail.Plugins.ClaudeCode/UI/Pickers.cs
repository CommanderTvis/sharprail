using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>How a picker drive reaches the terminal: write keys, read the buffer's tail, wait.</summary>
/// <param name="Write">Types into the terminal.</param>
/// <param name="ReadLines">The buffer's last lines; with <c>true</c>, faint text (a placeholder or suggestion) left out.</param>
/// <param name="Delay">Waits between keystrokes and polls.</param>
internal sealed record PickerIo(Action<string> Write, Func<bool, IReadOnlyList<string>> ReadLines, Func<TimeSpan, Task> Delay);

internal enum PickerOutcome { Switched, NoPicker, NotFound, Draft }

/// <summary>
/// Drives Claude Code's own <c>/model</c> picker to a session-only switch: refuse over a typed draft, open the picker,
/// walk the highlight to the row naming the model, then press <c>s</c>, never Enter or a digit, both of which would
/// overwrite the user's saved default.
/// </summary>
internal static partial class ModelPicker
{
    private static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);
    private const int OpenPolls = 24;
    private const int MovePolls = 8;
    private const int MaxSteps = 12;
    private const int ConfirmPolls = 8;

    [GeneratedRegex(@"^\s*❯\s*\d+\.\s+(.+?)(?:\s{2}.*)?$")]
    private static partial Regex HighlightedRow();

    [GeneratedRegex(@"^\s*❯\s?(.*)$")]
    private static partial Regex ComposerLine();

    // What Claude Code draws in an empty composer: a suggestion, its queued-message hints, or the agent being messaged.
    [GeneratedRegex("^(?:Try \"|Press (?:up|Enter) to (?:edit|select) |Message @.+…$)")]
    private static partial Regex ComposerPlaceholder();

    [GeneratedRegex(@"Switch model\?|Change effort level\?")]
    private static partial Regex ConfirmPrompt();

    /// <summary>What the user has typed at Claude Code's prompt, read off its composer line; null when it is empty.</summary>
    public static string? ComposerDraft(IReadOnlyList<string> lines)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var line = lines[index];
            if (HighlightedRow().IsMatch(line)) return null;
            if (ComposerLine().Match(line) is not { Success: true } match) continue;
            var text = match.Groups[1].Value.Trim();
            return text.Length > 0 && !ComposerPlaceholder().IsMatch(text) ? text : null;
        }
        return null;
    }

    public static string? PickerHighlight(IReadOnlyList<string> lines)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
            if (HighlightedRow().Match(lines[index]) is { Success: true } match)
                return match.Groups[1].Value.Replace("✔", "", StringComparison.Ordinal).Trim();
        return null;
    }

    /// <summary>
    /// Switching a cached conversation asks first ("Switch model? 1. Yes … 2. No, go back"). The user already answered by
    /// picking from the chip's menu, so the drive answers it rather than leave a question nothing will press.
    /// </summary>
    public static string? ConfirmationChoice(IReadOnlyList<string> lines)
    {
        if (!lines.Any(line => ConfirmPrompt().IsMatch(line))) return null;
        if (PickerHighlight(lines) is not { } label) return null;
        return label.StartsWith("yes", StringComparison.OrdinalIgnoreCase) && (label.Length == 3 || !char.IsLetterOrDigit(label[3])) ? "yes" : "no";
    }

    public static bool HighlightNamesModel(string? label, string model)
    {
        if (label is null) return false;
        var name = label.ToLowerInvariant();
        var id = model.ToLowerInvariant();
        return name == id || name.StartsWith(id + " (", StringComparison.Ordinal) || Regex.IsMatch(name, $@"^{Regex.Escape(id)} \d[\d.]*( \(|$)");
    }

    public static async Task AnswerConfirmationAsync(PickerIo io)
    {
        for (var poll = 0; poll < ConfirmPolls; poll++)
        {
            await io.Delay(Poll);
            if (ConfirmationChoice(io.ReadLines(false)) is not { } choice) continue;
            // The highlight opens on "Yes"; move to it if it did not, then take it.
            if (choice == "no") io.Write("\u001b[A");
            io.Write("\r");
            await io.Delay(Poll);
            return;
        }
    }

    public static async Task<PickerOutcome> DriveAsync(PickerIo io, string model)
    {
        // A slash command only opens the picker at the start of an empty line.
        if (ComposerDraft(io.ReadLines(true)) is not null) return PickerOutcome.Draft;
        io.Write("/model");
        await io.Delay(EnterDelay);
        io.Write("\r");
        string? label = null;
        for (var poll = 0; poll < OpenPolls && label is null; poll++)
        {
            await io.Delay(Poll);
            label = PickerHighlight(io.ReadLines(false));
        }
        if (label is null)
        {
            io.Write("\u001b");
            return PickerOutcome.NoPicker;
        }
        for (var step = 0; step < MaxSteps; step++)
        {
            if (HighlightNamesModel(label, model))
            {
                io.Write("s");
                await AnswerConfirmationAsync(io);
                return PickerOutcome.Switched;
            }
            var before = label;
            io.Write("\u001b[B");
            for (var poll = 0; poll < MovePolls && label == before; poll++)
            {
                await io.Delay(Poll);
                label = PickerHighlight(io.ReadLines(false));
            }
        }
        io.Write("\u001b");
        return PickerOutcome.NotFound;
    }
}

/// <summary>
/// Drives <c>/effort</c> to a level for this session only: the same shape as the model picker, but the slider is steered
/// with left and right by distance. The current rung is read from the <c>▲</c> the slider draws under it, the one mark of
/// the selection a plain-text buffer keeps.
/// </summary>
internal static class EffortPicker
{
    /// <summary>The rungs the slider can draw, in order; not every environment shows all of them.</summary>
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high", "xhigh", "max", "ultracode"];

    private static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(250);
    private const int OpenPolls = 24;
    private const int MovePolls = 8;
    private const int MaxSteps = 8;
    private const char Marker = '▲';
    // Below this many labels found on a line, it is too little to trust as the slider's track.
    private const int MinLabels = 2;

    // Where each level's name sits on the labels line, by its middle column; in order, so high is not found inside xhigh.
    private static Dictionary<string, double> LabelCenters(string line)
    {
        var centers = new Dictionary<string, double>();
        var cursor = 0;
        foreach (var level in Levels)
        {
            var at = line.IndexOf(level, cursor, StringComparison.Ordinal);
            if (at == -1) continue;
            centers[level] = at + level.Length / 2.0;
            cursor = at + level.Length;
        }
        return centers;
    }

    public static string? Highlight(IReadOnlyList<string> lines)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var marker = lines[index].IndexOf(Marker);
            if (marker == -1) continue;
            var centers = LabelCenters(index + 1 < lines.Count ? lines[index + 1] : "");
            if (centers.Count < MinLabels) continue;
            return centers.MinBy(center => Math.Abs(center.Value - marker)).Key;
        }
        return null;
    }

    public static async Task<PickerOutcome> DriveAsync(PickerIo io, string level)
    {
        var target = Levels.ToList().IndexOf(level);
        if (target == -1) return PickerOutcome.NotFound;
        if (ModelPicker.ComposerDraft(io.ReadLines(true)) is not null) return PickerOutcome.Draft;
        io.Write("/effort");
        await io.Delay(EnterDelay);
        io.Write("\r");
        string? current = null;
        for (var poll = 0; poll < OpenPolls && current is null; poll++)
        {
            await io.Delay(ModelPicker.Poll);
            current = Highlight(io.ReadLines(false));
        }
        if (current is null)
        {
            io.Write("\u001b");
            return PickerOutcome.NoPicker;
        }
        for (var step = 0; step < MaxSteps; step++)
        {
            var at = Levels.ToList().IndexOf(current);
            if (at == target)
            {
                io.Write("s");
                await ModelPicker.AnswerConfirmationAsync(io);
                return PickerOutcome.Switched;
            }
            var before = current;
            io.Write(at < target ? "\u001b[C" : "\u001b[D");
            for (var poll = 0; poll < MovePolls && current == before; poll++)
            {
                await io.Delay(ModelPicker.Poll);
                current = Highlight(io.ReadLines(false)) ?? current;
            }
            // The slider clamps at its ends: a move that changed nothing is not going to start.
            if (current == before) break;
        }
        io.Write("\u001b");
        return PickerOutcome.NotFound;
    }
}