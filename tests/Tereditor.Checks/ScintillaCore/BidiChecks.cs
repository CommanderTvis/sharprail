using System.Text;

using SharpRail.Scintilla;
using SharpRail.Scintilla.View;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Managed-only checks (no upstream suite): UAX #9 cases for the managed replacement of SheenBidi.</summary>
internal static class BidiChecks
{
    internal static void Run()
    {
        const ScintillaTextDirection ltr = ScintillaTextDirection.LeftToRight, rtl = ScintillaTextDirection.RightToLeft, auto = ScintillaTextDirection.Auto;
        Check("abc def", ltr, "0-7L");
        Check("", ltr, "");
        Check("abc", rtl, "0-3L");
        Check("אבג", auto, "0-6R");
        Check("سلام", auto, "0-8R");
        Check("abc אבג def", ltr, "0-4L 4-10R 10-14L");
        Check("abc אבג def", rtl, "11-14L 3-11R 0-3L");
        // W7: European numbers after R stay at a higher even level, reordered as a block.
        Check("אב 123", ltr, "5-8L 0-5R");
        // W2: numbers after Arabic letters become Arabic numbers.
        Check("ع 12", auto, "3-5L 0-3R");
        // W4: a common separator between numbers joins them.
        Check("1,2", rtl, "0-3L");
        // N0: brackets around RTL text in RTL context are RTL; N1 alone would make the closing one LTR.
        Check("אב(ג)", ltr, "0-8R");
        Check("a(b)ג", rtl, "4-6R 0-4L");
        // X5a-X6a: an isolate is resolved apart from its surroundings.
        Check("a\u2067אב\u2069c", ltr, "0-4L 4-8R 8-12L");
        Check("a\u2068אב\u2069c", ltr, "0-4L 4-8R 8-12L");
        // P2-P3: Auto takes the first strong character.
        Check("123 אב", auto, "3-8R 0-3L");
        Check("123 ab", auto, "0-6L");
        // Line ends stay last in the paragraph direction.
        Check("אב\r\n", auto, "0-4R 4-6R");
        Check("ab\r\n", ltr, "0-2L 2-4L");
        Check("\n", rtl, "0-1R");
        // L1: trailing whitespace takes the paragraph level.
        Check("אב  ", ltr, "0-4R 4-6L");
        // Paragraph separators start a new paragraph with its own base direction.
        Check("אב\u2029ab\n", auto, "0-7R 7-9L 9-10R");
        // Overrides force the direction of strong characters.
        Check("\u202Eabc\u202C", ltr, "0-3L 3-6R 6-9L");
        // Invalid UTF-8 is a neutral replacement character.
        CheckBytes([(byte)'a', 0xFF, (byte)'b'], ltr, "0-3L");
    }

    private static void Check(string text, ScintillaTextDirection direction, string expected) =>
        CheckBytes(Encoding.UTF8.GetBytes(text), direction, expected, text);

    private static void CheckBytes(byte[] text, ScintillaTextDirection direction, string expected, string? name = null)
    {
        var runs = Bidi.VisualRuns(text, direction);
        var actual = string.Join(' ', runs.Select(run => $"{run.Start}-{run.End}{(run.RightToLeft ? 'R' : 'L')}"));
        Expect.Equal(expected, actual, $"bidi runs of '{name ?? Convert.ToHexString(text)}' ({direction})");
    }
}