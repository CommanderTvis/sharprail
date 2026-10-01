using Avalonia.Media;

namespace Ghostty.Avalonia;

/// <summary>
/// A terminal theme. Colours are opaque; composite translucent theme colours before passing them.
/// A null cursor or selection colour lets the terminal choose one.
/// </summary>
public sealed record TerminalColors(Color Background, Color Foreground, IReadOnlyList<Color> Ansi)
{
    public Color? Cursor { get; init; }
    public Color? SelectionBackground { get; init; }
    public Color? SelectionForeground { get; init; }
    /// <summary>The WCAG contrast ratio text keeps against its background, as Ghostty's <c>minimum-contrast</c>.</summary>
    public double MinimumContrast { get; init; } = 1;

    public static TerminalColors Default { get; } = new(Color.FromRgb(0x1d, 0x1f, 0x21), Color.FromRgb(0xc5, 0xc8, 0xc6),
    [
        Color.FromRgb(0x1d, 0x1f, 0x21), Color.FromRgb(0xcc, 0x66, 0x66), Color.FromRgb(0xb5, 0xbd, 0x68), Color.FromRgb(0xf0, 0xc6, 0x74),
        Color.FromRgb(0x81, 0xa2, 0xbe), Color.FromRgb(0xb2, 0x94, 0xbb), Color.FromRgb(0x8a, 0xbe, 0xb7), Color.FromRgb(0xc5, 0xc8, 0xc6),
        Color.FromRgb(0x66, 0x66, 0x66), Color.FromRgb(0xd5, 0x4e, 0x53), Color.FromRgb(0xb9, 0xca, 0x4a), Color.FromRgb(0xe7, 0xc5, 0x47),
        Color.FromRgb(0x7a, 0xa6, 0xda), Color.FromRgb(0xc3, 0x97, 0xd8), Color.FromRgb(0x70, 0xc0, 0xb1), Color.FromRgb(0xea, 0xea, 0xea)
    ]);

    internal uint[] NativeView() =>
    [
        Background.ToUInt32(), Foreground.ToUInt32(), Cursor?.ToUInt32() ?? 0,
        SelectionBackground?.ToUInt32() ?? 0, SelectionForeground?.ToUInt32() ?? 0, .. Palette()
    ];

    internal uint[] Vt() => [Background.ToUInt32(), Foreground.ToUInt32(), Cursor?.ToUInt32() ?? 0, .. Palette()];

    private IEnumerable<uint> Palette()
    {
        if (Ansi.Count != 16) throw new ArgumentException("A terminal palette has 16 ANSI colours.");
        return Ansi.Select(color => color.ToUInt32());
    }
}