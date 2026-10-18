using System.Text.RegularExpressions;

using SharpRail.Plugins.Agent.UI;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>How a picker drive reaches the terminal: write keys, read the buffer's tail, wait.</summary>
/// <param name="Write">Types into the terminal.</param>
/// <param name="ReadLines">The buffer's last lines; with <c>true</c>, faint text (a placeholder or suggestion) left out.</param>
/// <param name="Delay">Waits between keystrokes and polls.</param>
/// <param name="Hold">Holds the user's keys out of the terminal until the handle is disposed; null holds nothing.</param>
internal sealed record PickerIo(Action<string> Write, Func<bool, IReadOnlyList<string>> ReadLines, Func<TimeSpan, Task> Delay, Func<IDisposable>? Hold = null)
{
    /// <summary>Holds the user's keys out of the terminal for a driven entry; dispose it when the entry is done or fails.</summary>
    public IDisposable HoldInput() => Hold?.Invoke() ?? NoHold.Instance;

    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();
        public void Dispose() { }
    }
}

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
    private const int MaxSteps = 128;
    private const int ConfirmPolls = 8;

    [GeneratedRegex(@"^\s*❯\s*(\d+)\.\s+(.+?)(?:\s{2}.*)?$")]
    private static partial Regex HighlightedRow();

    // What only Claude Code draws in an empty prompt, beside the hints every agent shares (PromptComposer.IsHint): the
    // agent being messaged. Hints are drawn faint, so a renderer that sees cell styling never gets this far.
    [GeneratedRegex(@"^Message @.+(?:…|\.\.\.)$")]
    private static partial Regex ComposerPlaceholder();

    [GeneratedRegex(@"Switch model\?|Change effort level\?")]
    private static partial Regex ConfirmPrompt();

    /// <summary>What the user has typed at Claude Code's prompt, read off its composer line; null when it is empty or shows a hint.</summary>
    public static string? ComposerDraft(IReadOnlyList<string> lines) =>
        PromptComposer.Draft(lines, "❯", HighlightedRow(), text => ComposerPlaceholder().IsMatch(text));

    public static string? PickerHighlight(IReadOnlyList<string> lines)
    {
        for (var index = lines.Count - 1; index >= 0; index--)
            if (HighlightedRow().Match(lines[index]) is { Success: true } match)
                return CleanLabel(match.Groups[2].Value);
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

    /// <summary>Reads the live session's complete picker without changing its model or saved default.</summary>
    public static async Task<(PickerOutcome Outcome, IReadOnlyList<string> Models)> DiscoverAsync(PickerIo io)
    {
        using var hold = io.HoldInput();
        if (ComposerDraft(io.ReadLines(true)) is not null) return (PickerOutcome.Draft, []);
        io.Write("/model");
        await io.Delay(EnterDelay);
        io.Write("\r");
        try
        {
            string? label = null;
            for (var poll = 0; poll < OpenPolls && label is null; poll++)
            {
                await io.Delay(Poll);
                label = PickerHighlight(io.ReadLines(false));
            }
            if (label is null) return (PickerOutcome.NoPicker, []);
            var rows = new Dictionary<int, string>();
            foreach (var direction in new[] { "\u001b[A", "\u001b[B" })
            {
                var seen = new HashSet<int>();
                for (var step = 0; step < MaxSteps; step++)
                {
                    var selected = io.ReadLines(false).Select(line => HighlightedRow().Match(line)).LastOrDefault(row => row.Success);
                    if (selected is null) return (PickerOutcome.NoPicker, []);
                    var number = int.Parse(selected.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!seen.Add(number)) break;
                    rows[number] = CleanLabel(selected.Groups[2].Value);
                    var before = label;
                    io.Write(direction);
                    for (var poll = 0; poll < MovePolls && label == before; poll++)
                    {
                        await io.Delay(Poll);
                        label = PickerHighlight(io.ReadLines(false));
                    }
                    if (label == before) break;
                    if (step == MaxSteps - 1) return (PickerOutcome.NotFound, []);
                }
            }
            if (rows.Count > 0 && rows.Keys.Min() == 1 && rows.Keys.Max() == rows.Count)
                return (PickerOutcome.Switched, rows.OrderBy(row => row.Key).Select(row => row.Value).ToArray());
            return (PickerOutcome.NotFound, []);
        }
        finally { io.Write("\u001b"); }
    }

    private static string CleanLabel(string label) => label.Replace("✔", "", StringComparison.Ordinal).Replace("✓", "", StringComparison.Ordinal).Trim();

    public static async Task<PickerOutcome> DriveAsync(PickerIo io, string model)
    {
        using var hold = io.HoldInput();
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
        var direction = "\u001b[B";
        var visited = new HashSet<string>();
        for (var step = 0; step < MaxSteps * 2; step++)
        {
            if (label is null || !visited.Add(label)) break;
            if (HighlightNamesModel(label, model))
            {
                io.Write("s");
                await AnswerConfirmationAsync(io);
                return PickerOutcome.Switched;
            }
            var before = label;
            io.Write(direction);
            for (var poll = 0; poll < MovePolls && label == before; poll++)
            {
                await io.Delay(Poll);
                label = PickerHighlight(io.ReadLines(false));
            }
            if (label == before)
            {
                if (direction == "\u001b[A") break;
                direction = "\u001b[A";
                visited.Clear();
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
        using var hold = io.HoldInput();
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