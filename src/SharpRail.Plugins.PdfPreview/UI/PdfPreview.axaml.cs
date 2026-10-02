using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Visualization;

namespace SharpRail.Plugins.PdfPreview.UI;

/// <summary>
/// A PDF file's pages, rasterized by PDFium at the zoom they are shown at so text stays sharp, with a selectable text
/// layer over each page, its own toolbar, pinch and Command-wheel zoom, and a reload whenever the file's bytes change.
/// </summary>
internal sealed partial class PdfPreview : UserControl
{
    // How long the scale has to hold still before the pages are drawn again at it.
    private static readonly TimeSpan RasterSettle = TimeSpan.FromMilliseconds(120);
    private readonly IPluginUIContext context;
    private readonly FileViewerProps props;
    private readonly TextBlock pageCount, zoomLevel, loadingText, errorText;
    private readonly StackPanel pages, error;
    private readonly Border toolbar;
    private readonly ScrollViewer scroll;
    private readonly DispatcherTimer settle;
    private readonly List<(PdfPage Page, Border Box, Image Image)> shown = [];
    private CancellationTokenSource? loading;
    private IDisposable? revisionWatch;
    private byte[]? bytes;
    private double scale = 1;
    private int renders;

    public PdfPreview(IPluginUIContext context, FileViewerProps props)
    {
        this.context = context;
        this.props = props;
        AvaloniaXamlLoader.Load(this);
        pageCount = this.FindControl<TextBlock>("PdfPageCount")!;
        zoomLevel = this.FindControl<TextBlock>("PdfZoomLevel")!;
        loadingText = this.FindControl<TextBlock>("PdfLoading")!;
        errorText = this.FindControl<TextBlock>("PdfErrorText")!;
        pages = this.FindControl<StackPanel>("PdfPages")!;
        error = this.FindControl<StackPanel>("PdfPreviewError")!;
        toolbar = this.FindControl<Border>("PdfToolbar")!;
        scroll = this.FindControl<ScrollViewer>("PdfPreviewScroll")!;
        WireAction("PdfReload", "refresh", Load);
        WireAction("PdfZoomOut", "subtract", () => ZoomTo(scale / ZoomGesture.ScaleStep));
        WireAction("PdfZoomIn", "add", () => ZoomTo(scale * ZoomGesture.ScaleStep));
        WireAction("PdfZoomReset", "arrowGoBack", () => ZoomTo(1));
        this.FindControl<Button>("PdfRetry")!.Click += (_, _) => Load();
        settle = new DispatcherTimer { Interval = RasterSettle };
        settle.Tick += (_, _) => { settle.Stop(); _ = RasterizeAsync(); };
        // Tunnelled, so the zoom is taken before the scroller treats the same wheel as a scroll.
        scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!ZoomGesture.IsZoom(e.KeyModifiers)) return;
            e.Handled = true;
            ZoomTo(ZoomGesture.ForWheel(scale, e.Delta.Y));
        }, RoutingStrategies.Tunnel);
        scroll.AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            e.Handled = true;
            ZoomTo(scale * (1 + e.Delta.X));
        }, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            // A compiler rewriting the file is the reason this pane exists: the file's own revision, not the
            // workspace's, so a build writing a dozen other files beside it does not redraw an unchanged document.
            revisionWatch ??= context.ObserveFileRevision(props.WorkspaceId, props.Path, _ => Load());
            if (bytes is null && (loading is null || loading.IsCancellationRequested)) Load();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            loading?.Cancel();
            revisionWatch?.Dispose();
            revisionWatch = null;
            settle.Stop();
            bytes = null;
            ClearPages();
        };
    }

    /// <summary>How many times the pages have been rasterized, for the checks.</summary>
    internal int Renders => renders;
    internal double Scale => scale;

    private void WireAction(string name, string icon, Action action)
    {
        var button = this.FindControl<Button>(name)!;
        button.Content = Ui.Icon(icon);
        button.Click += (_, _) => action();
    }

    private void ZoomTo(double next)
    {
        next = ZoomGesture.Clamp(next);
        if (next == scale) return;
        scale = next;
        zoomLevel.Text = $"{Math.Round(scale * 100)}%";
        // The pixels drawn last are stretched at once; the sharp ones arrive when the scale stops moving.
        foreach (var (page, box, _) in shown) (box.Width, box.Height) = (page.Width * scale, page.Height * scale);
        settle.Stop();
        settle.Start();
    }

    private async void Load()
    {
        loading?.Cancel();
        var cancel = loading = new CancellationTokenSource();
        error.IsVisible = false;
        toolbar.IsVisible = true;
        loadingText.IsVisible = shown.Count == 0;
        try
        {
            var read = await context.ReadFileAsync(props.WorkspaceId, props.Path, cancel.Token);
            var parsed = await Task.Run(() => PdfEngine.Read(read), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            bytes = read;
            Show(parsed);
            await RasterizeAsync();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (DllNotFoundException) { Fail("PDF rendering is not available on this platform."); }
        catch (Exception failure) { Fail(failure.Message); }
        finally
        {
            if (ReferenceEquals(loading, cancel)) loading = null;
            cancel.Dispose();
        }

        void Fail(string message)
        {
            if (cancel.IsCancellationRequested) return;
            errorText.Text = "This PDF could not be read: " + message;
            error.IsVisible = true;
            toolbar.IsVisible = false;
            loadingText.IsVisible = false;
            ClearPages();
        }
    }

    private void ClearPages()
    {
        foreach (var (_, _, image) in shown)
        {
            (image.Source as Bitmap)?.Dispose();
            image.Source = null;
        }
        pages.Children.Clear();
        shown.Clear();
    }

    private void Show(IReadOnlyList<PdfPage> parsed)
    {
        pageCount.Text = parsed.Count == 1 ? "1 page" : $"{parsed.Count} pages";
        loadingText.IsVisible = false;
        ClearPages();
        var selection = new PdfSelection();
        foreach (var page in parsed)
        {
            var image = new Image { Name = "PdfPage", Stretch = Stretch.Fill };
            var surface = new Grid();
            surface.Children.Add(image);
            surface.Children.Add(new PdfTextLayer(page, selection));
            var box = new Border
            {
                Width = page.Width * scale,
                Height = page.Height * scale,
                Background = Brushes.White,
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = surface
            };
            pages.Children.Add(box);
            shown.Add((page, box, image));
        }
    }

    private async Task RasterizeAsync()
    {
        if (bytes is not { } source || shown.Count == 0) return;
        var target = scale;
        var density = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var rasters = await Task.Run(() => PdfEngine.Render(source, target * density));
        // A newer read or zoom has superseded this one.
        if (!ReferenceEquals(source, bytes) || target != scale || rasters.Count != shown.Count) return;
        for (var index = 0; index < rasters.Count; index++)
        {
            var raster = rasters[index];
            var handle = GCHandle.Alloc(raster.Pixels, GCHandleType.Pinned);
            try
            {
                var previous = shown[index].Image.Source as Bitmap;
                shown[index].Image.Source = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(),
                    new PixelSize(raster.Width, raster.Height), new Vector(96 * density, 96 * density), raster.Stride);
                previous?.Dispose();
            }
            finally { handle.Free(); }
        }
        renders++;
    }
}