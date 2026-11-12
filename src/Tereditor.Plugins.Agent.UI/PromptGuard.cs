using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Agent.UI;

/// <summary>What the agent's prompt box shows.</summary>
public enum PromptState
{
    /// <summary>The prompt box is on screen and holds nothing but a placeholder.</summary>
    Empty,

    /// <summary>The prompt box holds text the user typed.</summary>
    Draft,

    /// <summary>No prompt box is recognisable: an unknown screen, an open picker or dialog.</summary>
    NotAtPrompt
}

/// <summary>
/// Decides whether a chip may type a slash command into an agent: only when its prompt box is visible and empty. The
/// screen read is bounded to the bottom rows, faint placeholder cells omitted where the renderer can blank them, and
/// the parser is a pure function over that text so it is checked against fake screens.
/// </summary>
public static partial class PromptGuard
{
    /// <summary>How many of the screen's bottom rows are read.</summary>
    public const int TailLines = 24;

    /// <summary>Shown when the prompt holds a draft.</summary>
    public const string DraftNotice = "Clear the prompt first: it holds a draft.";

    /// <summary>Shown when no prompt box is recognised.</summary>
    public const string NotAtPromptNotice = "Not at the prompt.";

    // What every agent's empty prompt may say: a suggestion, or the keys that act on the prompt in its current state.
    [GeneratedRegex(
        """^(?:Try\s+["“'‘]|(?:Press|Hit)\s+(?:up|down|left|right|enter|return|esc(?:ape)?|tab|space|↑|↓|←|→|⏎|ctrl[+\-]\w+|shift[+\-]\w+|alt[+\-]\w+|opt(?:ion)?[+\-]\w+|cmd[+\-]\w+|[⌃⌥⇧⌘]\S+)(?:\s+(?:again|twice))?(?:\s+or\s+\S+)?\s+(?:to|for|and)\b|\?\s+for\s+shortcuts|/\s+for\s+commands|(?:Esc|Enter|Tab|Shift\+Tab|Ctrl\+\w+)\s+to\s+\w+|Type\s+(?:a\s+message|to\s+queue|/|@))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyHint();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Whitespace of any kind collapsed, and the box's right edge dropped.
    private static string Clean(string text) => Whitespace().Replace(text.TrimEnd().TrimEnd('│', '┃', '|'), " ").Trim();

    private static bool IsBorder(string line) => line.Count(character => character == '─') >= 4;

    /// <summary>Classifies the bottom rows of the screen.</summary>
    /// <param name="lines">The screen's last rows, oldest first.</param>
    /// <param name="glyph">The agent's prompt glyph.</param>
    /// <param name="pickerRow">Matches an open picker's highlighted row, which means the prompt is not in front.</param>
    /// <param name="isPlaceholder">Whether the agent's own wording is one of its placeholders.</param>
    /// <returns>The state; the first prompt line from the bottom is the one read, plus its continuation lines down to the box's bottom border.</returns>
    public static PromptState Classify(IReadOnlyList<string> lines, string glyph, Regex pickerRow, Func<string, bool> isPlaceholder)
    {
        var prompt = new Regex(@"^\s*[│┃|]?\s*" + Regex.Escape(glyph) + @"[\s ]?(.*)$", RegexOptions.CultureInvariant);
        bool Typed(string text) => text.Length > 0 && !KeyHint().IsMatch(text) && !isPlaceholder(text);
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (pickerRow.IsMatch(lines[index])) return PromptState.NotAtPrompt;
            if (prompt.Match(lines[index]) is not { Success: true } match) continue;
            if (Typed(Clean(match.Groups[1].Value))) return PromptState.Draft;
            // Continuation lines of a multi-line draft count only inside the box, that is when its bottom border follows.
            var below = new List<string>();
            for (var next = index + 1; next < lines.Count; next++)
            {
                if (IsBorder(lines[next])) return below.Any(line => Typed(line)) ? PromptState.Draft : PromptState.Empty;
                below.Add(Clean(lines[next]));
            }
            return PromptState.Empty;
        }
        return PromptState.NotAtPrompt;
    }

    /// <summary>
    /// Reads the screen off the UI thread and says whether a command may be typed. Call it from the click handler and
    /// await it; after the await it is back on the caller's thread, where it checks the tab is still the same.
    /// </summary>
    /// <param name="readTail">Reads that many bottom rows of the terminal's screen, off the UI thread (<c>ITerminalAccessoryApi.ReadScreenTailAsync</c>).</param>
    /// <param name="classify">The agent's classifier over the screen tail.</param>
    /// <param name="stillCurrent">Whether the session and tab are still the ones that were clicked.</param>
    /// <param name="refuse">Receives the notice when typing is refused.</param>
    /// <returns>True when the prompt is empty and the caller may type.</returns>
    public static async Task<bool> MayTypeAsync(Func<int, Task<IReadOnlyList<string>>> readTail, Func<IReadOnlyList<string>, PromptState> classify, Func<bool> stillCurrent, Action<string> refuse)
    {
        PromptState state;
        try { state = classify(await readTail(TailLines)); }
        catch (Exception error) when (error is not OperationCanceledException) { state = PromptState.NotAtPrompt; }
        if (!stillCurrent()) return false;
        if (state == PromptState.Empty) return true;
        refuse(state == PromptState.Draft ? DraftNotice : NotAtPromptNotice);
        return false;
    }
}