using System.Text.RegularExpressions;

namespace Tereditor.Plugins.Chat.UI;

/// <summary>
/// The marker line the Claude CLI puts into a prompt for an attached image: <c>[Image: source: /tmp/…/3.png]</c> (the model reads the file)
/// or <c>[Image #3]</c> (a reference to an image that came with the prompt). The bubble shows each as a name, never as the raw text;
/// the prompt's own text is not changed.
/// </summary>
internal static partial class ChatImageMarkers
{
    [GeneratedRegex(@"[ \t]*\[Image(?: #(?<n>\d+))?(?:: source: (?<path>[^\]\r\n]+))?\][ \t]*")]
    private static partial Regex Marker();

    /// <summary>The text without its markers, and one (name, path) per marker; the path is empty for an <c>[Image #N]</c> reference.</summary>
    internal static (string Text, IReadOnlyList<(string Name, string Path)> Images) Split(string text)
    {
        if (text.IndexOf("[Image", StringComparison.Ordinal) < 0) return (text, []);
        var images = new List<(string, string)>();
        // Fenced code is left as written: only the lines outside a fence are searched.
        var lines = text.Split('\n');
        var fence = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) { fence = !fence; continue; }
            if (fence) continue;
            var before = images.Count;
            var stripped = Strip(lines[index], images);
            if (images.Count > before) lines[index] = stripped.Trim(' ', '\t');
        }
        if (images.Count == 0) return (text, []);
        return (string.Join('\n', lines).Trim(), images);
    }

    private static string Strip(string line, List<(string, string)> images) =>
        Marker().Replace(line, match =>
        {
            var path = match.Groups["path"].Success ? match.Groups["path"].Value.Trim() : "";
            if (path.Length == 0 && !match.Groups["n"].Success) return match.Value;
            var name = path.Length > 0 ? path[(path.LastIndexOfAny(['/', '\\']) + 1)..] : "Image #" + match.Groups["n"].Value;
            images.Add((name.Length > 0 ? name : path, path));
            return " ";
        });
}