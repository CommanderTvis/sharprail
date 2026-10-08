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
        if (failures.Count > 0) throw new InvalidOperationException("Design checks failed:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS generated design sources are current and colours are named only through roles");
    }

    /// <summary>The UI project's hand-written XAML and C#, with comments blanked so prose may name a value.</summary>
    private static List<SourceFile> Sources(string root)
    {
        var ui = DesignSources.Ui(root);
        return [.. Directory.EnumerateFiles(ui, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(ui, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("bin/", StringComparison.Ordinal) && !path.StartsWith("obj/", StringComparison.Ordinal) &&
                !path.StartsWith("Rendering/Generated/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => new SourceFile(path, Uncomment(File.ReadAllText(Path.Combine(ui, path)), path.EndsWith(".axaml", StringComparison.Ordinal))))];
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