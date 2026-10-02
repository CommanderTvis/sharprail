using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// Draws SVG bytes, such as a plugin's <c>asset:</c> icon, at a square size while attached. An unreadable document
/// draws nothing. A glyph drawn in <c>currentColor</c> takes the given brush's colour and follows theme changes, the way
/// a monochrome icon follows its text colour.
/// </summary>
public sealed class SvgAsset : Image
{
    private readonly byte[] svg;
    private readonly ISolidColorBrush? color;
    private SvgSource? source;

    public SvgAsset(byte[] svg, double size) : this(svg, size, null) { }

    public SvgAsset(byte[] svg, double size = 16, IBrush? color = null)
    {
        this.svg = svg;
        this.color = color as ISolidColorBrush;
        Width = size;
        Height = size;
        Stretch = Stretch.Uniform;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (color is not null) Ui.ThemeChanged += Load;
        Load();
    }

    private void Load()
    {
        Source = null;
        source?.Dispose();
        try
        {
            var bytes = svg;
            if (color is { Color: var tint })
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(svg).Replace("currentColor", $"#{tint.R:x2}{tint.G:x2}{tint.B:x2}", StringComparison.Ordinal));
            using var stream = new MemoryStream(bytes);
            source = SvgSource.LoadFromStream(stream);
            Source = new SvgImage { Source = source };
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
        {
            source = null;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Ui.ThemeChanged -= Load;
        Source = null;
        source?.Dispose();
        source = null;
    }
}