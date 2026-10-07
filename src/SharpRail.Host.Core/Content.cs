using System.Text.RegularExpressions;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>
/// The byte-level classification shared by diff sides and file reads: media type from magic numbers, then SVG and
/// Git LFS pointers from text, then the filename only when the bytes say nothing. Text has no recognised magic
/// number, no NUL in the first 8 KiB and a strict UTF-8 decode.
/// </summary>
public static partial class ContentClassifier
{
    public const int SniffBytes = 8 * 1024;
    private const int LfsPointerLimit = 1024;

    private static readonly (string Media, byte[] Magic)[] Magics =
    [
        ("image/png", [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        ("image/jpeg", [0xff, 0xd8, 0xff]),
        ("image/gif", "GIF87a"u8.ToArray()),
        ("image/gif", "GIF89a"u8.ToArray()),
        ("image/bmp", "BM"u8.ToArray()),
        ("image/x-icon", [0x00, 0x00, 0x01, 0x00]),
        ("application/pdf", "%PDF-"u8.ToArray()),
        ("application/zip", [0x50, 0x4b, 0x03, 0x04]),
        ("application/zip", [0x50, 0x4b, 0x05, 0x06]),
        ("application/zip", [0x50, 0x4b, 0x07, 0x08]),
        ("application/gzip", [0x1f, 0x8b]),
        ("font/woff", "wOFF"u8.ToArray()),
        ("font/woff2", "wOF2"u8.ToArray())
    ];

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".json"] = "application/json",
        [".csv"] = "text/csv",
        [".tsv"] = "text/csv",
        [".yaml"] = "text/yaml",
        [".yml"] = "text/yaml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xhtml"] = "application/xhtml+xml",
        [".txt"] = "text/plain"
    };

    [GeneratedRegex(@"^\s*<svg[\s>]")] private static partial Regex SvgRoot();
    [GeneratedRegex(@"^\s*<\?xml(?:\s|\?>)")] private static partial Regex XmlProlog();
    [GeneratedRegex(@"<svg[\s>]")] private static partial Regex SvgTag();
    [GeneratedRegex(@"^version https://git-lfs\.github\.com/spec/v1\noid sha256:[0-9a-f]{64}\nsize \d{1,15}\n\z")] private static partial Regex LfsPointer();

    /// <summary>The metadata of a resource that does not exist.</summary>
    public static ContentMetadata Absent { get; } = new(null, null, true, null);

    public static ContentMetadata Classify(ReadOnlySpan<byte> bytes, string path)
    {
        var (text, media) = Sniff(bytes);
        return new(ContentInfo.Sha256Hex(bytes), bytes.Length, text, media ?? (ByExtension.TryGetValue(Path.GetExtension(path), out var named) ? named : null));
    }

    private static (bool Text, string? Media) Sniff(ReadOnlySpan<byte> bytes)
    {
        if (Magic(bytes) is { } magic) return (false, magic);
        if (bytes[..Math.Min(bytes.Length, SniffBytes)].Contains((byte)0) || !ContentInfo.IsText(bytes)) return (false, null);
        if (IsSvg(bytes)) return (true, "image/svg+xml");
        if (bytes.Length <= LfsPointerLimit && LfsPointer().IsMatch(ContentInfo.Decode(bytes))) return (true, "application/vnd.git-lfs");
        return (true, null);
    }

    private static string? Magic(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8) &&
            (bytes.Slice(8, 4).SequenceEqual("avif"u8) || bytes.Slice(8, 4).SequenceEqual("avis"u8))) return "image/avif";
        foreach (var (media, magic) in Magics)
            if (bytes.StartsWith(magic)) return media;
        return null;
    }

    private static bool IsSvg(ReadOnlySpan<byte> bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, SniffBytes)]);
        if (SvgRoot().IsMatch(head)) return true;
        if (!XmlProlog().IsMatch(head)) return false;
        var end = head.IndexOf("?>", StringComparison.Ordinal);
        return end >= 0 && SvgTag().IsMatch(head[(end + 2)..]);
    }
}