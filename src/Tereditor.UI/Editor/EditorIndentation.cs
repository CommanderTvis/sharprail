using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Tereditor.Host.Abstractions;

namespace Tereditor.UI.Editor;

/// <summary>A file editor's effective indentation: what Tab inserts, how far one step goes, and how wide a tab draws.</summary>
internal readonly record struct Indentation(bool UseTabs, int Size, int TabWidth);

/// <summary>The indentation settings (Settings › Editor) a file starts from.</summary>
internal readonly record struct IndentSettings(bool UseTabs, int Size, bool Detect)
{
    public static IndentSettings Default => new(false, HostSettings.DefaultEditorTabWidth, true);
}

/// <summary>A file's own indentation, as far as its text makes it clear; a null size means it did not say.</summary>
internal readonly record struct DetectedIndent(bool UseTabs, int? Size);

/// <summary>The .editorconfig keys editors honour for one file; null is unset.</summary>
internal sealed record EditorConfigIndent(bool? UseTabs, int? Size, bool SizeIsTab, int? TabWidth)
{
    public bool IsEmpty => UseTabs is null && Size is null && !SizeIsTab && TabWidth is null;
}

/// <summary>Resolves indentation per key: .editorconfig over the file's own clear indentation over the settings.</summary>
internal static class EditorIndentation
{
    private const int MaxDetectLines = 10_000, MaxSize = 16;

    public static Indentation Resolve(IndentSettings settings, DetectedIndent? detected, EditorConfigIndent? config)
    {
        var detect = settings.Detect ? detected : null;
        var useTabs = config?.UseTabs ?? detect?.UseTabs ?? settings.UseTabs;
        // editorconfig: indent_size = tab means the tab width; tab_width defaults to indent_size.
        var size = config?.Size ?? (config?.SizeIsTab == true ? config.TabWidth : null) ?? detect?.Size ?? settings.Size;
        var tabWidth = config?.TabWidth ?? config?.Size ?? size;
        return new(useTabs, Math.Clamp(size, 1, MaxSize), Math.Clamp(tabWidth, 1, MaxSize));
    }

    /// <summary>
    /// The file's indentation when it is clear: tabs or spaces lead at least twice as many indented lines as the other, and
    /// for spaces the most common step between neighbouring lines (2–8 columns) occurs at least twice. Null when unclear.
    /// </summary>
    public static DetectedIndent? Detect(string text)
    {
        int tabLines = 0, spaceLines = 0, previous = 0, lines = 0;
        var steps = new int[9];
        foreach (var range in text.AsSpan().EnumerateLines())
        {
            if (++lines > MaxDetectLines) break;
            var line = range;
            if (line.IsWhiteSpace()) continue;
            var spaces = 0;
            while (spaces < line.Length && line[spaces] == ' ') spaces++;
            if (spaces < line.Length && line[spaces] == '\t') { if (spaces == 0) tabLines++; previous = 0; continue; }
            // A block comment's leading " * " is alignment, not indentation.
            if (spaces < line.Length && line[spaces] == '*') continue;
            if (spaces > 0) spaceLines++;
            var step = Math.Abs(spaces - previous);
            if (step is >= 2 and <= 8) steps[step]++;
            previous = spaces;
        }
        if (tabLines >= 2 && tabLines >= 2 * spaceLines) return new(true, null);
        if (spaceLines < 2 || spaceLines < 2 * tabLines) return null;
        var best = 0;
        for (var step = 2; step < steps.Length; step++)
            if (steps[step] > steps[best]) best = step;
        return steps[best] >= 2 ? new(false, best) : null;
    }

    /// <summary>The workspace-relative .editorconfig paths that may govern <paramref name="path"/>, nearest first.</summary>
    public static IReadOnlyList<string> ConfigPaths(string path)
    {
        var paths = new List<string>();
        var directory = Path.GetDirectoryName(path.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
        while (true)
        {
            paths.Add(directory.Length == 0 ? ".editorconfig" : directory + "/.editorconfig");
            if (directory.Length == 0) return paths;
            var slash = directory.LastIndexOf('/');
            directory = slash < 0 ? "" : directory[..slash];
        }
    }

    /// <summary>
    /// Applies .editorconfig files (nearest first, each with its workspace-relative path) to <paramref name="path"/>: a
    /// <c>root = true</c> file stops the search, closer files and later sections win. Null when none sets a key editors use.
    /// </summary>
    public static EditorConfigIndent? Read(IReadOnlyList<(string Path, string Text)> files, string path)
    {
        path = path.Replace('\\', '/');
        var chain = new List<(string Path, string Text)>();
        foreach (var file in files)
        {
            chain.Add(file);
            if (IsRoot(file.Text)) break;
        }
        chain.Reverse();
        string? style = null, size = null, tabWidth = null;
        foreach (var (configPath, text) in chain)
        {
            var directory = configPath.Contains('/') ? configPath[..configPath.LastIndexOf('/')] : "";
            var relative = directory.Length == 0 ? path : path.StartsWith(directory + "/", StringComparison.Ordinal) ? path[(directory.Length + 1)..] : null;
            if (relative is null) continue;
            Regex? section = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';') continue;
                if (line[0] == '[')
                {
                    var end = line.LastIndexOf(']');
                    section = end > 1 ? Glob(line[1..end]) : null;
                    continue;
                }
                if (section is null || !section.IsMatch(relative)) continue;
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                var key = line[..equals].Trim().ToLowerInvariant();
                var value = line[(equals + 1)..].Trim().ToLowerInvariant();
                switch (key)
                {
                    case "indent_style": style = value; break;
                    case "indent_size": size = value; break;
                    case "tab_width": tabWidth = value; break;
                }
            }
        }
        var result = new EditorConfigIndent(style switch { "tab" => true, "space" => false, _ => null }, Number(size), size == "tab", Number(tabWidth));
        return result.IsEmpty ? null : result;

        static int? Number(string? value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= MaxSize ? number : null;
    }

    private static bool IsRoot(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) return false;
            var equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals("root", StringComparison.OrdinalIgnoreCase))
                return line[(equals + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>An .editorconfig section glob: <c>*</c>, <c>**</c>, <c>?</c>, <c>[...]</c> and <c>{a,b}</c>; a glob without a slash matches a file name in any directory.</summary>
    private static Regex Glob(string glob)
    {
        var anywhere = !glob.Contains('/');
        if (glob.StartsWith('/')) glob = glob[1..];
        var pattern = new StringBuilder(anywhere ? "^(?:.*/)?" : "^");
        var braces = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    pattern.Append(".*"); i++; break;
                case '*': pattern.Append("[^/]*"); break;
                case '?': pattern.Append("[^/]"); break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close < 0) { pattern.Append(@"\["); break; }
                    var set = glob[(i + 1)..close];
                    pattern.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set)).Append(']');
                    i = close;
                    break;
                case '{': braces++; pattern.Append("(?:"); break;
                case '}' when braces > 0: braces--; pattern.Append(')'); break;
                case ',' when braces > 0: pattern.Append('|'); break;
                case '\\' when i + 1 < glob.Length: pattern.Append(Regex.Escape(glob[++i].ToString())); break;
                default: pattern.Append(Regex.Escape(c.ToString())); break;
            }
        }
        while (braces-- > 0) pattern.Append(')');
        try { return new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return new Regex("(?!)"); }
    }
}