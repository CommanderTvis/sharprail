using System.Text.RegularExpressions;

using SharpRail.UI.Rendering;

namespace SharpRail.Checks.Design;

/// <summary>
/// The design-system gate: generated output matches its authored source, and the UI's XAML and C# name
/// colours only through roles.
/// </summary>
internal static partial class DesignChecks
{
    private sealed record SourceFile(string Path, string[] Lines);

    internal static void Run(bool write)
    {
        var failures = new List<string>();
        if (!write)
        {
            ThemeGates.Run(failures);
            if (failures.Count > 0) throw new InvalidOperationException("Theme gates failed:\n" + string.Join("\n", failures));
            Console.WriteLine("PASS bundled manifests parse strictly, agree with their schema and meet the contrast and distinguishability floors");
        }
        if (DesignSources.Root() is not { } root)
        {
            Console.WriteLine("SKIP design source checks: no checkout around this executable");
            return;
        }
        foreach (var (name, text) in DesignSources.Generate(root))
        {
            var path = DesignSources.Generated(root, name);
            if (write) File.WriteAllText(path, text);
            else if (!File.Exists(path) || File.ReadAllText(path) != text)
                failures.Add($"Rendering/Generated/{name} is stale; run the checks with -- --design --write.");
        }
        if (write)
        {
            Console.WriteLine("Wrote generated design sources; rebuild before checking.");
            return;
        }
        var sources = Sources(root);
        ColorUsage(DesignSources.LoadColors(root), sources, failures);
        SpacingUsage(root, sources, failures);
        if (failures.Count > 0) throw new InvalidOperationException("Design checks failed:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS generated design sources are current, colours are named only through roles and rhythm stays on the spacing scale");
    }

    /// <summary>The UI project's hand-written XAML and C#, with comments blanked so prose may name a value.</summary>
    private static List<SourceFile> Sources(string root)
    {
        var ui = DesignSources.Ui(root);
        var kit = Path.Combine(root, "src", "SharpRail.Plugins.UI.Kit");
        var files = new[] { ui, kit }.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Select(path => (Physical: path, Relative: Path.GetRelativePath(directory, path).Replace('\\', '/'), Kit: directory == kit)))
            .Where(file => !file.Relative.StartsWith("bin/", StringComparison.Ordinal) && !file.Relative.StartsWith("obj/", StringComparison.Ordinal) &&
                !file.Relative.StartsWith("Generated/", StringComparison.Ordinal) && !file.Relative.StartsWith("Rendering/Generated/", StringComparison.Ordinal));
        return [.. files.Select(file => new SourceFile(file.Kit ? KitPath(file.Relative) : file.Relative,
            Uncomment(File.ReadAllText(file.Physical), file.Physical.EndsWith(".axaml", StringComparison.Ordinal)))).OrderBy(file => file.Path, StringComparer.Ordinal)];

        static string KitPath(string path) => path.StartsWith("Editor/", StringComparison.Ordinal) ? path
            : path.StartsWith("Markdown/", StringComparison.Ordinal) ? "Rendering/" + path[9..]
            : path.StartsWith("Visualization/", StringComparison.Ordinal) ? "Rendering/" + path[14..]
            : "Rendering/" + path;
    }

    private static string[] Uncomment(string text, bool xaml)
    {
        static string Blank(Match match) => NonNewline().Replace(match.Value, " ");
        text = xaml ? XmlComment().Replace(text, Blank) : LineComment().Replace(BlockComment().Replace(text, Blank), Blank);
        return text.Split('\n');
    }

    private static void Scan(IEnumerable<SourceFile> sources, Regex pattern, string message, List<string> failures)
    {
        foreach (var source in sources)
            for (var line = 0; line < source.Lines.Length; line++)
                foreach (Match match in pattern.Matches(source.Lines[line]))
                    failures.Add($"{source.Path}:{line + 1}: {message}: {match.Value.Trim()}");
    }

    // Ui computes tints and compositing; Themes decodes the palette's hex.
    private static readonly string[] ColorOwners = ["Rendering/Ui.cs", "Rendering/Themes.cs"];

    private static void ColorUsage(DesignSources.Colors colors, List<SourceFile> sources, List<string> failures)
    {
        foreach (var role in colors.Roles)
        {
            if (!Themes.ColorKeys.Contains(role.From)) failures.Add($"colors.json: role {role.Name} names the unknown palette key {role.From}.");
            if (role.Alpha is not null && !colors.Scale.ContainsKey(role.Alpha)) failures.Add($"colors.json: role {role.Name} names the unknown alpha step {role.Alpha}.");
            if (DesignSources.Nullable(role) && (!role.IsColor || role.Alpha is not null))
                failures.Add($"colors.json: role {role.Name} reads a nullable key, so it must be an untinted colour role.");
        }
        var claimed = colors.Roles.Select(role => role.From).ToHashSet();
        foreach (var key in Themes.ColorKeys)
        {
            if (!claimed.Contains(key) && !colors.Unclaimed.Contains(key)) failures.Add($"colors.json: palette key {key} is claimed by no role.");
            if (claimed.Contains(key) && colors.Unclaimed.Contains(key)) failures.Add($"colors.json: palette key {key} is claimed and still listed as unclaimed.");
        }
        foreach (var key in colors.Unclaimed.Except(Themes.ColorKeys)) failures.Add($"colors.json: unclaimed lists the unknown palette key {key}.");

        var controls = sources.Where(source => !ColorOwners.Contains(source.Path)).ToArray();
        Scan(controls.Where(source => source.Path.EndsWith(".cs", StringComparison.Ordinal)), RawColorCode(), "raw colour; name a role from Ui", failures);
        Scan(controls.Where(source => source.Path.EndsWith(".axaml", StringComparison.Ordinal)), RawColorXaml(), "raw colour; bind a role from Ui", failures);
        Scan(controls, PaletteRead(), "palette key read outside Ui; add a role to colors.json", failures);
        Scan(controls, PartialOpacity(), "opacity used as a tint; add a role on the alpha scale", failures);

        var text = string.Join('\n', controls.SelectMany(source => source.Lines));
        var own = string.Join('\n', sources.Single(source => source.Path == "Rendering/Ui.cs").Lines);
        foreach (var name in colors.Roles.Select(role => role.Name).Concat(colors.Effects.Select(effect => effect.Name)))
            if (!Regex.IsMatch(text, $@"\bUi\.{name}\b") && !Regex.IsMatch(own, $@"(?<![.\w]){name}\b"))
                failures.Add($"colors.json: role {name} is used by no control.");
    }

    /// <summary>Literal gaps, margins and paddings in XAML and C# must be steps; computed values are not rhythm.</summary>
    private static void SpacingUsage(string root, List<SourceFile> sources, List<string> failures)
    {
        var (steps, exceptions) = DesignSources.LoadSpacing(root);
        var used = new HashSet<DesignSources.SpacingException>();
        foreach (var source in sources)
            for (var line = 0; line < source.Lines.Length; line++)
                foreach (Match match in (source.Path.EndsWith(".axaml", StringComparison.Ordinal) ? SpacingXaml() : SpacingCode()).Matches(source.Lines[line]))
                {
                    var value = match.Groups["value"].Value.Trim();
                    var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var numbers = parts.Select(part => double.TryParse(part, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : (double?)null).ToArray();
                    // An expression is measured at run time; only literals are held to the scale.
                    if (numbers.Any(number => number is null) || numbers.All(number => steps.Contains(Math.Abs(number!.Value)))) continue;
                    var exception = exceptions.FirstOrDefault(entry => entry.File == source.Path && entry.Value == value);
                    if (exception is null) failures.Add($"{source.Path}:{line + 1}: off-scale spacing: {match.Value.Trim()}");
                    else used.Add(exception);
                }
        foreach (var exception in exceptions.Except(used))
            failures.Add($"spacing.json: the exception for {exception.File} ({exception.Value}) matches nothing.");
    }

    [GeneratedRegex(@"\b(Spacing|RowSpacing|ColumnSpacing|Margin|Padding)\s*=\s*(new\s*(Thickness)?\s*\((?<value>[^()]*)\)|(?<value>-?[\d.]+)\b)")]
    private static partial Regex SpacingCode();
    [GeneratedRegex(@"\b(Spacing|RowSpacing|ColumnSpacing|Margin|Padding)=""(?<value>[^""{]*)""|Property=""(Margin|Padding|Spacing|RowSpacing|ColumnSpacing)""\s+Value=""(?<value>[^""{]*)""")]
    private static partial Regex SpacingXaml();

    [GeneratedRegex(@"[^\n]")]
    private static partial Regex NonNewline();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();
    // A URI's "//" is not a comment.
    [GeneratedRegex(@"(?<!:)//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\bColor\.(Parse|TryParse|FromArgb|FromRgb|FromUInt32)\b|(?<![.\w])Colors\.[A-Z]\w*|(?<![.\w])Brushes\.(?!Transparent\b)\w+|\bnew\s+(SolidColor|LinearGradient|RadialGradient|ConicGradient)Brush\b|""#[0-9a-fA-F]{3,8}""")]
    private static partial Regex RawColorCode();
    [GeneratedRegex(@"""#[0-9a-fA-F]{3,8}""|\b(Background|Foreground|BorderBrush|Fill|Stroke|Color|SelectionBrush|CaretBrush|Accent)=""(?!Transparent""|\{)[^""]*""|Property=""(Background|Foreground|BorderBrush|Fill|Stroke)""\s+Value=""(?!Transparent""|\{)[^""]*""")]
    private static partial Regex RawColorXaml();
    [GeneratedRegex(@"\b\w*[Tt]heme\s*\[\s*""|\.Colors\s*\[\s*""")]
    private static partial Regex PaletteRead();
    [GeneratedRegex(@"\bOpacity\s*=\s*""?(?![01](?![\d.]))[\d.]+|Property=""Opacity""\s+Value=""(?![01]"")[^""]*""")]
    private static partial Regex PartialOpacity();
}