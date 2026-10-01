using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>One bundled <c>*.theme.json</c> manifest: the reference's schema-version-2 palette.</summary>
public sealed record ThemeManifest(string Id, string Label, int Order, string Appearance, string Contrast,
    IReadOnlyDictionary<string, Color?> Colors, IReadOnlyList<Color> Ansi)
{
    public bool IsLight => Appearance == "light";
    public bool IsHighContrast => Contrast == "high";
    public Color this[string key] => Colors[key] ?? throw new KeyNotFoundException(key);
}