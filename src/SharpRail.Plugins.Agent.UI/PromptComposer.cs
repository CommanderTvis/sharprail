using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Agent.UI;

/// <summary>
/// Reads what the user has typed at an agent's prompt off the terminal's text, for the guards that must not drive a slash
/// command over a half-typed message. A prompt that is empty shows hint text in its place (a placeholder, a suggestion,
/// the queued-message keys), so the text after the prompt glyph alone does not say whether anything was typed.
/// </summary>
public static partial class PromptComposer
{
    private static readonly ConcurrentDictionary<string, Regex> Lines = new();

    // What every agent's empty prompt may say: a suggestion, or the keys that act on the prompt in its current state.
    // Wording is matched by family (a key to press, a command to type) rather than whole sentences, so a new hint of an
    // existing kind is recognised without a release.
    [GeneratedRegex(
        """^(?:Try\s+["“'‘]|(?:Press|Hit)\s+(?:up|down|left|right|enter|return|esc(?:ape)?|tab|space|↑|↓|←|→|⏎|ctrl[+\-]\w+|shift[+\-]\w+|alt[+\-]\w+|opt(?:ion)?[+\-]\w+|cmd[+\-]\w+|[⌃⌥⇧⌘]\S+)(?:\s+(?:again|twice))?(?:\s+or\s+\S+)?\s+(?:to|for|and)\b|\?\s+for\s+shortcuts|/\s+for\s+commands|(?:Esc|Enter|Tab|Shift\+Tab|Ctrl\+\w+)\s+to\s+\w+|Type\s+(?:a\s+message|to\s+queue|/|@))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyHint();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Whether <paramref name="text"/> reads as an agent's hint for an empty prompt rather than something typed.</summary>
    /// <param name="text">The text after the prompt glyph.</param>
    /// <returns>True for a hint of a kind every agent shows.</returns>
    public static bool IsHint(string text) => KeyHint().IsMatch(Clean(text));

    /// <summary>
    /// What the user has typed at the prompt: the text after the last prompt glyph line, null when the prompt is empty,
    /// shows a hint, or a picker is open instead. Only the glyph line is read, so a multi-line draft is reported by its
    /// first line. Callers pass lines with faint cells already blanked where the renderer can (see
    /// <c>ITerminalAccessoryApi.BufferTail</c>); this also recognises the hints by wording, for the renderers that cannot.
    /// </summary>
    /// <param name="lines">The terminal's last lines, oldest first.</param>
    /// <param name="glyph">The agent's prompt glyph.</param>
    /// <param name="pickerRow">Matches a picker's highlighted row, which means the prompt is not in front.</param>
    /// <param name="isPlaceholder">Whether the agent's own wording is one of its placeholders.</param>
    /// <returns>The typed text, or null.</returns>
    public static string? Draft(IReadOnlyList<string> lines, string glyph, Regex pickerRow, Func<string, bool> isPlaceholder)
    {
        var line = Lines.GetOrAdd(glyph, g => new Regex(@"^\s*[│┃|]?\s*" + Regex.Escape(g) + @"[\s ]?(.*)$", RegexOptions.CultureInvariant));
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (pickerRow.IsMatch(lines[index])) return null;
            if (line.Match(lines[index]) is not { Success: true } match) continue;
            var text = Clean(match.Groups[1].Value);
            return text.Length > 0 && !KeyHint().IsMatch(text) && !isPlaceholder(text) ? text : null;
        }
        return null;
    }

    // Whitespace of any kind (a no-break space after the glyph, padding to a box edge) collapsed, and the box's right edge dropped.
    private static string Clean(string text) =>
        Whitespace().Replace(text.TrimEnd().TrimEnd('│', '┃', '|'), " ").Trim();
}