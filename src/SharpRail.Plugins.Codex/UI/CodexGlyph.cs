using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// The Codex mark from this plugin's own <c>codex.svg</c>, drawn in a theme brush (the asset paints
/// <c>currentColor</c>), and redrawn when the theme changes; OpenAI's mark stands in until the asset is read or when
/// it cannot be. GPT models wear OpenAI's mark (<see cref="Gpt"/>), which Codex's own is not.
/// </summary>
public sealed class CodexGlyph : ContentControl
{
    private static Task<byte[]?>? asset;
    private readonly double size;
    private readonly ISolidColorBrush brush;

    public CodexGlyph(IPluginUIContext context, double size = 16, ISolidColorBrush? brush = null)
    {
        this.size = size;
        this.brush = brush ?? Ui.Muted;
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        asset ??= Read(context);
        Content = Gpt(size, this.brush);
        _ = Draw();
    }

    private static async Task<byte[]?> Read(IPluginUIContext context)
    {
        try { return await context.ReadAssetAsync("codex.svg"); }
        catch (Exception) { return null; }
    }

    /// <summary>Forgets the cached asset, for a new activation.</summary>
    public static void Reset() => asset = null;

    public static Control Gpt(double size = 14, IBrush? brush = null) => Ui.Icon("openai", brush ?? Ui.Muted, size);

    private async Task Draw()
    {
        if (await asset! is not { } svg) return;
        var color = brush.Color;
        var hex = "#" + color.R.ToString("x2", CultureInfo.InvariantCulture) + color.G.ToString("x2", CultureInfo.InvariantCulture) + color.B.ToString("x2", CultureInfo.InvariantCulture);
        var painted = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(svg).Replace("currentColor", hex, StringComparison.Ordinal));
        Dispatcher.UIThread.Post(() => Content = new SvgAsset(painted, size));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Ui.ThemeChanged += Redraw;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Ui.ThemeChanged -= Redraw;
    }

    private void Redraw() => _ = Draw();
}