using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Omp.UI;

/// <summary>
/// The omp mark from this plugin's own <c>omp.svg</c>, drawn in a theme brush (the asset paints <c>currentColor</c>)
/// and redrawn when the theme changes; the kit's terminal glyph stands in until the asset is read or when it cannot be.
/// Tagged with <see cref="OmpManifest.Icon"/> like the host's tab marks.
/// </summary>
public sealed class OmpGlyph : ContentControl
{
    private const int Attempts = 6;
    private static Task<byte[]?>? asset;
    private readonly IPluginUIContext context;
    private readonly double size;
    private readonly ISolidColorBrush brush;

    public OmpGlyph(IPluginUIContext context, double size = 16, ISolidColorBrush? brush = null)
    {
        this.context = context;
        this.size = size;
        this.brush = brush ?? Ui.Muted;
        Width = Height = size;
        Tag = OmpManifest.Icon;
        VerticalAlignment = VerticalAlignment.Center;
        Content = Ui.Icon("terminal", this.brush, size);
        _ = Draw();
    }

    // A failed read is not kept: the host half may not be serving assets yet when the first glyph is built, and a
    // remembered failure would leave the terminal glyph on every launcher icon until restart.
    private static async Task<byte[]?> Read(IPluginUIContext context)
    {
        try { return await context.ReadAssetAsync("omp.svg"); }
        catch (Exception) { return null; }
    }

    private static async Task<byte[]?> Load(IPluginUIContext context)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var read = asset ??= Read(context);
            if (await read is { } svg) return svg;
            if (ReferenceEquals(asset, read)) asset = null;
            await Task.Delay(TimeSpan.FromSeconds(attempt + 1));
        }
        return null;
    }

    /// <summary>Forgets the cached asset, for a new activation.</summary>
    public static void Reset() => asset = null;

    private async Task Draw()
    {
        if (await Load(context) is not { } svg) return;
        Dispatcher.UIThread.Post(() => Content = new SvgAsset(svg, size, brush));
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