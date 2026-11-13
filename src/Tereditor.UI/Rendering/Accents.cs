using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI.Rendering;

/// <summary>One curated accent: a hue and saturation, whose lightness each theme solves for.</summary>
public sealed record AccentChoice(string Id, string Label, double Hue, double Saturation);

/// <summary>
/// The user's accent colour. A theme manifest supplies the accent roles (<c>accent</c>, <c>accentSolid</c>, <c>accentHover</c>,
/// <c>onAccent</c>); a chosen accent replaces exactly those four keys and leaves the manifest otherwise whole, so every role,
/// tint and baking consumer follows through <c>Ui.Apply</c> with no control knowing the accent exists. The default accent is
/// the manifest itself, unchanged.
/// </summary>
public static partial class Accents
{
    public const string DefaultId = "green";

    /// <summary>
    /// Green is the bundled theme accents themselves; the rest keep their hue and take the lightness each theme can legibly hold.
    /// Frog, the app icon's cyan, comes first; the order is only the picker's, green stays the default.
    /// </summary>
    public static IReadOnlyList<AccentChoice> Curated { get; } =
    [
        new("frog", "Frog", 188, 0.9),
        new(DefaultId, "Green", 100, 1),
        new("blue", "Blue", 215, 1),
        new("purple", "Purple", 265, 0.9),
        new("pink", "Pink", 330, 0.9),
        new("red", "Red", 355, 0.9),
        new("orange", "Orange", 28, 1),
        new("yellow", "Yellow", 50, 1),
        new("graphite", "Graphite", 220, 0.08)
    ];

    /// <summary>Retired ids and the curated accent each now means: Teal merged into Frog, its near twin.</summary>
    private static readonly Dictionary<string, string> Retired = new() { ["teal"] = "frog" };

    /// <summary>The stored value, with a retired id replaced by the accent it became.</summary>
    public static string Canonical(string accent) => Retired.GetValueOrDefault(accent, accent);

    [GeneratedRegex("^#?[0-9a-fA-F]{6}$")]
    private static partial Regex CustomPattern();

    /// <summary>Whether the stored value is the unchanged bundled accent.</summary>
    public static bool IsDefault(string? accent) => Resolve(accent) is null;

    /// <summary>The canonical stored form of a custom colour, <c>#rrggbb</c>, or null when the text is not a six-digit hex colour.</summary>
    public static string? ParseCustom(string? text)
    {
        text = text?.Trim();
        return text is not null && CustomPattern().IsMatch(text) ? "#" + text.TrimStart('#').ToLowerInvariant() : null;
    }

    /// <summary>The curated entry, a custom colour as a one-off entry, or null for an unknown or default value.</summary>
    private static AccentChoice? Resolve(string? accent)
    {
        if (string.IsNullOrEmpty(accent) || accent == DefaultId) return null;
        accent = Canonical(accent);
        if (Curated.FirstOrDefault(choice => choice.Id == accent) is { } curated) return curated;
        if (ParseCustom(accent) is not { } hex) return null;
        var (hue, saturation, _) = ToHsl(Color.Parse(hex));
        return new(hex, hex, hue, saturation);
    }

    private static readonly ConditionalWeakTable<ThemeManifest, Dictionary<string, ThemeManifest>> Cache = [];

    /// <summary>The theme with the chosen accent's roles; the same instance for the same pair, so <c>Ui.Apply</c> recognises it as applied.</summary>
    public static ThemeManifest Apply(ThemeManifest theme, string? accent)
    {
        if (Resolve(accent) is not { } choice) return theme;
        var derived = Cache.GetOrCreateValue(theme);
        lock (derived)
        {
            if (!derived.TryGetValue(choice.Id, out var result))
                derived[choice.Id] = result = Derive(theme, choice, custom: choice.Id.StartsWith('#'));
            return result;
        }
    }

    /// <summary>The solid fill a swatch shows for an accent in a theme.</summary>
    public static Color Swatch(ThemeManifest theme, string? accent) => Apply(theme, accent)["accentSolid"];

    /// <summary>The brush a swatch paints: an accent shown as data, never a role.</summary>
    public static IBrush SwatchBrush(ThemeManifest theme, string? accent) => new SolidColorBrush(Swatch(theme, accent));

    private static readonly string[] Resting = ["background", "content", "sidebar", "header", "elevated"];

    private static ThemeManifest Derive(ThemeManifest theme, AccentChoice choice, bool custom)
    {
        var high = theme.IsHighContrast;
        var surfaces = Resting.Select(key => theme[key]).ToArray();
        var hover = theme["hover"];
        var (_, themeSaturation, accentLightness) = ToHsl(theme["accent"]);
        // A quiet palette keeps its accents quiet; a custom colour keeps the saturation it was given.
        var saturation = custom ? choice.Saturation : choice.Saturation * Math.Min(1, themeSaturation);

        Color At(double lightness) => FromHsl(choice.Hue, saturation, lightness);

        var textFloor = high ? 7.0 : 4.5;
        var onHoverFloor = high ? 4.5 : 3.0;
        var accent = Solve(accentLightness, lightness =>
        {
            var color = At(lightness);
            return Math.Min(surfaces.Min(surface => Contrast(color, surface)) - textFloor, Contrast(color, hover) - onHoverFloor);
        });

        var (_, _, solidLightness) = ToHsl(theme["accentSolid"]);
        var hoverStep = ToHsl(theme["accentHover"]).Lightness - solidLightness;
        Color Step(double lightness) => At(Math.Clamp(lightness + hoverStep, 0, 1));
        // The label is whichever of near-black and white reads better on the fill, never the theme's tinted label.
        var dark = Color.Parse("#0a0a0a");
        var light = Colors.White;
        double Worst(Color label, double lightness) => Math.Min(Contrast(label, At(lightness)), Contrast(label, Step(lightness)));
        var onAccent = Worst(dark, solidLightness) >= Worst(light, solidLightness) ? dark : light;
        // The hover step must stay visible: a bright fill with a lighter hover would clamp at white.
        var solid = Solve(solidLightness, lightness => Math.Min(Worst(onAccent, lightness) - 4.5, Contrast(At(lightness), Step(lightness)) - 1.1));

        var colors = new Dictionary<string, Color?>(theme.Colors)
        {
            ["accent"] = At(accent),
            ["accentSolid"] = At(solid),
            ["accentHover"] = Step(solid),
            ["onAccent"] = onAccent
        };
        return theme with { Colors = colors };
    }

    /// <summary>The lightness nearest the preferred one whose margin is not negative; failing that, the one with the best margin.</summary>
    private static double Solve(double preferred, Func<double, double> margin)
    {
        var best = preferred;
        var bestMargin = double.NegativeInfinity;
        for (var step = 0; step <= 200; step++)
            foreach (var lightness in step == 0 ? new[] { preferred } : new[] { preferred - step / 200.0, preferred + step / 200.0 })
            {
                if (lightness is < 0 or > 1) continue;
                var value = margin(lightness);
                if (value >= 0) return lightness;
                if (value > bestMargin) (best, bestMargin) = (lightness, value);
            }
        return best;
    }

    public static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    /// <summary>The WCAG contrast ratio, ignoring alpha.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (first, second) = (Luminance(a), Luminance(b));
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var delta = max - min;
        if (delta == 0) return (0, 0, lightness);
        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var sector = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return ((sector * 60 % 360 + 360) % 360, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - chroma / 2;
        var (r, g, b) = ((int)(hue / 60) % 6) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        byte Channel(double value) => (byte)Math.Round(Math.Clamp(value + m, 0, 1) * 255, MidpointRounding.AwayFromZero);
        return Color.FromRgb(Channel(r), Channel(g), Channel(b));
    }
}