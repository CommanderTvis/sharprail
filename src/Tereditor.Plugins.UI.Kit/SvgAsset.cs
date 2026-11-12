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
    private static readonly Dictionary<(byte[] Bytes, Color? Tint), SharedSource> Sources = [];
    private (byte[] Bytes, Color? Tint)? sourceKey;

    private sealed class SharedSource(SvgSource? source)
    {
        internal SvgSource? Source { get; } = source;
        internal SvgImage? Image { get; } = source is null ? null : new SvgImage { Source = source };
        internal int Users { get; set; }
    }

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
        var key = (svg, color?.Color);
        if (sourceKey == key) return;
        Release();
        if (!Sources.TryGetValue(key, out var shared))
            Sources.Add(key, shared = new SharedSource(Parse()));
        shared.Users++;
        sourceKey = key;
        Source = shared.Image;
    }

    private SvgSource? Parse()
    {
        try
        {
            var bytes = svg;
            if (color is { Color: var tint })
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(svg).Replace("currentColor", $"#{tint.R:x2}{tint.G:x2}{tint.B:x2}", StringComparison.Ordinal));
            using var stream = new MemoryStream(bytes);
            return SvgSource.LoadFromStream(stream);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private void Release()
    {
        Source = null;
        if (sourceKey is not { } key) return;
        sourceKey = null;
        var shared = Sources[key];
        if (--shared.Users == 0)
        {
            Sources.Remove(key);
            shared.Source?.Dispose();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Ui.ThemeChanged -= Load;
        Release();
    }
}