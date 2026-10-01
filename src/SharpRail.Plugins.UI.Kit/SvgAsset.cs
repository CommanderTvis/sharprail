using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// Draws SVG bytes, such as a plugin's <c>asset:</c> icon, at a square size while attached. An unreadable document
/// draws nothing.
/// </summary>
public sealed class SvgAsset : Image
{
    private readonly byte[] svg;
    private SvgSource? source;

    public SvgAsset(byte[] svg, double size = 16)
    {
        this.svg = svg;
        Width = size;
        Height = size;
        Stretch = Stretch.Uniform;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            using var stream = new MemoryStream(svg);
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
        Source = null;
        source?.Dispose();
        source = null;
    }
}
