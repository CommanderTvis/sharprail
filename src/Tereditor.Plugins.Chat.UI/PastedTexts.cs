using System.Text.RegularExpressions;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>
/// The big pastes of one draft. A paste over <see cref="MaxLines"/> lines or <see cref="MaxChars"/> characters goes into the box as one
/// token, <c>[Pasted text #N +K lines]</c>, and its text is kept here; a send replaces every token by its text. Only a token that is exactly
/// what <see cref="Add"/> returned counts: one edited by hand is plain text.
/// </summary>
internal sealed partial class PastedTexts
{
    internal const int MaxLines = 10, MaxChars = 1000;

    [GeneratedRegex(@"\[Pasted text #(?<n>\d+) \+\d+ lines?\]")]
    private static partial Regex Pattern();

    private readonly Dictionary<int, string> texts = [];
    private int last;

    /// <summary>The lines of a paste; one line break at the very end does not start another.</summary>
    internal static int Lines(string text)
    {
        var normal = text.ReplaceLineEndings("\n");
        if (normal.EndsWith('\n')) normal = normal[..^1];
        return normal.Count(c => c == '\n') + 1;
    }

    /// <summary>Whether a paste of <paramref name="text"/> collapses to a token.</summary>
    internal static bool Collapses(string text) => text.Length > MaxChars || Lines(text) > MaxLines;

    internal static string Token(int number, int lines) => $"[Pasted text #{number} +{lines} {(lines == 1 ? "line" : "lines")}]";

    /// <summary>Keeps <paramref name="text"/> and returns the token that stands for it; tokens are numbered from 1 per draft.</summary>
    internal string Add(string text)
    {
        texts[++last] = text;
        return Token(last, Lines(text));
    }

    internal string? Text(int number) => texts.GetValueOrDefault(number);

    /// <summary>The live tokens in <paramref name="text"/>, in order: [Start, End) and the paste each stands for.</summary>
    internal IEnumerable<(int Start, int End, int Number)> Tokens(string text)
    {
        if (texts.Count == 0) yield break;
        foreach (Match match in Pattern().Matches(text))
            if (Known(match) is { } number) yield return (match.Index, match.Index + match.Length, number);
    }

    /// <summary>The live token whose span holds <paramref name="index"/>, its edges included.</summary>
    internal (int Start, int End, int Number)? At(string text, int index)
    {
        foreach (var token in Tokens(text))
            if (token.Start <= index && index <= token.End) return token;
        return null;
    }

    /// <summary><paramref name="text"/> with every live token replaced by its paste: what is sent.</summary>
    internal string Expand(string text) => texts.Count == 0 ? text : Pattern().Replace(text, match => Known(match) is { } number ? texts[number] : match.Value);

    private int? Known(Match match) =>
        int.TryParse(match.Groups["n"].ValueSpan, out var number) && texts.TryGetValue(number, out var text) && match.Value == Token(number, Lines(text)) ? number : null;
}
