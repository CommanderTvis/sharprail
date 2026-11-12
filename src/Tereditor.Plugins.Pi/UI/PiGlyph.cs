using System.Text;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Pi.UI;

/// <summary>
/// Pi's mark, from https://pi.dev/press-kit (the square badge), with its fill taken from the theme. It is the same
/// drawing as <c>assets/pi.svg</c>, held here because a host without a plugin directory (a browser tab's) has no asset
/// file to read it from.
/// </summary>
internal static class PiGlyph
{
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes(
        """<svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 560 560"><path fill="currentColor" d="M420 280H280V140H0V0H420V280Z"/><path fill="currentColor" d="M560 560H420V280H560V560Z"/><path fill="currentColor" d="M140 560H0V140H140V280H280V420H140V560Z"/></svg>""");

    public static Control Create(double size = 16, IBrush? color = null) =>
        new ContentControl { Name = "PiGlyph", Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Content = new SvgAsset(Svg, size, color ?? Ui.Muted) };
}