using System.Globalization;
using System.Text.Json;

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>The theme a preference resolves to; <paramref name="Fallback"/> marks an unavailable or wrong-appearance request.</summary>
public sealed record ThemeResolution(string RequestedId, ThemeManifest Theme, bool Fallback, string? SystemAppearance);

/// <summary>The fixed catalogue of bundled manifests. Adding a theme means adding one Assets/Themes file.</summary>
public static class Themes
{
    public const string DefaultId = "dark";
    private static readonly string[] ColorKeys =
    [
        "accent", "accentHover", "accentSolid", "onAccent", "bubbleAccent", "background", "header", "content", "sidebar",
        "input", "elevated", "hover", "border", "borderStrong", "text", "muted", "hint", "selection", "selectionForeground",
        "editorSelection", "editorSelectionForeground", "info", "success", "danger", "warning"
    ];
    private static readonly string[] NullableKeys = ["selectionForeground", "editorSelectionForeground"];
    private static readonly string[] AnsiKeys =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white", "brightBlack", "brightRed", "brightGreen",
        "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite"
    ];

    public static IReadOnlyList<ThemeManifest> All { get; } = Load();

    /// <summary>The operating system's appearance as the application inherits it; an unreported appearance reads as light.</summary>
    public static string SystemAppearance => Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";

    public static ThemeManifest? Exact(string id) => All.FirstOrDefault(theme => theme.Id == id);

    public static ThemeManifest Resolve(string id) => Exact(id) ?? Exact(DefaultId)!;

    /// <summary>The lowest-order theme of an appearance, preferring the given contrast, then normal contrast.</summary>
    public static ThemeManifest Fallback(string appearance, string? contrast = null)
    {
        var themes = All.Where(theme => theme.Appearance == appearance).ToArray();
        return themes.FirstOrDefault(theme => theme.Contrast == contrast) ??
            themes.FirstOrDefault(theme => theme.Contrast == "normal") ?? themes[0];
    }

    /// <summary>The pair first shown by system mode: the fixed theme fills its own slot, the other matches its contrast.</summary>
    public static SystemThemePair DerivePair(string id)
    {
        var fixedTheme = Resolve(id);
        var pair = new SystemThemePair
        {
            Light = Fallback("light", fixedTheme.Contrast).Id,
            Dark = Fallback("dark", fixedTheme.Contrast).Id
        };
        if (fixedTheme.IsLight) pair.Light = fixedTheme.Id; else pair.Dark = fixedTheme.Id;
        return pair;
    }

    public static ThemeResolution Resolve(Preferences preferences, string systemAppearance)
    {
        if (preferences.ThemeMode != "system" || preferences.SystemThemePair is not { } pair)
        {
            var exact = Exact(preferences.Theme);
            return new(preferences.Theme, exact ?? Resolve(preferences.Theme), exact is null, null);
        }
        var requested = systemAppearance == "light" ? pair.Light : pair.Dark;
        var match = Exact(requested);
        return match?.Appearance == systemAppearance
            ? new(requested, match, false, systemAppearance)
            : new(requested, Fallback(systemAppearance), true, systemAppearance);
    }

    private static List<ThemeManifest> Load()
    {
        var folder = new Uri("avares://SharpRail.UI/Assets/Themes/");
        var themes = new List<ThemeManifest>();
        foreach (var uri in AssetLoader.GetAssets(folder, null).Where(uri => uri.AbsolutePath.EndsWith(".theme.json", StringComparison.Ordinal)))
        {
            using var stream = AssetLoader.Open(uri);
            using var json = JsonDocument.Parse(stream);
            themes.Add(Parse(json.RootElement, uri.AbsolutePath));
        }
        if (themes.Select(theme => theme.Id).Distinct().Count() != themes.Count) throw new InvalidDataException("Bundled theme ids must be unique.");
        if (themes.All(theme => theme.Id != DefaultId)) throw new InvalidDataException($"The bundled default theme is missing: {DefaultId}");
        if (!themes.Any(theme => theme.IsLight) || themes.All(theme => theme.IsLight))
            throw new InvalidDataException("The bundled theme catalogue needs at least one light and one dark theme.");
        return [.. themes.OrderBy(theme => theme.Order).ThenBy(theme => theme.Label, StringComparer.Ordinal).ThenBy(theme => theme.Id, StringComparer.Ordinal)];
    }

    private static ThemeManifest Parse(JsonElement root, string source)
    {
        string Text(string name) => root.GetProperty(name).GetString() is { Length: > 0 } value ? value
            : throw new InvalidDataException($"{source}: {name} must be a non-empty string.");
        if (root.GetProperty("schemaVersion").GetInt32() != 2) throw new InvalidDataException($"{source}: unsupported schema version.");
        var appearance = Text("appearance");
        var contrast = Text("contrast");
        if (appearance is not ("light" or "dark") || contrast is not ("normal" or "high"))
            throw new InvalidDataException($"{source}: invalid appearance or contrast.");
        var colors = root.GetProperty("colors");
        var palette = ColorKeys.ToDictionary(key => key, key =>
        {
            var value = colors.GetProperty(key);
            if (value.ValueKind == JsonValueKind.Null && NullableKeys.Contains(key)) return (Color?)null;
            return Hex(value.GetString(), $"{source}: colors.{key}");
        });
        var ansi = root.GetProperty("ansi");
        return new(Text("id"), Text("label"), root.GetProperty("order").GetInt32(), appearance, contrast, palette,
            [.. AnsiKeys.Select(key => Hex(ansi.GetProperty(key).GetString(), $"{source}: ansi.{key}"))]);
    }

    /// <summary>Canonical #rrggbb or #rrggbbaa; Avalonia's own parser reads eight digits as #aarrggbb.</summary>
    private static Color Hex(string? value, string field)
    {
        if (value is not ['#', ..] || value.Length is not (7 or 9) ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
            throw new InvalidDataException($"{field} must be a six- or eight-digit hex colour.");
        if (value.Length == 7) number = number << 8 | 0xff;
        return Color.FromArgb((byte)number, (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8));
    }
}