using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace SharpRail.UI.Rendering;

internal sealed partial class AssetIcon : Grid
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G'];
    private readonly Control fallback;

    internal AssetIcon(string name, string fallbackGlyph, IBrush? color, double size)
    {
        AvaloniaXamlLoader.Load(this);
        Tag = name;
        Width = Height = size;
        fallback = Ui.Icon(fallbackGlyph, color, size);
        fallback.Tag = fallbackGlyph;
        this.FindControl<ContentControl>("Fallback")!.Content = fallback;
    }

    internal void Show(byte[] bytes, IBrush color, double size)
    {
        // A PNG (a language server's mark, say) draws as itself; anything else is an SVG tinted through currentColor.
        Image image = bytes.AsSpan().StartsWith(Png) ? new Image { Width = size, Height = size, Stretch = Stretch.Uniform, Source = Bitmap(bytes) } : new SvgAsset(bytes, size, color);
        image.PropertyChanged += (_, change) =>
        {
            if (change.Property == Avalonia.Controls.Image.SourceProperty) fallback.IsVisible = image.Source is null;
        };
        fallback.IsVisible = image.Source is null;
        this.FindControl<ContentControl>("Image")!.Content = image;
    }

    /// <summary>PNG decodes so far; a list that recycles icons must not decode a mark again for each card it realizes.</summary>
    internal static int Decodes;

    // A card is realized again each time it scrolls into view with the same bytes; the decoded mark lives as long as they do.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Avalonia.Media.Imaging.Bitmap?> Decoded = new();

    private static Avalonia.Media.Imaging.Bitmap? Bitmap(byte[] bytes)
    {
        if (Decoded.TryGetValue(bytes, out var cached)) return cached;
        Interlocked.Increment(ref Decodes);
        Avalonia.Media.Imaging.Bitmap? bitmap;
        try { using var stream = new MemoryStream(bytes); bitmap = new Avalonia.Media.Imaging.Bitmap(stream); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { bitmap = null; }
        Decoded.AddOrUpdate(bytes, bitmap);
        return bitmap;
    }
}