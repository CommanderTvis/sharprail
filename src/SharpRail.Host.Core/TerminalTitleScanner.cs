using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// Follows the window and tab title a program sets with OSC 0 or OSC 2 in a shell's output, across chunk boundaries.
/// OSC 1 names only the icon, which no client shows. Bounded: a sequence longer than <see cref="MaxBytes"/> is ignored.
/// </summary>
internal sealed class TerminalTitleScanner
{
    internal const int MaxBytes = 4096;
    private enum Mode { Ground, Escape, Osc, OscEscape }
    private Mode mode;
    private readonly byte[] buffer = new byte[MaxBytes];
    private int length;
    private bool overflow;

    /// <summary>Scans output; true when it set a title, with the last one in <paramref name="title"/>, null for an empty title.</summary>
    internal bool Scan(ReadOnlySpan<byte> data, out string? title)
    {
        title = null;
        var found = false;
        foreach (var value in data)
            switch (mode)
            {
                case Mode.Ground:
                    if (value == 0x1b) mode = Mode.Escape;
                    break;
                case Mode.Escape:
                    if (value == (byte)']') { mode = Mode.Osc; length = 0; overflow = false; }
                    else if (value != 0x1b) mode = Mode.Ground;
                    break;
                case Mode.Osc:
                    if (value == 0x07) { mode = Mode.Ground; found |= Finish(ref title); }
                    else if (value == 0x1b) mode = Mode.OscEscape;
                    else if (value is 0x18 or 0x1a) mode = Mode.Ground;
                    else if (length < buffer.Length) buffer[length++] = value;
                    else overflow = true;
                    break;
                case Mode.OscEscape:
                    if (value == (byte)'\\') { mode = Mode.Ground; found |= Finish(ref title); }
                    // Any other escape abandons the sequence and starts a new one.
                    else if (value == (byte)']') { mode = Mode.Osc; length = 0; overflow = false; }
                    else mode = value == 0x1b ? Mode.Escape : Mode.Ground;
                    break;
            }
        return found;
    }

    private bool Finish(ref string? title)
    {
        if (overflow) return false;
        var text = buffer.AsSpan(0, length);
        var separator = text.IndexOf((byte)';');
        if (separator < 0 || !(text[..separator] is [(byte)'0'] or [(byte)'2'])) return false;
        title = Clean(Encoding.UTF8.GetString(text[(separator + 1)..]));
        return true;
    }

    // Control characters never reach a tab label; an over-long title is cut to the catalog's bound.
    internal static string? Clean(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value) text.Append(char.IsControl(character) ? ' ' : character);
        var clean = text.ToString().Trim();
        if (clean.Length > TerminalTab.MaxTitleLength)
            clean = clean[..(char.IsHighSurrogate(clean[TerminalTab.MaxTitleLength - 1]) ? TerminalTab.MaxTitleLength - 1 : TerminalTab.MaxTitleLength)];
        return clean.Length == 0 ? null : clean;
    }
}