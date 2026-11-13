using Avalonia.Platform;

using SkiaSharp;

namespace Tereditor.Plugins.UI.Kit.Editor;

/// <summary>
/// The code editors' font family, shared by every file editor, diff and Markdown source in this process. Empty is the
/// bundled JetBrains Mono; a family this machine does not have falls back to it, so layout never depends on a missing font.
/// </summary>
internal static class EditorFonts
{
    /// <summary>The bundled family's name, offered first in the font list.</summary>
    public const string Bundled = "JetBrains Mono";

    // Shared by every editor for the application's lifetime; system faces are process-wide and never disposed either.
    private static readonly Lazy<SKTypeface> BundledFace = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://Tereditor.Plugins.UI.Kit/Assets/Fonts/JetBrainsMono-Regular.ttf"));
        return SKTypeface.FromStream(stream);
    });
    private static readonly Dictionary<string, SKTypeface?> Faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chosen family as the host stores it; empty is the bundled font.</summary>
    public static string Family
    {
        get;
        set
        {
            value = (value ?? "").Trim();
            if (field == value) return;
            field = value;
            Changed?.Invoke();
        }
    } = "";

    /// <summary>Raised when <see cref="Family"/> changes; live editors switch typeface.</summary>
    public static event Action? Changed;

    /// <summary>The typeface editors draw with: the chosen family when this machine has it, otherwise the bundled font.</summary>
    public static SKTypeface Typeface => Resolve(Family) ?? BundledFace.Value;

    /// <summary>Whether <see cref="Family"/> resolved to the bundled font although another family was chosen.</summary>
    public static bool FellBack => Family.Length > 0 && Resolve(Family) is null;

    /// <summary>
    /// The installed face of <paramref name="family"/>, or null for the bundled font and for a family this machine lacks.
    /// Skia substitutes a default face for an unknown name, so a face only counts when its family name matches.
    /// </summary>
    public static SKTypeface? Resolve(string family)
    {
        family = family.Trim();
        if (family.Length == 0 || family.Equals(Bundled, StringComparison.OrdinalIgnoreCase)) return null;
        lock (Faces)
        {
            if (Faces.TryGetValue(family, out var cached)) return cached;
            var face = SKTypeface.FromFamilyName(family);
            if (face is not null && !face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase)) face = null;
            Faces[family] = face;
            return face;
        }
    }

    private static readonly Lazy<IReadOnlyList<string>> MonospaceFamilies = new(ListMonospace);

    /// <summary>
    /// The installed fixed-pitch families, sorted, after the bundled font. The first call loads a face per family, so
    /// callers run it off the UI thread; later calls reuse that list.
    /// </summary>
    public static IReadOnlyList<string> Monospace() => MonospaceFamilies.Value;

    private static IReadOnlyList<string> ListMonospace()
    {
        var families = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in SKFontManager.Default.FontFamilies)
            {
                if (family.Length == 0 || family.StartsWith('.') || family.Equals(Bundled, StringComparison.OrdinalIgnoreCase)) continue;
                // Skia shares one wrapper per native face, so a matched face is never disposed (another editor may draw with it).
                var face = SKFontManager.Default.MatchFamily(family);
                if (face is { IsFixedPitch: true }) families.Add(family);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Console.Error.WriteLine("Font list failed: " + error.Message); }
        return [Bundled, .. families];
    }

    /// <summary>The width of one column in the editor typeface relative to the bundled font's, for line widths counted in symbols.</summary>
    public static double AdvanceRatio
    {
        get
        {
            var face = Typeface;
            if (face == BundledFace.Value) return 1;
            using var chosen = new SKFont(face, 13);
            using var bundled = new SKFont(BundledFace.Value, 13);
            var reference = bundled.MeasureText("0");
            return reference > 0 && chosen.MeasureText("0") is var width and > 0 ? width / reference : 1;
        }
    }
}