using System.Text;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Acp.UI;

/// <summary>The ACP mark, the same drawing as <c>assets/acp.svg</c>, held here because a host without a plugin directory has no asset file to read.</summary>
internal static class AcpGlyph
{
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes(
        """<svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24"><path stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" d="M8 7l-5 5 5 5M16 7l5 5-5 5M13.5 5l-3 14"/></svg>""");

    public static Control Create(double size = 16, IBrush? color = null) =>
        new ContentControl { Name = "AcpGlyph", Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Content = new SvgAsset(Svg, size, color ?? Ui.Muted) };
}