using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace SharpRail.UI.Plugins;

internal sealed partial class AssetIcon : Grid
{
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
        var image = new SvgAsset(bytes, size, color);
        image.PropertyChanged += (_, change) =>
        {
            if (change.Property == Avalonia.Controls.Image.SourceProperty) fallback.IsVisible = image.Source is null;
        };
        this.FindControl<ContentControl>("Image")!.Content = image;
    }
}