using System.Globalization;
using System.Text;
using System.Text.Json;

using SharpRail.UI.Rendering;

namespace SharpRail.Checks.Design;

/// <summary>The authored design sources under Rendering/Design and the C# generated from them.</summary>
internal static class DesignSources
{
    internal sealed record ColorRole(string Name, string From, string? Alpha, bool IsColor);
    internal sealed record Effect(string Name, string Dark, string Light);
    internal sealed record Colors(IReadOnlyDictionary<string, int> Scale, IReadOnlyList<ColorRole> Roles, IReadOnlyList<Effect> Effects,
        IReadOnlyList<string> Unclaimed);

    /// <summary>The checkout holding the sources, or null when the checks run away from one (a copied publish).</summary>
    internal static string? Root()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "SharpRail.slnx"))) return directory.FullName;
        return null;
    }

    internal static string Ui(string root) => Path.Combine(root, "src", "SharpRail.UI");
    internal static string Source(string root, string name) => Path.Combine(Ui(root), "Rendering", "Design", name);
    internal static string Generated(string root, string name) => Path.Combine(root, "src", "SharpRail.Plugins.UI.Kit", "Generated", name);

    internal static Colors LoadColors(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Source(root, "colors.json")));
        var document = json.RootElement;
        static string? Optional(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;
        return new(
            document.GetProperty("scale").EnumerateObject().ToDictionary(step => step.Name, step => step.Value.GetInt32()),
            [.. document.GetProperty("roles").EnumerateObject().Select(role => new ColorRole(role.Name,
                role.Value.GetProperty("from").GetString()!, Optional(role.Value, "alpha"), Optional(role.Value, "kind") == "color"))],
            [.. document.GetProperty("effects").EnumerateObject().Select(effect => new Effect(effect.Name,
                effect.Value.GetProperty("dark").GetString()!, effect.Value.GetProperty("light").GetString()!))],
            [.. document.GetProperty("unclaimed").EnumerateObject().Select(key => key.Name)]);
    }

    internal sealed record SpacingException(string File, string Value);

    internal static (IReadOnlyList<double> Steps, IReadOnlyList<SpacingException> Exceptions) LoadSpacing(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Source(root, "spacing.json")));
        return ([.. json.RootElement.GetProperty("steps").EnumerateArray().Select(step => step.GetDouble())],
            [.. json.RootElement.GetProperty("exceptions").EnumerateArray().Select(entry =>
                new SpacingException(entry.GetProperty("file").GetString()!, entry.GetProperty("value").GetString()!))]);
    }

    /// <summary>Every generated file by name, as it should currently read.</summary>
    internal static IReadOnlyDictionary<string, string> Generate(string root) => new Dictionary<string, string>
    {
        ["Ui.Colors.g.cs"] = GenerateColors(LoadColors(root)),
        ["LayoutRoles.g.cs"] = GenerateLayout(LoadLayout(root)),
        ["Typography.g.cs"] = GenerateTypography(LoadTypography(root))
    };

    internal sealed record LayoutRole(string Name, double Value, string Kind, string Reason);

    internal static IReadOnlyList<LayoutRole> LoadLayout(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Source(root, "spacing.json")));
        return [.. json.RootElement.GetProperty("layout").EnumerateObject().Where(role => role.Value.ValueKind == JsonValueKind.Object)
            .Select(role => new LayoutRole(role.Name, role.Value.GetProperty("value").GetDouble(),
                role.Value.GetProperty("kind").GetString()!, role.Value.GetProperty("reason").GetString()!))];
    }

    private static string GenerateLayout(IReadOnlyList<LayoutRole> roles)
    {
        var text = new StringBuilder(string.Format(CultureInfo.InvariantCulture, Header, "spacing.json"));
        text.Append("#nullable enable\nnamespace SharpRail.Plugins.UI.Kit;\n\n/// <summary>Named layout roles: values that stay equal across surfaces.</summary>\npublic static class LayoutRoles\n{\n");
        foreach (var role in roles)
            text.Append($"    /// <summary>{char.ToUpperInvariant(role.Reason[0]) + role.Reason[1..]}.</summary>\n    public const double {role.Name} = {role.Value.ToString(CultureInfo.InvariantCulture)};\n");
        return text.Append("}\n").ToString();
    }

    /// <summary>A canonical style names one primitive per property; an alias names a canonical style by group.name.</summary>
    internal sealed record TextStyle(string Group, string Name, string? Ref, string Family, string Size, string Weight, string Leading,
        string Tracking, string Transform)
    {
        internal string Id => Group + "." + Name;
        internal string Member => char.ToUpperInvariant(Group[0]) + Group[1..] + char.ToUpperInvariant(Name[0]) + Name[1..];
        internal string Class => Group switch
        {
            "ui" when Name == "default" => "tr-text-ui",
            "ui" or "body" => "tr-text-" + Kebab(Name),
            _ => $"tr-{Group}-{Kebab(Name)}"
        };
        private static string Kebab(string name) => string.Concat(name.Select(c => char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : c.ToString()));
    }

    internal sealed record Typography(IReadOnlyDictionary<string, string> Families, IReadOnlyDictionary<string, int> Weights,
        IReadOnlyDictionary<string, double> Sizes, IReadOnlyDictionary<string, double> Leadings, IReadOnlyDictionary<string, double> Trackings,
        IReadOnlyList<TextStyle> Styles, IReadOnlyList<SpacingException> Exceptions);

    internal static Typography LoadTypography(string root)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Source(root, "typography.json")));
        var document = json.RootElement;
        Dictionary<string, T> Map<T>(string name, Func<JsonElement, T> read) =>
            document.GetProperty(name).EnumerateObject().ToDictionary(entry => entry.Name, entry => read(entry.Value));
        static string Field(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString()! : "";
        var styles = new List<TextStyle>();
        foreach (var group in document.GetProperty("textStyles").EnumerateObject())
            foreach (var style in group.Value.EnumerateObject())
                styles.Add(new TextStyle(group.Name, style.Name, style.Value.TryGetProperty("$ref", out var reference) ? reference.GetString() : null,
                    Field(style.Value, "fontFamily"), Field(style.Value, "fontSize"), Field(style.Value, "fontWeight"), Field(style.Value, "lineHeight"),
                    Field(style.Value, "letterSpacing"), Field(style.Value, "textTransform")));
        return new(Map("fontFamilies", value => value.GetString()!), Map("fontWeights", value => value.GetInt32()),
            Map("fontSizes", value => value.GetDouble()), Map("lineHeights", value => value.GetDouble()), Map("letterSpacings", value => value.GetDouble()),
            styles, [.. document.GetProperty("exceptions").EnumerateArray().Select(entry =>
                new SpacingException(entry.GetProperty("file").GetString()!, entry.GetProperty("value").GetString()!))]);
    }

    /// <summary>The canonical style an alias names, or the style itself.</summary>
    internal static TextStyle Resolve(Typography type, TextStyle style) =>
        style.Ref is null ? style : type.Styles.Single(target => target.Id == style.Ref);

    private static string GenerateTypography(Typography type)
    {
        static string Number(double value) => Math.Round(value, 4).ToString(CultureInfo.InvariantCulture);
        var text = new StringBuilder(string.Format(CultureInfo.InvariantCulture, Header, "typography.json"));
        text.Append("#nullable enable\nusing Avalonia.Media;\n\nnamespace SharpRail.Plugins.UI.Kit;\n\n/// <summary>The semantic text styles.</summary>\npublic static partial class Typography\n{\n");
        foreach (var style in type.Styles)
        {
            var canonical = type.Styles.FirstOrDefault(target => target.Id == style.Ref) ?? style;
            var size = type.Sizes.GetValueOrDefault(canonical.Size);
            text.Append($"    /// <summary><c>{style.Id}</c>{(style.Ref is null ? "" : $", identical to <c>{style.Ref}</c>")}.</summary>\n");
            text.Append($"    public static readonly TextRole {style.Member} = new(\"{style.Class}\", Ui.{type.Families.GetValueOrDefault(canonical.Family)}, {Number(size)}, " +
                $"(FontWeight){type.Weights.GetValueOrDefault(canonical.Weight)}, {Number(size * type.Leadings.GetValueOrDefault(canonical.Leading))}, " +
                $"{Number(size * type.Trackings.GetValueOrDefault(canonical.Tracking))}, {(canonical.Transform == "uppercase" ? "true" : "false")});\n");
        }
        text.Append("\n    /// <summary>Every style, aliases included.</summary>\n    public static readonly IReadOnlyList<TextRole> All = [");
        text.Append(string.Join(", ", type.Styles.Select(style => style.Member)));
        return text.Append("];\n}\n").ToString();
    }

    private const string Header = "// <auto-generated>\n// Written from Rendering/Design/{0} by `SharpRail.Checks -- --design --write`. Edit the source, not this file.\n// </auto-generated>\n";

    private static string GenerateColors(Colors colors)
    {
        var text = new StringBuilder(string.Format(CultureInfo.InvariantCulture, Header, "colors.json"));
        text.Append("#nullable enable\nusing Avalonia.Media;\n\nnamespace SharpRail.Plugins.UI.Kit;\n\npublic static partial class Ui\n{\n");
        foreach (var role in colors.Roles)
            text.Append(role.IsColor
                ? $"    public static Color{(Nullable(role) ? "?" : "")} {role.Name} {{ get; private set; }}\n"
                : $"    public static readonly SolidColorBrush {role.Name} = new();\n");
        foreach (var effect in colors.Effects) text.Append($"    public static readonly SolidColorBrush {effect.Name} = new();\n");
        text.Append("\n    private static void ApplyRoles(ThemeManifest theme)\n    {\n");
        foreach (var role in colors.Roles)
        {
            var source = Nullable(role) ? $"theme.Colors[\"{role.From}\"]" : $"theme[\"{role.From}\"]";
            if (role.Alpha is not null) source = $"Alpha({source}, {colors.Scale[role.Alpha]})";
            text.Append($"        {role.Name}{(role.IsColor ? "" : ".Color")} = {source};\n");
        }
        foreach (var effect in colors.Effects)
            text.Append(effect.Dark == effect.Light
                ? $"        {effect.Name}.Color = {Argb(effect.Dark)};\n"
                : $"        {effect.Name}.Color = theme.IsLight ? {Argb(effect.Light)} : {Argb(effect.Dark)};\n");
        return text.Append("    }\n}\n").ToString();
    }

    internal static bool Nullable(ColorRole role) => Themes.NullableKeys.Contains(role.From);

    // #rrggbbaa as the #aarrggbb number Avalonia reads.
    private static string Argb(string hex) => $"Color.FromUInt32(0x{hex[7..9]}{hex[1..7]})";
}